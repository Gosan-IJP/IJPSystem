using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// 노즐 800개를 한 줄 막대로 그리고, 드래그로 선택/해제하는 요소.
    ///
    /// <para>
    /// <b>왜 필요한가</b>: 콤마 목록(<c>1,2,3,…,100</c>)만으로는 800개 중 무엇을 쓰는지 눈으로
    /// 읽을 수 없다. "437번이 막혀 빼야 한다" 같은 실제 작업도 번호를 타이핑해야만 된다.
    /// </para>
    /// <para>
    /// <b>한 줄인 이유</b>: 실제 헤드가 몇 열인지 아직 확정되지 않았다. 물리 배열을 흉내 내면
    /// 그 가정이 틀렸을 때 화면이 거짓말을 한다. 번호 순 한 줄은 어떤 배열이든 참이다.
    /// </para>
    /// <para>
    /// 텍스트는 WPF FormattedText(DirectWrite) — 제어 PC 글꼴 문제와 무관하다.
    /// [[project-control-pc-skia-font]]
    /// </para>
    /// </summary>
    public sealed class NozzleStrip : FrameworkElement
    {
        private const double BarHeight   = 46;
        private const double RulerHeight = 16;
        private const double FontSize    = 10;
        private const double MinTickGap  = 58;

        private static readonly Brush Off      = Frozen(0x1E, 0x29, 0x3B);
        private static readonly Brush On       = Frozen(0x22, 0xC5, 0x5E);
        private static readonly Brush Preview  = Frozen(0x38, 0xBD, 0xF8);   // 입력 중 미리보기
        private static readonly Brush TextCol  = Frozen(0x94, 0xA3, 0xB8);
        private static readonly Pen   Border   = FrozenPen(0x47, 0x55, 0x69, 1.0);
        private static readonly Pen   TickPen  = FrozenPen(0x47, 0x55, 0x69, 1.0);

        private static Brush Frozen(byte r, byte g, byte b)
        { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        private static Pen FrozenPen(byte r, byte g, byte b, double t)
        { var p = new Pen(Frozen(r, g, b), t); p.Freeze(); return p; }

        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(NozzleStrip),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FirstNozzleProperty = Reg(nameof(FirstNozzle), 1);
        public static readonly DependencyProperty TotalNozzlesProperty = Reg(nameof(TotalNozzles), 800);
        public static readonly DependencyProperty SelectedProperty = Reg<IReadOnlyCollection<int>?>(nameof(Selected), null);
        public static readonly DependencyProperty PreviewSelectionProperty = Reg<IReadOnlyCollection<int>?>(nameof(PreviewSelection), null);

        public int FirstNozzle
        {
            get => (int)GetValue(FirstNozzleProperty);
            set => SetValue(FirstNozzleProperty, value);
        }

        public int TotalNozzles
        {
            get => (int)GetValue(TotalNozzlesProperty);
            set => SetValue(TotalNozzlesProperty, value);
        }

        /// <summary>현재 사용 노즐.</summary>
        public IReadOnlyCollection<int>? Selected
        {
            get => (IReadOnlyCollection<int>?)GetValue(SelectedProperty);
            set => SetValue(SelectedProperty, value);
        }

        /// <summary>입력창에 치는 중인 명령의 결과. 적용 전에 어디가 바뀔지 보여 준다.</summary>
        public IReadOnlyCollection<int>? PreviewSelection
        {
            get => (IReadOnlyCollection<int>?)GetValue(PreviewSelectionProperty);
            set => SetValue(PreviewSelectionProperty, value);
        }

        /// <summary>드래그로 구간을 칠했을 때. <paramref name="add"/> 가 false 면 해제다.</summary>
        public event EventHandler<(int From, int To, bool Add)>? RangeToggled;

        /// <summary>마우스가 가리키는 노즐 번호(없으면 null).</summary>
        public event EventHandler<int?>? Hovered;

        public NozzleStrip()
        {
            Cursor = Cursors.Hand;
            Height = BarHeight + RulerHeight;
        }

        // 드래그 상태 — 시작 시점에 "칠할지 지울지"를 정하고 끝까지 유지한다.
        // 매 셀마다 토글하면 드래그가 지나간 자리가 깜빡이며 뒤집힌다.
        private int _dragFrom = -1, _dragTo = -1;
        private bool _dragAdd;

        private Rect BarRect() => new(0, RulerHeight, Math.Max(1, ActualWidth), BarHeight);

        /// <summary>화면 X → 노즐 번호. 막대 밖이면 null.</summary>
        private int? NozzleAt(double x)
        {
            var bar = BarRect();
            int n = TotalNozzles;
            if (n <= 0 || bar.Width <= 0) return null;
            if (x < bar.Left || x > bar.Right) return null;

            int idx = (int)((x - bar.Left) / bar.Width * n);
            return FirstNozzle + Math.Clamp(idx, 0, n - 1);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            int n = TotalNozzles;
            if (w <= 0 || n <= 0) return;

            var bar = BarRect();
            dc.DrawRectangle(Off, null, bar);

            var sel = Selected as ISet<int> ?? (Selected == null ? null : new HashSet<int>(Selected));
            var pre = PreviewSelection as ISet<int> ?? (PreviewSelection == null ? null : new HashSet<int>(PreviewSelection));

            // 셀 하나가 1px 미만이면 칸마다 그리는 것이 무의미하다 → 인접한 같은 상태를 묶어
            // 한 번에 그린다. 800개를 800번 DrawRectangle 하지 않기 위함이기도 하다.
            double per = bar.Width / n;
            int i = 0;
            while (i < n)
            {
                var brush = BrushFor(FirstNozzle + i, sel, pre, out bool dragging);
                int j = i + 1;
                while (j < n && ReferenceEquals(BrushFor(FirstNozzle + j, sel, pre, out bool d2), brush) && d2 == dragging)
                    j++;

                if (brush != null)
                {
                    double x0 = bar.Left + i * per;
                    double x1 = bar.Left + j * per;
                    dc.DrawRectangle(brush, null, new Rect(x0, bar.Top, Math.Max(1, x1 - x0), bar.Height));
                }
                i = j;
            }

            dc.DrawRectangle(null, Border, bar);
            DrawRuler(dc, bar, n);
        }

        /// <summary>그 노즐을 어떤 색으로 칠할지. null 이면 안 칠함(꺼짐 배경 그대로).</summary>
        private Brush? BrushFor(int nozzle, ISet<int>? sel, ISet<int>? pre, out bool dragging)
        {
            dragging = _dragFrom >= 0 && nozzle >= Math.Min(_dragFrom, _dragTo) && nozzle <= Math.Max(_dragFrom, _dragTo);
            if (dragging) return _dragAdd ? On : null;          // 드래그 중에는 결과를 미리 보여 준다
            if (pre != null) return pre.Contains(nozzle) ? Preview : null;
            return sel != null && sel.Contains(nozzle) ? On : null;
        }

        private void DrawRuler(DrawingContext dc, Rect bar, int n)
        {
            int last = FirstNozzle + n - 1;
            int divisions = Math.Max(1, (int)(bar.Width / MinTickGap));
            int stepRaw = Math.Max(1, n / divisions);
            int step = NiceStep(stepRaw);

            for (int v = FirstNozzle; v <= last; v += step)
            {
                double x = bar.Left + (v - FirstNozzle) / (double)n * bar.Width;
                dc.DrawLine(TickPen, new Point(x, bar.Top - 3), new Point(x, bar.Top));

                var ft = new FormattedText(v.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), FontSize, TextCol, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(Math.Min(x, bar.Right - ft.Width), 0));
            }
        }

        /// <summary>눈금 간격을 1·2·5×10ⁿ 으로 — 137 같은 값이 눈금에 서면 읽히지 않는다.</summary>
        private static int NiceStep(int raw)
        {
            int mag = 1;
            while (mag * 10 <= raw) mag *= 10;
            int norm = raw / mag;
            return (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var at = NozzleAt(e.GetPosition(this).X);
            if (at == null) return;

            // 시작점이 이미 선택돼 있으면 이 드래그는 "해제" — 같은 곳을 다시 긁으면 지워지는
            // 것이 손에 맞는다(별도 모드 전환 없이).
            var sel = Selected;
            _dragAdd  = !(sel != null && sel.Contains(at.Value));
            _dragFrom = _dragTo = at.Value;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var at = NozzleAt(e.GetPosition(this).X);
            Hovered?.Invoke(this, at);

            if (_dragFrom < 0 || at == null) return;
            if (at.Value == _dragTo) return;
            _dragTo = at.Value;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_dragFrom < 0) return;

            int from = Math.Min(_dragFrom, _dragTo), to = Math.Max(_dragFrom, _dragTo);
            bool add = _dragAdd;
            _dragFrom = _dragTo = -1;
            if (IsMouseCaptured) ReleaseMouseCapture();

            RangeToggled?.Invoke(this, (from, to, add));
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Hovered?.Invoke(this, null);
        }

        protected override Size MeasureOverride(Size availableSize)
            => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
                   BarHeight + RulerHeight);
    }
}
