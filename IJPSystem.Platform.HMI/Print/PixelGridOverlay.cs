using System;
using System.Windows;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 미리보기 위에 얹는 <b>실척 격자</b> — 1mm 마다 가는 선, 5칸마다 진한 선.
    ///
    /// <para>
    /// 이미지와 같이 확대되지 <b>않는다</b>. 이미지 안에 넣고 같이 키우면 확대 배율만큼 선도
    /// 굵어져, 20배쯤에서는 격자가 그림을 덮어 버린다. 그래서 화면 좌표로 그리고,
    /// 간격·원점만 배율에 맞춰 받는다.
    /// </para>
    /// </summary>
    public sealed class PixelGridOverlay : FrameworkElement
    {
        private static DependencyProperty Reg(string name, double def = 0.0) =>
            DependencyProperty.Register(name, typeof(double), typeof(PixelGridOverlay),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PitchXProperty  = Reg(nameof(PitchX));
        public static readonly DependencyProperty PitchYProperty  = Reg(nameof(PitchY));
        public static readonly DependencyProperty OriginXProperty = Reg(nameof(OriginX));
        public static readonly DependencyProperty OriginYProperty = Reg(nameof(OriginY));
        public static readonly DependencyProperty AreaWidthProperty  = Reg(nameof(AreaWidth));
        public static readonly DependencyProperty AreaHeightProperty = Reg(nameof(AreaHeight));

        /// <summary>세로선 간격 [화면 px]. 이미지 1mm 를 화면에서 차지하는 길이.</summary>
        public double PitchX { get => (double)GetValue(PitchXProperty); set => SetValue(PitchXProperty, value); }
        public double PitchY { get => (double)GetValue(PitchYProperty); set => SetValue(PitchYProperty, value); }

        /// <summary>이미지 좌상단의 화면 좌표.</summary>
        public double OriginX { get => (double)GetValue(OriginXProperty); set => SetValue(OriginXProperty, value); }
        public double OriginY { get => (double)GetValue(OriginYProperty); set => SetValue(OriginYProperty, value); }

        /// <summary>이미지가 화면에서 차지하는 크기 — 격자를 이미지 밖으로 흘리지 않는다.</summary>
        public double AreaWidth  { get => (double)GetValue(AreaWidthProperty);  set => SetValue(AreaWidthProperty, value); }
        public double AreaHeight { get => (double)GetValue(AreaHeightProperty); set => SetValue(AreaHeightProperty, value); }

        /// <summary>몇 칸마다 진한 선을 그을지. 1mm 격자에서 5 = 5mm 마다.</summary>
        public int MajorEvery { get; set; } = 5;

        private static readonly Pen MinorPen = FrozenPen(0x40, 0x2F, 0x80, 0xED, 1.0);
        private static readonly Pen MajorPen = FrozenPen(0x99, 0x2F, 0x80, 0xED, 1.0);

        private static Pen FrozenPen(byte a, byte r, byte g, byte b, double thickness)
        {
            var p = new Pen(new SolidColorBrush(Color.FromArgb(a, r, g, b)), thickness);
            p.Freeze();
            return p;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = AreaWidth, h = AreaHeight;
            if (w <= 0 || h <= 0) return;

            // 2px 아래로 촘촘해지면 격자가 아니라 면이 된다 — 그릴수록 안 보인다.
            double px = PitchX, py = PitchY;
            bool drawX = px >= 2, drawY = py >= 2;
            if (!drawX && !drawY) return;

            int every = Math.Max(1, MajorEvery);
            double ox = OriginX, oy = OriginY;

            // 이미지 밖으로 새지 않게 자른다. 화면 밖까지 그리는 건 클립이 막아 준다.
            dc.PushClip(new RectangleGeometry(new Rect(ox, oy, w, h)));

            if (drawX)
                for (int i = 0; i * px <= w; i++)
                {
                    double x = Snap(ox + i * px);
                    dc.DrawLine(i % every == 0 ? MajorPen : MinorPen, new Point(x, oy), new Point(x, oy + h));
                }

            if (drawY)
                for (int j = 0; j * py <= h; j++)
                {
                    double y = Snap(oy + j * py);
                    dc.DrawLine(j % every == 0 ? MajorPen : MinorPen, new Point(ox, y), new Point(ox + w, y));
                }

            dc.Pop();
        }

        /// <summary>반 픽셀 격자에 맞춰 1px 선이 두 픽셀로 번지지 않게 한다.</summary>
        private static double Snap(double v) => Math.Round(v) + 0.5;
    }
}
