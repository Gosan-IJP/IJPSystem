using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// 노즐을 막대로 그리고, 드래그로 선택/해제하는 요소.
    ///
    /// <para>
    /// <b>왜 필요한가</b>: 콤마 목록(<c>1,2,3,…,100</c>)만으로는 800개 중 무엇을 쓰는지 눈으로
    /// 읽을 수 없다. "437번이 막혀 빼야 한다" 같은 실제 작업도 번호를 타이핑해야만 된다.
    /// </para>
    /// <para>
    /// <b>여러 줄</b>: 헤드 열 수(<see cref="Rows"/>)만큼 나눠 그린다. 800개를 한 줄에 넣으면
    /// 노즐 하나가 1픽셀도 안 돼 개별 조작이 불가능하다. 번호는 줄에 걸쳐 <b>연속</b>이다 —
    /// 2열이면 위가 1~400, 아래가 401~800. 줄을 넘겨 드래그하면 그 사이 번호가 전부 잡힌다.
    /// </para>
    /// <para>
    /// 텍스트는 WPF FormattedText(DirectWrite) — 제어 PC 글꼴 문제와 무관하다.
    /// [[project-control-pc-skia-font]]
    /// </para>
    /// </summary>
    public sealed class NozzleStrip : FrameworkElement
    {
        private const double DefaultBarHeight = 40;
        private const double RulerHeight      = 15;
        private const double RowGap           = 8;
        private const double FontSize         = 10;
        private const double MinTickGap       = 58;

        /// <summary>
        /// 기본 막대 높이일 때 줄 하나가 차지하는 세로 크기(눈금 + 막대 + 아래 간격).
        /// 창이 열리기 전에 높이를 잡아야 하므로 public — 열 수가 늘면 창도 그만큼 커져야 한다.
        /// </summary>
        public const double RowPitchPx = RulerHeight + DefaultBarHeight + RowGap;

        private static readonly Brush Preview  = Frozen(0x38, 0xBD, 0xF8);   // 입력 중 미리보기
        private static readonly Brush RowLabel = Frozen(0x64, 0x74, 0x8B);   // 밝은 배경/어두운 배경 둘 다 읽힘

        private static Brush Frozen(byte r, byte g, byte b)
        { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        private static Pen FrozenPen(byte r, byte g, byte b, double t)
        { var p = new Pen(Frozen(r, g, b), t); p.Freeze(); return p; }

        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(NozzleStrip),
                new FrameworkPropertyMetadata(def,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FirstNozzleProperty = Reg(nameof(FirstNozzle), 1);
        public static readonly DependencyProperty TotalNozzlesProperty = Reg(nameof(TotalNozzles), 800);
        public static readonly DependencyProperty RowsProperty = Reg(nameof(Rows), 2);
        public static readonly DependencyProperty SelectedProperty = Reg<IReadOnlyCollection<int>?>(nameof(Selected), null);
        public static readonly DependencyProperty PreviewSelectionProperty = Reg<IReadOnlyCollection<int>?>(nameof(PreviewSelection), null);

        // 색은 창마다 다르다 — 노즐 선택 창은 어두운 바탕, DXF 변환 창은 흰 바탕이다.
        // 여기 색을 박아 두면 흰 배경에서 눈금 글씨가 안 보인다.
        public static readonly DependencyProperty OffBrushProperty     = Reg<Brush>(nameof(OffBrush),     Frozen(0x1E, 0x29, 0x3B));
        public static readonly DependencyProperty OnBrushProperty      = Reg<Brush>(nameof(OnBrush),      Frozen(0x22, 0xC5, 0x5E));
        public static readonly DependencyProperty TextBrushProperty    = Reg<Brush>(nameof(TextBrush),    Frozen(0x94, 0xA3, 0xB8));
        public static readonly DependencyProperty OutlineBrushProperty = Reg<Brush>(nameof(OutlineBrush), Frozen(0x47, 0x55, 0x69));
        public static readonly DependencyProperty BarHeightProperty    = Reg(nameof(BarHeight), DefaultBarHeight);
        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(NozzleStrip),
                new PropertyMetadata(false, (d, e) =>
                    ((NozzleStrip)d).Cursor = (bool)e.NewValue ? Cursors.Arrow : Cursors.Hand));

        /// <summary>안 쓰는 노즐 바탕색.</summary>
        public Brush OffBrush     { get => (Brush)GetValue(OffBrushProperty);     set => SetValue(OffBrushProperty, value); }
        /// <summary>쓰는 노즐 색.</summary>
        public Brush OnBrush      { get => (Brush)GetValue(OnBrushProperty);      set => SetValue(OnBrushProperty, value); }
        /// <summary>눈금 숫자 색.</summary>
        public Brush TextBrush    { get => (Brush)GetValue(TextBrushProperty);    set => SetValue(TextBrushProperty, value); }
        /// <summary>막대 테두리·눈금선 색.</summary>
        public Brush OutlineBrush { get => (Brush)GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }

        /// <summary>막대 하나의 높이. 자리가 좁은 화면(DXF 변환)에서는 줄여 쓴다.</summary>
        public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

        /// <summary>표시 전용 — 드래그로 선택을 바꾸지 못하게 한다.</summary>
        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

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

        /// <summary>몇 줄로 나눠 그릴지. 헤드 열 수와 맞추면 화면이 실제 배열과 같아진다.</summary>
        public int Rows
        {
            get => (int)GetValue(RowsProperty);
            set => SetValue(RowsProperty, value);
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

        /// <summary>드래그로 구간을 칠했을 때. <paramref name="Add"/> 가 false 면 해제다.</summary>
        public event EventHandler<(int From, int To, bool Add)>? RangeToggled;

        /// <summary>마우스가 가리키는 노즐 번호(없으면 null).</summary>
        public event EventHandler<int?>? Hovered;

        public NozzleStrip() => Cursor = Cursors.Hand;

        // 드래그 상태 — 시작 시점에 "칠할지 지울지"를 정하고 끝까지 유지한다.
        // 매 셀마다 토글하면 드래그가 지나간 자리가 깜빡이며 뒤집힌다.
        private int _dragFrom = -1, _dragTo = -1;
        private bool _dragAdd;

        private int RowCount => Math.Max(1, Rows);

        /// <summary>한 줄에 들어가는 노즐 수. 나누어떨어지지 않으면 올림 — 뒤가 잘리지 않게.</summary>
        private int PerRow => (int)Math.Ceiling(Math.Max(1, TotalNozzles) / (double)RowCount);

        private double RowPitch => RulerHeight + BarHeight + RowGap;

        /// <summary>r번째 줄의 막대 사각형.</summary>
        private Rect BarRect(int r) =>
            new(0, r * RowPitch + RulerHeight, Math.Max(1, ActualWidth), BarHeight);

        /// <summary>화면 좌표 → 노즐 번호. 막대 밖이면 null.</summary>
        private int? NozzleAt(Point p)
        {
            int total = TotalNozzles, perRow = PerRow;
            if (total <= 0 || ActualWidth <= 0) return null;

            // 줄 사이 여백을 눌러도 가까운 줄로 붙인다 — 800개 막대에서 8px 틈을 정확히
            // 피해 누르라고 요구할 수 없다.
            int r = (int)Math.Floor(p.Y / RowPitch);
            r = Math.Clamp(r, 0, RowCount - 1);

            var bar = BarRect(r);
            if (p.X < bar.Left || p.X > bar.Right) return null;

            int idxInRow = (int)((p.X - bar.Left) / bar.Width * perRow);
            idxInRow = Math.Clamp(idxInRow, 0, perRow - 1);

            int idx = r * perRow + idxInRow;
            if (idx >= total) return null;              // 마지막 줄이 덜 찼을 때
            return FirstNozzle + idx;
        }

        protected override void OnRender(DrawingContext dc)
        {
            int total = TotalNozzles;
            if (ActualWidth <= 0 || total <= 0) return;

            var sel = AsSet(Selected);
            var pre = AsSet(PreviewSelection);
            int perRow = PerRow;
            var outline = OutlinePen();

            for (int r = 0; r < RowCount; r++)
            {
                int rowFirst = r * perRow;
                int rowCount = Math.Min(perRow, total - rowFirst);
                if (rowCount <= 0) break;

                var bar = BarRect(r);
                dc.DrawRectangle(OffBrush, null, bar);

                // 셀 하나가 1px 미만이면 칸마다 그리는 것이 무의미하다 → 인접한 같은 상태를 묶어
                // 한 번에 그린다. 400개를 400번 DrawRectangle 하지 않기 위함이기도 하다.
                double per = bar.Width / perRow;
                int i = 0;
                while (i < rowCount)
                {
                    var brush = BrushFor(FirstNozzle + rowFirst + i, sel, pre);
                    int j = i + 1;
                    while (j < rowCount && ReferenceEquals(BrushFor(FirstNozzle + rowFirst + j, sel, pre), brush))
                        j++;

                    if (brush != null)
                    {
                        double x0 = bar.Left + i * per;
                        double x1 = bar.Left + j * per;
                        dc.DrawRectangle(brush, null, new Rect(x0, bar.Top, Math.Max(1, x1 - x0), bar.Height));
                    }
                    i = j;
                }

                dc.DrawRectangle(null, outline, bar);
                DrawRuler(dc, bar, rowFirst, rowCount, perRow, outline);
            }
        }

        // 테두리·눈금선 펜은 브러시가 바뀔 때만 새로 만든다 — OnRender 는 드래그 중 매 프레임 돈다.
        private Brush? _penBrush;
        private Pen? _pen;
        private Pen OutlinePen()
        {
            var b = OutlineBrush;
            if (!ReferenceEquals(b, _penBrush) || _pen == null)
            {
                _penBrush = b;
                _pen = new Pen(b, 1.0);
                _pen.Freeze();
            }
            return _pen;
        }

        private static ISet<int>? AsSet(IReadOnlyCollection<int>? c) =>
            c == null ? null : c as ISet<int> ?? new HashSet<int>(c);

        /// <summary>그 노즐을 어떤 색으로 칠할지. null 이면 안 칠함(꺼짐 배경 그대로).</summary>
        private Brush? BrushFor(int nozzle, ISet<int>? sel, ISet<int>? pre)
        {
            if (_dragFrom >= 0 &&
                nozzle >= Math.Min(_dragFrom, _dragTo) && nozzle <= Math.Max(_dragFrom, _dragTo))
                return _dragAdd ? OnBrush : null;       // 드래그 중에는 결과를 미리 보여 준다

            if (pre != null) return pre.Contains(nozzle) ? Preview : null;
            return sel != null && sel.Contains(nozzle) ? OnBrush : null;
        }

        private void DrawRuler(DrawingContext dc, Rect bar, int rowFirst, int rowCount, int perRow, Pen tick)
        {
            int divisions = Math.Max(1, (int)(bar.Width / MinTickGap));
            int step = NiceStep(Math.Max(1, perRow / divisions));
            double y = bar.Top - RulerHeight;

            // 줄의 마지막 번호를 오른쪽 끝에 — 이 줄이 어디까지인지 한눈에 보이게.
            // 먼저 그려 자리를 확보한다: 눈금 숫자가 여기까지 밀려오면 "391400" 처럼 붙어 버린다.
            var last = Text((FirstNozzle + rowFirst + rowCount - 1).ToString(CultureInfo.InvariantCulture), RowLabel);
            dc.DrawText(last, new Point(Math.Max(0, bar.Right - last.Width), y));
            double tickLimit = bar.Right - last.Width - 6;

            // 줄의 첫 번호는 눈금이 어디서 시작하는지 알려 주므로 항상 적는다.
            for (int k = 0; k < rowCount; k += step)
            {
                int v = FirstNozzle + rowFirst + k;
                double x = bar.Left + k / (double)perRow * bar.Width;
                dc.DrawLine(tick, new Point(x, bar.Top - 3), new Point(x, bar.Top));

                var ft = Text(v.ToString(CultureInfo.InvariantCulture), TextBrush);
                if (k > 0 && x + ft.Width > tickLimit) continue;
                dc.DrawText(ft, new Point(Math.Max(0, x), y));
            }
        }

        private FormattedText Text(string s, Brush brush) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
            if (IsReadOnly) return;

            var at = NozzleAt(e.GetPosition(this));
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
            var at = NozzleAt(e.GetPosition(this));
            Hovered?.Invoke(this, at);

            if (_dragFrom < 0 || at == null || at.Value == _dragTo) return;
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
                   RowCount * RowPitch - RowGap);
    }
}
