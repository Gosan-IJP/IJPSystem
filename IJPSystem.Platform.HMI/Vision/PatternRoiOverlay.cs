using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Vision
{
    /// <summary>
    /// 카메라 이미지 위에서 <b>패턴 영역(ROI)을 드래그로 고르고</b>, 찾은 자리를 표시하는 덮개.
    ///
    /// <para>좌표는 <see cref="CrossLineOverlay"/> 와 같은 규약 — 패널이 아니라
    /// <b>이미지에 대한 비율</b>(0~1)이다. Stretch=Uniform 이라 레터박스가 생기면
    /// 패널 좌표와 이미지 좌표가 어긋나는데, 그 상태로 잘라 낸 패턴은 엉뚱한 그림이 된다.</para>
    ///
    /// <para><see cref="IsEditing"/> 이 false 면 마우스를 받지 않는다 —
    /// 조그 중에 화면을 눌렀다고 등록 영역이 바뀌면 안 된다.</para>
    /// </summary>
    public sealed class PatternRoiOverlay : FrameworkElement
    {
        private static readonly Pen RoiPen    = MakePen(0x3B, 0x82, 0xF6, 1.4);
        private static readonly Pen ResultPen = MakePen(0x22, 0xC5, 0x5E, 1.6);
        private static readonly Pen FailPen   = MakePen(0xEF, 0x44, 0x44, 1.6);

        private static readonly Brush RoiFill    = Frozen(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6));
        private static readonly Brush LabelText  = Frozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
        private static readonly Brush LabelBack  = Frozen(Color.FromArgb(0xC8, 0x0B, 0x0F, 0x1A));

        private const double LabelSize = 11;
        private const double LabelPad  = 4;
        private const double HandleLen = 10;   // 모서리 표식 — 얇은 사각형만으로는 경계가 잘 안 보인다

        private Point? _dragStart;

        private static Pen MakePen(byte r, byte g, byte b, double thickness)
        {
            var p = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), thickness);
            p.Freeze();
            return p;
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ── 의존 속성 ────────────────────────────────────────────────────

        /// <summary>ROI 는 <b>화면에서 그려서 VM 으로 올라가는</b> 값이라 양방향이 기본이다.</summary>
        private static DependencyProperty DragRatio(string name) =>
            DependencyProperty.Register(name, typeof(double), typeof(PatternRoiOverlay),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        /// <summary>
        /// 찾은 자리는 <b>VM 이 계산해 내려보내기만</b> 한다. 양방향으로 두면 읽기 전용 속성에
        /// 되돌려 쓰려다 화면이 뜨는 순간 예외가 난다(실장 확인 2026-08-21).
        /// </summary>
        private static DependencyProperty ReadRatio(string name) =>
            DependencyProperty.Register(name, typeof(double), typeof(PatternRoiOverlay),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RoiXProperty = DragRatio(nameof(RoiX));
        public static readonly DependencyProperty RoiYProperty = DragRatio(nameof(RoiY));
        public static readonly DependencyProperty RoiWProperty = DragRatio(nameof(RoiW));
        public static readonly DependencyProperty RoiHProperty = DragRatio(nameof(RoiH));

        public static readonly DependencyProperty ResultXProperty = ReadRatio(nameof(ResultX));
        public static readonly DependencyProperty ResultYProperty = ReadRatio(nameof(ResultY));
        public static readonly DependencyProperty ResultWProperty = ReadRatio(nameof(ResultW));
        public static readonly DependencyProperty ResultHProperty = ReadRatio(nameof(ResultH));

        private static DependencyProperty Flag(string name) =>
            DependencyProperty.Register(name, typeof(bool), typeof(PatternRoiOverlay),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsEditingProperty    = Flag(nameof(IsEditing));
        public static readonly DependencyProperty ShowResultProperty   = Flag(nameof(ShowResult));
        public static readonly DependencyProperty ResultFailedProperty = Flag(nameof(ResultFailed));

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(PatternRoiOverlay),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ResultLabelProperty =
            DependencyProperty.Register(nameof(ResultLabel), typeof(string), typeof(PatternRoiOverlay),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public double RoiX { get => (double)GetValue(RoiXProperty); set => SetValue(RoiXProperty, value); }
        public double RoiY { get => (double)GetValue(RoiYProperty); set => SetValue(RoiYProperty, value); }
        public double RoiW { get => (double)GetValue(RoiWProperty); set => SetValue(RoiWProperty, value); }
        public double RoiH { get => (double)GetValue(RoiHProperty); set => SetValue(RoiHProperty, value); }

        public double ResultX { get => (double)GetValue(ResultXProperty); set => SetValue(ResultXProperty, value); }
        public double ResultY { get => (double)GetValue(ResultYProperty); set => SetValue(ResultYProperty, value); }
        public double ResultW { get => (double)GetValue(ResultWProperty); set => SetValue(ResultWProperty, value); }
        public double ResultH { get => (double)GetValue(ResultHProperty); set => SetValue(ResultHProperty, value); }

        /// <summary>드래그로 영역을 고르는 중. 꺼져 있으면 마우스를 받지 않는다.</summary>
        public bool IsEditing    { get => (bool)GetValue(IsEditingProperty);    set => SetValue(IsEditingProperty, value); }
        public bool ShowResult   { get => (bool)GetValue(ShowResultProperty);   set => SetValue(ShowResultProperty, value); }

        /// <summary>못 찾았을 때. 초록 대신 빨간 테두리로 그린다.</summary>
        public bool ResultFailed { get => (bool)GetValue(ResultFailedProperty); set => SetValue(ResultFailedProperty, value); }

        public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

        public string ResultLabel { get => (string)GetValue(ResultLabelProperty); set => SetValue(ResultLabelProperty, value); }

        public PatternRoiOverlay() => Cursor = Cursors.Cross;

        /// <summary>Stretch=Uniform 으로 놓인 이미지가 실제로 차지하는 사각형.</summary>
        private Rect ImageRect()
        {
            double w = ActualWidth, h = ActualHeight;
            var src = Source;
            if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return new Rect(0, 0, w, h);

            double s = Math.Min(w / src.PixelWidth, h / src.PixelHeight);
            double dw = src.PixelWidth * s, dh = src.PixelHeight * s;
            return new Rect((w - dw) / 2, (h - dh) / 2, dw, dh);
        }

        private Rect ToPanel(Rect img, double x, double y, double w, double h)
            => new(img.X + x * img.Width, img.Y + y * img.Height, w * img.Width, h * img.Height);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // 편집 중일 때만 투명 판을 깐다 — 안 그리면 마우스를 못 받고, 늘 깔면
            // 아래의 크로스라인이 클릭을 못 받는다.
            if (IsEditing) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var img = ImageRect();

            if (RoiW > 0 && RoiH > 0)
            {
                var r = ToPanel(img, RoiX, RoiY, RoiW, RoiH);
                dc.DrawRectangle(RoiFill, RoiPen, r);
                DrawCorners(dc, r, RoiPen);
            }

            if (ShowResult && ResultW > 0 && ResultH > 0)
            {
                var pen = ResultFailed ? FailPen : ResultPen;
                var r = ToPanel(img, ResultX, ResultY, ResultW, ResultH);
                dc.DrawRectangle(null, pen, r);
                DrawCorners(dc, r, pen);

                if (!string.IsNullOrEmpty(ResultLabel)) DrawLabel(dc, r, w, h);
            }
        }

        private static void DrawCorners(DrawingContext dc, Rect r, Pen pen)
        {
            double len = Math.Min(HandleLen, Math.Min(r.Width, r.Height) / 3);
            if (len <= 1) return;

            foreach (var (x, y, sx, sy) in new[]
            {
                (r.Left,  r.Top,    1.0,  1.0),
                (r.Right, r.Top,   -1.0,  1.0),
                (r.Left,  r.Bottom,  1.0, -1.0),
                (r.Right, r.Bottom, -1.0, -1.0),
            })
            {
                dc.DrawLine(pen, new Point(x, y), new Point(x + len * sx, y));
                dc.DrawLine(pen, new Point(x, y), new Point(x, y + len * sy));
            }
        }

        private void DrawLabel(DrawingContext dc, Rect box, double w, double h)
        {
            // FormattedText(DirectWrite) — 제어 PC 글꼴 문제의 영향을 받지 않는다.
            var ft = new FormattedText(ResultLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       new Typeface("Consolas"), LabelSize, LabelText,
                                       VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double bw = ft.Width + LabelPad * 2, bh = ft.Height + LabelPad * 2;
            double bx = box.Left;
            double by = box.Top - bh - 4;

            // 위쪽에 자리가 없으면 상자 안쪽으로 접는다.
            if (by < 0) by = box.Top + 4;
            if (bx + bw > w) bx = Math.Max(0, w - bw);

            dc.DrawRoundedRectangle(LabelBack, null, new Rect(bx, by, bw, bh), 3, 3);
            dc.DrawText(ft, new Point(bx + LabelPad, by + LabelPad));
        }

        // ── 드래그로 영역 고르기 ─────────────────────────────────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!IsEditing) return;

            _dragStart = Clamp(e.GetPosition(this));
            CaptureMouse();

            // 새로 그리기 시작하면 이전 영역은 지운다 — 남아 있으면 어느 쪽이 지금 것인지 헷갈린다.
            RoiW = RoiH = 0;
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragStart is not { } start || !IsMouseCaptured) return;

            UpdateRoi(start, Clamp(e.GetPosition(this)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
            _dragStart = null;
        }

        /// <summary>이미지 밖(레터박스)으로 끌어도 이미지 안으로 잘라서 받는다.</summary>
        private Point Clamp(Point p)
        {
            var img = ImageRect();
            if (img.Width <= 0 || img.Height <= 0) return p;

            return new Point(Math.Clamp(p.X, img.X, img.X + img.Width),
                             Math.Clamp(p.Y, img.Y, img.Y + img.Height));
        }

        private void UpdateRoi(Point a, Point b)
        {
            var img = ImageRect();
            if (img.Width <= 0 || img.Height <= 0) return;

            double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
            double y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);

            RoiX = (x0 - img.X) / img.Width;
            RoiY = (y0 - img.Y) / img.Height;
            RoiW = (x1 - x0) / img.Width;
            RoiH = (y1 - y0) / img.Height;
        }
    }
}
