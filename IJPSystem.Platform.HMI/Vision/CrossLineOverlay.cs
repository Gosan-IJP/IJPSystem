using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Vision
{
    /// <summary>
    /// 카메라 이미지 위에 겹쳐 그리는 <b>크로스라인(십자 기준선)</b>.
    /// 토글로 표시/숨김, 드래그로 이동, 중앙 정렬 — 글라스 모서리를 기준선에 맞춰 조그하는 용도다.
    ///
    /// <para>
    /// 위치는 <b>이미지에 대한 비율</b>(0~1)이다. 패널이 아니라 이미지 기준인 이유:
    /// Stretch=Uniform 이라 이미지가 레터박스로 놓이면 패널 비율과 이미지 위치가 어긋나,
    /// "선을 글라스 모서리에 맞췄다"는 값이 실제 이미지 어디인지 말할 수 없게 된다.
    /// 이미지 기준이면 해상도가 바뀌어도 같은 지점을 가리키고, 픽셀 좌표를 그대로 읽어줄 수 있다.
    /// (<see cref="Source"/> 가 없으면 패널 전체를 기준으로 삼는다 — 이미지 전에도 선은 보여야 한다)
    /// </para>
    /// <para>
    /// 별도 요소로 뺀 이유: 화면마다 Canvas + Line 두 개를 놓고 코드비하인드에서
    /// 크기 변화·드래그를 계산하면 화면 수만큼 같은 계산이 복제된다. 여기 한 곳에 두면
    /// 붙이는 쪽은 XAML 한 줄이다.
    /// </para>
    /// </summary>
    public sealed class CrossLineOverlay : FrameworkElement
    {
        // 패턴인쇄 화면과 같은 색(#FF3B30) — 두 화면의 기준선이 서로 다른 색이면
        // 같은 기능인지 알아보기 어렵다.
        private static readonly Pen LinePen = MakePen(0xFF, 0x3B, 0x30, 1.0);

        private static readonly Brush ReadoutText = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xAF));
        private static readonly Brush ReadoutBack = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0F, 0x1A));

        private const double HubRadius   = 7;    // 교차점 표식 — 선만 있으면 기준점이 어디인지 읽기 어렵다
        private const double ReadoutSize = 11;
        private const double ReadoutPad  = 4;
        private const double ReadoutGap  = 10;   // 교차점과 좌표판 사이 간격

        private static Pen MakePen(byte r, byte g, byte b, double thickness)
        {
            var p = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), thickness);
            p.Freeze();
            return p;
        }

        static CrossLineOverlay()
        {
            ReadoutText.Freeze();
            ReadoutBack.Freeze();
        }

        public static readonly DependencyProperty ShowCrossProperty =
            DependencyProperty.Register(nameof(ShowCross), typeof(bool), typeof(CrossLineOverlay),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// 아래에 깔린 이미지. 좌표 기준(Uniform 배치 사각형)과 픽셀 좌표 표시에만 쓰고
        /// 그리지는 않는다 — 그리는 것은 아래의 Image 컨트롤이다.
        /// </summary>
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(CrossLineOverlay),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        // 양방향 기본값 — 드래그로 옮긴 위치가 ViewModel 로 돌아가야 '중앙 정렬' 같은
        // 명령과 값이 어긋나지 않는다.
        private static DependencyProperty Ratio(string name) =>
            DependencyProperty.Register(name, typeof(double), typeof(CrossLineOverlay),
                new FrameworkPropertyMetadata(0.5,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty XRatioProperty = Ratio(nameof(XRatio));
        public static readonly DependencyProperty YRatioProperty = Ratio(nameof(YRatio));

        /// <summary>크로스라인 표시 여부. 숨기면 마우스도 받지 않는다(아래 요소를 가리지 않도록).</summary>
        public bool ShowCross
        {
            get => (bool)GetValue(ShowCrossProperty);
            set => SetValue(ShowCrossProperty, value);
        }

        public BitmapSource? Source
        {
            get => (BitmapSource?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        /// <summary>세로선의 가로 위치 — 이미지 폭에 대한 비율 [0~1].</summary>
        public double XRatio
        {
            get => (double)GetValue(XRatioProperty);
            set => SetValue(XRatioProperty, value);
        }

        /// <summary>가로선의 세로 위치 — 이미지 높이에 대한 비율 [0~1].</summary>
        public double YRatio
        {
            get => (double)GetValue(YRatioProperty);
            set => SetValue(YRatioProperty, value);
        }

        public CrossLineOverlay()
        {
            Cursor = Cursors.Cross;
        }

        /// <summary>
        /// Stretch=Uniform 으로 놓인 이미지가 실제로 차지하는 사각형.
        /// 이미지가 없으면 패널 전체.
        /// </summary>
        private Rect ImageRect()
        {
            double w = ActualWidth, h = ActualHeight;
            var src = Source;
            if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return new Rect(0, 0, w, h);

            double s = Math.Min(w / src.PixelWidth, h / src.PixelHeight);
            double dw = src.PixelWidth * s, dh = src.PixelHeight * s;
            return new Rect((w - dw) / 2, (h - dh) / 2, dw, dh);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (!ShowCross || w <= 0 || h <= 0) return;

            // 투명 판을 먼저 깐다 — 그리지 않은 FrameworkElement 는 마우스를 받지 못해
            // 드래그가 아예 동작하지 않는다. 숨김 상태에서는 이 판도 없으므로 클릭이 통과한다.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var img = ImageRect();
            double x = img.X + Math.Clamp(XRatio, 0, 1) * img.Width;
            double y = img.Y + Math.Clamp(YRatio, 0, 1) * img.Height;

            // 0.5 를 더해 픽셀 경계에 걸치게 한다 — 1px 선이 두 픽셀에 반씩 걸려 흐려지는 것을 막는다.
            x = Math.Floor(x) + 0.5;
            y = Math.Floor(y) + 0.5;

            // 선은 패널 끝까지 긋는다 — 레터박스 여백까지 이어져야 직선자 역할을 한다.
            dc.DrawLine(LinePen, new Point(x, 0), new Point(x, h));
            dc.DrawLine(LinePen, new Point(0, y), new Point(w, y));
            dc.DrawEllipse(null, LinePen, new Point(x, y), HubRadius, HubRadius);

            DrawReadout(dc, x, y, w, h);
        }

        /// <summary>교차점의 <b>이미지 픽셀</b> 좌표를 적는다 — 화면 비율만 보여주면 옮긴 위치를 기록할 수 없다.</summary>
        private void DrawReadout(DrawingContext dc, double x, double y, double w, double h)
        {
            var src = Source;
            if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return;

            string label = $"X {Math.Clamp(XRatio, 0, 1) * src.PixelWidth:F0} · " +
                           $"Y {Math.Clamp(YRatio, 0, 1) * src.PixelHeight:F0} px";

            // FormattedText(DirectWrite) 로 그린다 — LiveCharts(Skia) 와 달리 제어 PC 글꼴 문제의
            // 영향을 받지 않는다. [[project-control-pc-skia-font]]
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       new Typeface("Consolas"), ReadoutSize, ReadoutText,
                                       VisualTreeHelper.GetDpi(this).PixelsPerDip);

            // 기본은 교차점 오른쪽 아래. 가장자리에 붙으면 반대쪽으로 접어 화면 밖으로 나가지 않게 한다.
            double bw = ft.Width + ReadoutPad * 2, bh = ft.Height + ReadoutPad * 2;
            double bx = x + ReadoutGap, by = y + ReadoutGap;
            if (bx + bw > w) bx = x - ReadoutGap - bw;
            if (by + bh > h) by = y - ReadoutGap - bh;

            dc.DrawRoundedRectangle(ReadoutBack, null, new Rect(bx, by, bw, bh), 3, 3);
            dc.DrawText(ft, new Point(bx + ReadoutPad, by + ReadoutPad));
        }

        // ── 드래그로 이동 ─────────────────────────────────────────────────────
        // 누른 즉시 그 지점으로 옮긴다(클릭=이동). 화면이 좁을 때 선을 정확히 집어야 하는
        // 부담을 없애기 위함 — 패턴인쇄 화면의 Select 툴과 같은 규약이다.
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!ShowCross) return;
            CaptureMouse();
            MoveTo(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured) MoveTo(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        // 이미지 바깥(레터박스)을 눌러도 이미지 안으로 잘라서 받는다 — 좌표가 이미지 밖이면
        // 픽셀 표시가 이미지에 없는 값을 말하게 된다.
        private void MoveTo(Point p)
        {
            var img = ImageRect();
            if (img.Width <= 0 || img.Height <= 0) return;
            XRatio = Math.Clamp((p.X - img.X) / img.Width,  0, 1);
            YRatio = Math.Clamp((p.Y - img.Y) / img.Height, 0, 1);
        }
    }
}
