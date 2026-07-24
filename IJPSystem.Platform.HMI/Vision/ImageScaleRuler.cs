using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Vision
{
    /// <summary>
    /// 카메라 이미지와 µm 눈금자를 <b>함께</b> 그리는 요소.
    /// (LabVIEW "Sample DW.vi" 의 이미지 눈금과 같은 역할 — 단, 픽셀이 아니라 µm 로 표시해
    ///  Measure Start/END Position·Nozzle Pitch 등 화면 파라미터와 바로 대조할 수 있게 한다)
    ///
    /// <b>이미지를 직접 그리는 이유</b>: 별도 Image 컨트롤과 눈금을 겹쳐 두면 두 곳에서 배치를
    /// 계산하게 되어 어긋날 여지가 생긴다. 한 곳에서 같은 사각형으로 그리면 항상 정렬된다.
    ///
    /// <b>원점 고정</b>: 이미지를 가운데 정렬하지 않고 <b>좌상단에 고정</b>한다. 라이브에서 Delay Time 을
    /// 바꿔가며 액적 이동을 읽는 용도라, 이미지 크기·비율이 달라져도 (0,0) 이 화면에서 움직이면 안 된다.
    ///
    /// ※ 텍스트는 WPF FormattedText(DirectWrite) 로 그린다 — LiveCharts(Skia) 와 달리
    ///   제어 PC 글꼴 문제의 영향을 받지 않는다. [[project-control-pc-skia-font]]
    /// </summary>
    public sealed class ImageScaleRuler : FrameworkElement
    {
        /// <summary>눈금이 차지하는 왼쪽 폭[dip].</summary>
        public const double GutterLeft = 46;
        /// <summary>눈금이 차지하는 위쪽 높이[dip].</summary>
        public const double GutterTop = 18;

        private const double MinLabelSpacing = 64;   // 라벨이 겹치지 않는 최소 간격[dip]
        private const double TickLen = 7;            // 라벨 붙는 주 눈금
        private const double MinorTickLen = 3;       // 라벨 없는 보조 눈금

        private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        private static readonly Pen   TickPen   = MakePen(0x47, 0x55, 0x69, 1.0);
        private static readonly Pen   GridPen   = MakePen(0x33, 0x41, 0x55, 0.5);

        private static Pen MakePen(byte r, byte g, byte b, double thickness)
        {
            var p = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), thickness);
            p.Freeze();
            return p;
        }

        static ImageScaleRuler() => TextBrush.Freeze();

        public ImageScaleRuler()
        {
            // 축소 렌더 품질 — 기존 Image 의 RenderOptions.BitmapScalingMode=HighQuality 와 동일.
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(ImageScaleRuler),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>표시할 이미지. 눈금 범위도 이 이미지의 픽셀 크기에서 얻는다.</summary>
        public BitmapSource? Source
        {
            get => (BitmapSource?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public static readonly DependencyProperty MicronsPerPixelProperty =
            DependencyProperty.Register(nameof(MicronsPerPixel), typeof(double), typeof(ImageScaleRuler),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>교정 스케일[µm/px]. 0 이하면 눈금을 px 단위로 표시한다(교정 전).</summary>
        public double MicronsPerPixel
        {
            get => (double)GetValue(MicronsPerPixelProperty);
            set => SetValue(MicronsPerPixelProperty, value);
        }

        // 눈금·이미지 위치는 RenderSize 로 계산하므로, 크기가 바뀌면 다시 그려야 한다
        // (WPF 는 크기 변경만으로 OnRender 를 다시 호출하지 않는다 — 창 최대화 시 어긋나는 것을 막는다).
        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var src = Source;
            if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return;

            double areaW = RenderSize.Width  - GutterLeft;
            double areaH = RenderSize.Height - GutterTop;
            if (areaW <= 8 || areaH <= 8) return;

            // 종횡비 유지 축소(Uniform) + 좌상단 고정 — 가운데 정렬하지 않는다(원점 고정).
            double scale = Math.Min(areaW / src.PixelWidth, areaH / src.PixelHeight);
            double imgW = src.PixelWidth * scale, imgH = src.PixelHeight * scale;
            double x0 = GutterLeft, y0 = GutterTop;

            var imgRect = new Rect(x0, y0, imgW, imgH);
            dc.DrawImage(src, imgRect);

            // 교정 전(µm/px 미설정)에는 px 로 표시해 눈금이 거짓 물리량을 말하지 않게 한다.
            double umPerPx = MicronsPerPixel;
            bool inMicrons = umPerPx > 0 && !double.IsNaN(umPerPx) && !double.IsInfinity(umPerPx);
            if (!inMicrons) umPerPx = 1.0;

            double totalX = src.PixelWidth  * umPerPx;
            double totalY = src.PixelHeight * umPerPx;

            double stepX = NiceStep(totalX * (MinLabelSpacing / imgW));
            double stepY = NiceStep(totalY * (MinLabelSpacing / imgH));

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRectangle(null, TickPen, imgRect);   // 0 기준선이 어디인지 보이게

            // ── 보조 눈금(라벨 없음) ── 주 눈금 사이를 세분해 눈대중 읽기를 돕는다.
            foreach (double v in MinorTicks(totalX, stepX))
            {
                double x = x0 + v / totalX * imgW;
                dc.DrawLine(TickPen, new Point(x, y0 - MinorTickLen), new Point(x, y0));
            }
            foreach (double v in MinorTicks(totalY, stepY))
            {
                double y = y0 + v / totalY * imgH;
                dc.DrawLine(TickPen, new Point(x0 - MinorTickLen, y), new Point(x0, y));
            }

            // ── 상단(X) 눈금 ──
            // 끝 눈금(이미지 오른쪽 끝 = 실제 촬영 폭)은 항상 표시한다 — 화면에 보이는 범위가
            // 몇 µm 까지인지 알 수 있어야 한다. 대신 겹치는 마지막 정규 눈금은 생략한다.
            foreach (double v in TickValues(totalX, stepX, imgW))
            {
                double x = x0 + v / totalX * imgW;
                dc.DrawLine(TickPen, new Point(x, y0 - TickLen), new Point(x, y0));
                dc.DrawLine(GridPen, new Point(x, y0), new Point(x, y0 + imgH));   // 옅은 보조선
                DrawText(dc, FormatValue(v, stepX), x, y0 - TickLen - 2, dpi, centerX: true, rightAlign: false);
            }

            // ── 좌측(Y) 눈금 ──
            foreach (double v in TickValues(totalY, stepY, imgH))
            {
                double y = y0 + v / totalY * imgH;
                dc.DrawLine(TickPen, new Point(x0 - TickLen, y), new Point(x0, y));
                dc.DrawLine(GridPen, new Point(x0, y), new Point(x0 + imgW, y));
                DrawText(dc, FormatValue(v, stepY), x0 - TickLen - 3, y, dpi, centerX: false, rightAlign: true);
            }

            // 단위 표기 — 좌상단 구석. 왼쪽 끝에 붙여 X축 "0" 라벨과 붙어 보이지 않게 한다.
            DrawText(dc, inMicrons ? "µm" : "px", 2, 1, dpi, centerX: false, rightAlign: false);
        }

        /// <summary>
        /// 0 부터 step 간격의 눈금값 + <b>끝값(total)</b>. 끝값과 너무 가까운 정규 눈금은
        /// 라벨이 겹치므로 건너뛴다(끝값을 우선한다).
        /// </summary>
        private static IEnumerable<double> TickValues(double total, double step, double lengthDip)
        {
            if (total <= 0 || step <= 0 || lengthDip <= 0) yield break;

            // 끝값 라벨과 겹치지 않을 최소 여유 — 라벨 간격의 3/4.
            double minGapValue = total * (MinLabelSpacing * 0.75 / lengthDip);

            for (double v = 0; v < total - 1e-9; v += step)
                if (total - v > minGapValue) yield return v;

            yield return total;   // 끝 눈금은 항상
        }

        /// <summary>
        /// 주 눈금 사이를 세분한 보조 눈금값. 주 눈금 간격의 앞자리에 따라 5등분(1·5×10ⁿ) 또는
        /// 4등분(2×10ⁿ) 해서, 보조 눈금 하나가 항상 "떨어지는 값"이 되게 한다.
        /// </summary>
        private static IEnumerable<double> MinorTicks(double total, double step)
        {
            if (total <= 0 || step <= 0) yield break;

            double lead  = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
            int    div   = Math.Abs(lead - 2) < 0.01 ? 4 : 5;
            double minor = step / div;

            int count = (int)Math.Floor(total / minor);
            for (int i = 0; i <= count; i++) yield return i * minor;
        }

        /// <summary>눈금 라벨. 간격이 1 미만이면 소수점 한 자리까지 보여준다.</summary>
        private static string FormatValue(double v, double step) =>
            step >= 1 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                      : v.ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>요청 간격 이상이면서 1·2·5×10ⁿ 인 "읽기 좋은" 눈금 간격.</summary>
        private static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;
            double exp  = Math.Floor(Math.Log10(raw));
            double pow  = Math.Pow(10, exp);
            double frac = raw / pow;
            double nice = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
            return nice * pow;
        }

        private void DrawText(DrawingContext dc, string text, double x, double y, double dpi,
                              bool centerX, bool rightAlign)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       new Typeface("Segoe UI"), 10, TextBrush, dpi);
            double tx = centerX ? x - ft.Width / 2.0 : (rightAlign ? x - ft.Width : x);
            double ty = centerX ? y - ft.Height : y - ft.Height / 2.0;

            // 끝 눈금 라벨은 요소 경계를 넘어 잘리기 쉽다(이미지가 폭을 꽉 채울 때) → 안쪽으로 당긴다.
            tx = Math.Clamp(tx, 0, Math.Max(0, RenderSize.Width  - ft.Width));
            ty = Math.Clamp(ty, 0, Math.Max(0, RenderSize.Height - ft.Height));

            dc.DrawText(ft, new Point(tx, ty));
        }
    }
}
