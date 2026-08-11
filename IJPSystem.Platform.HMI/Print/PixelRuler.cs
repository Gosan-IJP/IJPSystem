using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 편집 캔버스 위에 얹는 <b>픽셀 눈금</b>.
    ///
    /// <para>
    /// 화면 캔버스는 실제 이미지(예: 4724×4724px)를 760px 로 줄여 보여 준다. 그래서 "지금 그린
    /// 선이 몇 번째 픽셀에 있는가" 를 눈으로 알 수 없고, 상태바 좌표 하나에만 의존하게 된다.
    /// 이 눈금은 화면 크기가 아니라 <b>이미지 픽셀 번호</b>를 적는다 — 확대해도 숫자는 그대로다.
    /// </para>
    /// <para>
    /// 캔버스 <b>바깥</b>(형제 요소)에 두는 것이 중요하다. 캔버스 안에 넣으면 저장·래스터화가
    /// 눈금까지 같이 찍는다. 여기서는 <c>DrawCanvas</c> 만 렌더하므로 섞일 일이 없다.
    /// </para>
    /// </summary>
    public sealed class PixelRuler : FrameworkElement
    {
        private static DependencyProperty Reg(string name, double def) =>
            DependencyProperty.Register(name, typeof(double), typeof(PixelRuler),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PixelsXProperty = Reg(nameof(PixelsX), 0.0);
        public static readonly DependencyProperty PixelsYProperty = Reg(nameof(PixelsY), 0.0);

        /// <summary>이미지 가로 픽셀 수. 눈금에 적는 숫자의 기준.</summary>
        public double PixelsX { get => (double)GetValue(PixelsXProperty); set => SetValue(PixelsXProperty, value); }
        public double PixelsY { get => (double)GetValue(PixelsYProperty); set => SetValue(PixelsYProperty, value); }

        private static readonly Pen MinorPen = FrozenPen(0x33, 0x64, 0x74, 0x8B, 1.0);
        private static readonly Pen MajorPen = FrozenPen(0x88, 0x25, 0x63, 0xEB, 1.0);
        private static readonly Pen UnitPen  = FrozenPen(0x22, 0x64, 0x74, 0x8B, 1.0);   // 1픽셀 격자
        private static readonly Brush LabelBrush = Frozen(0xFF, 0x1E, 0x3A, 0x5F);
        private static readonly Brush LabelBack  = Frozen(0xCC, 0xFF, 0xFF, 0xFF);

        private static Pen FrozenPen(byte a, byte r, byte g, byte b, double t)
        {
            var p = new Pen(Frozen(a, r, g, b), t); p.Freeze(); return p;
        }
        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var s = new SolidColorBrush(Color.FromArgb(a, r, g, b)); s.Freeze(); return s;
        }

        /// <summary>눈금 간격 후보 [이미지 px]. 1·2·5 × 10ⁿ — 사람이 암산할 수 있는 수만 쓴다.</summary>
        private static double NiceStep(double minPixels)
        {
            if (minPixels <= 1) return 1;
            double pow = Math.Pow(10, Math.Floor(Math.Log10(minPixels)));
            foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 })
                if (pow * m >= minPixels) return pow * m;
            return pow * 10;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double nx = PixelsX, ny = PixelsY;
            if (w <= 0 || h <= 0 || nx <= 0 || ny <= 0) return;

            double sx = w / nx, sy = h / ny;        // 이미지 1픽셀이 화면에서 차지하는 길이
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

            // 충분히 크게 확대했으면 진짜 1픽셀 격자를 보여 준다 — 이게 "픽셀 눈금"의 본뜻이다.
            if (sx >= 5) for (double i = 0; i <= nx; i++) VLine(dc, UnitPen, i * sx, h);
            if (sy >= 5) for (double j = 0; j <= ny; j++) HLine(dc, UnitPen, j * sy, w);

            double majX = NiceStep(80 / Math.Max(1e-9, sx));
            double majY = NiceStep(80 / Math.Max(1e-9, sy));
            double minX = majX / 5, minY = majY / 5;

            if (minX * sx >= 6) for (double i = 0; i <= nx; i += minX) VLine(dc, MinorPen, i * sx, h);
            if (minY * sy >= 6) for (double j = 0; j <= ny; j += minY) HLine(dc, MinorPen, j * sy, w);

            for (double i = 0; i <= nx; i += majX)
            {
                double x = i * sx;
                VLine(dc, MajorPen, x, h);
                Label(dc, ((long)i).ToString(CultureInfo.InvariantCulture), x + 3, 2, dpi);
            }
            for (double j = 0; j <= ny; j += majY)
            {
                double y = j * sy;
                HLine(dc, MajorPen, y, w);
                if (j > 0) Label(dc, ((long)j).ToString(CultureInfo.InvariantCulture), 3, y + 2, dpi);
            }

            dc.Pop();
        }

        private static void VLine(DrawingContext dc, Pen p, double x, double h)
            => dc.DrawLine(p, new Point(Snap(x), 0), new Point(Snap(x), h));

        private static void HLine(DrawingContext dc, Pen p, double y, double w)
            => dc.DrawLine(p, new Point(0, Snap(y)), new Point(w, Snap(y)));

        private static void Label(DrawingContext dc, string text, double x, double y, double dpi)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 10, LabelBrush, dpi);
            // 선 위에 바로 쓰면 눈금과 겹쳐 안 읽힌다 — 뒤에 반투명 흰 바탕을 깐다.
            dc.DrawRectangle(LabelBack, null, new Rect(x - 1, y, ft.Width + 2, ft.Height));
            dc.DrawText(ft, new Point(x, y));
        }

        private static double Snap(double v) => Math.Round(v) + 0.5;
    }
}
