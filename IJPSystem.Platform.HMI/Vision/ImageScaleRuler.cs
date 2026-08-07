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
        // 눈금·라벨을 이미지 <b>안쪽</b> 가장자리에 그린다 — 바깥 여백(gutter)을 두지 않아
        // 주어진 폭을 전부 이미지에 쓴다. 밝은 배경 위에서도 읽히도록 라벨 뒤에 반투명 판을 깐다.
        private const double MinLabelSpacing = 64;   // 라벨이 겹치지 않는 최소 간격[dip]
        private const double TickLen      = 6;       // 라벨 붙는 주 눈금
        private const double MinorTickLen = 3;       // 라벨 없는 보조 눈금
        private const double FontSize     = 9;
        private const double LabelPadX    = 3;       // 라벨 배경판 좌우 여백
        private const double LabelPadY    = 1;

        // 눈금도 분홍 — LabVIEW 'Sample DW' 와 같은 그림이 되도록 맞춘다(측정창과 같은 계열).
        private static readonly Brush TextBrush  = new SolidColorBrush(Color.FromRgb(0xEC, 0x40, 0x99));
        private static readonly Brush LabelBack  = new SolidColorBrush(Color.FromArgb(0xB8, 0x0B, 0x0F, 0x1A));
        private static readonly Pen   TickPen    = MakePen(0xEC, 0x40, 0x99, 1.0, 0xD0);
        private static readonly Pen   GridPen    = MakePen(0x94, 0xA3, 0xB8, 0.5, 0x55);
        private static readonly Pen   BorderPen  = MakePen(0x47, 0x55, 0x69, 1.0, 0xFF);

        // 가이드라인 — LabVIEW 'Sample DW' 와 같은 색 규약.
        //   분홍: 노즐 측정창(항상 표시). 초록: 측정 결과(측정했을 때만).
        private static readonly Pen   GuidePen   = MakePen(0xEC, 0x40, 0x99, 1.0, 0xE0);
        private static readonly Pen   ResultPen  = MakePen(0x22, 0xC5, 0x5E, 1.0, 0xE0);
        private static readonly Brush ResultText = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));

        private static Pen MakePen(byte r, byte g, byte b, double thickness, byte alpha)
        {
            var p = new Pen(new SolidColorBrush(Color.FromArgb(alpha, r, g, b)), thickness);
            p.Freeze();
            return p;
        }

        static ImageScaleRuler()
        {
            TextBrush.Freeze();
            LabelBack.Freeze();
            ResultText.Freeze();
        }

        // ── 노즐 가이드라인 ───────────────────────────────────────────────────
        // 전부 <b>이미지 픽셀</b> 단위다. µm 로 두면 스케일 교정이 어긋나는 순간 라인이 통째로
        // 밀려서 "고정 가이드"라는 목적 자체가 깨진다(실장 2026-08-06: 371px 로 잡혀 실제 113px
        // 와 3배 차이). 픽셀로 두면 교정 상태와 무관하게 같은 자리에 선다.

        private static DependencyProperty Px(string name) =>
            DependencyProperty.Register(name, typeof(double), typeof(ImageScaleRuler),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty NozzlePitchPxProperty       = Px(nameof(NozzlePitchPx));
        public static readonly DependencyProperty NozzleOriginXPxProperty     = Px(nameof(NozzleOriginXPx));
        public static readonly DependencyProperty NozzleWindowWidthPxProperty = Px(nameof(NozzleWindowWidthPx));
        public static readonly DependencyProperty BandTopPxProperty           = Px(nameof(BandTopPx));
        public static readonly DependencyProperty BandBottomPxProperty        = Px(nameof(BandBottomPx));

        /// <summary>측정창 간격[px]. 0 이면 가이드라인을 그리지 않는다.</summary>
        public double NozzlePitchPx
        {
            get => (double)GetValue(NozzlePitchPxProperty);
            set => SetValue(NozzlePitchPxProperty, value);
        }

        /// <summary>1번 측정창 중심 X[px].</summary>
        public double NozzleOriginXPx
        {
            get => (double)GetValue(NozzleOriginXPxProperty);
            set => SetValue(NozzleOriginXPxProperty, value);
        }

        /// <summary>측정창 폭[px].</summary>
        public double NozzleWindowWidthPx
        {
            get => (double)GetValue(NozzleWindowWidthPxProperty);
            set => SetValue(NozzleWindowWidthPxProperty, value);
        }

        /// <summary>측정 구간 윗변 Y[px].</summary>
        public double BandTopPx
        {
            get => (double)GetValue(BandTopPxProperty);
            set => SetValue(BandTopPxProperty, value);
        }

        /// <summary>측정 구간 아랫변 Y[px]. 0 이면 이미지 아래끝.</summary>
        public double BandBottomPx
        {
            get => (double)GetValue(BandBottomPxProperty);
            set => SetValue(BandBottomPxProperty, value);
        }

        public static readonly DependencyProperty MarksProperty =
            DependencyProperty.Register(nameof(Marks), typeof(IEnumerable<NozzleMeasureMark>), typeof(ImageScaleRuler),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>측정 결과 마커(초록). null/빈 목록이면 그리지 않는다 — 측정 전에는 분홍 가이드만 보인다.</summary>
        public IEnumerable<NozzleMeasureMark>? Marks
        {
            get => (IEnumerable<NozzleMeasureMark>?)GetValue(MarksProperty);
            set => SetValue(MarksProperty, value);
        }

        public ImageScaleRuler()
        {
            // LowQuality(=쌍선형, GPU) 여야 한다. HighQuality(Fant)는 CPU 소프트웨어 경로라
            // 라이브에서 2856×2848 프레임을 매 렌더 패스마다 다시 리샘플링해 화면이 통째로 깜빡인다
            // (글라스뷰에서 같은 원인으로 확인됨). 눈금·ROI 는 벡터라 품질 영향이 없다.
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        }

        // AffectsMeasure — 이미지가 바뀌면 종횡비가 달라져 필요한 높이도 달라진다(MeasureOverride 참고).
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(ImageScaleRuler),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

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

        public static readonly DependencyProperty MicronsPerPixelYProperty =
            DependencyProperty.Register(nameof(MicronsPerPixelY), typeof(double), typeof(ImageScaleRuler),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// 세로 눈금 스케일[µm/px]. 0 이면 <see cref="MicronsPerPixel"/> 을 쓴다.
        /// 시야(FOV)는 가로·세로가 따로 정해지므로, 프레임 종횡비가 시야와 다르면 두 값이 달라진다.
        /// 그때 한 값으로 양쪽 눈금을 그리면 한 축이 거짓 길이를 말한다.
        /// </summary>
        public double MicronsPerPixelY
        {
            get => (double)GetValue(MicronsPerPixelYProperty);
            set => SetValue(MicronsPerPixelYProperty, value);
        }

        /// <summary>
        /// 주어진 폭에서 <b>이미지가 실제로 차지하는 높이만</b> 요구한다.
        /// 이렇게 해야 세로로 긴 패널에 가로로 넓은 이미지를 넣어도 아래에 빈 공간이 남지 않는다
        /// (남는 공간은 부모가 결과 카드 등에 쓸 수 있다). 부모가 Auto 행이면 높이가 무한으로 오므로
        /// 폭 기준으로만 맞춘다 — 폭은 항상 유한하다.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            var src = Source;
            if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return new Size(0, 0);

            double areaW = availableSize.Width;
            if (double.IsInfinity(areaW) || double.IsNaN(areaW)) areaW = src.PixelWidth;
            if (areaW <= 8) return new Size(0, 0);

            // 세로가 더 좁으면 그쪽에 맞춘다(OnRender 와 동일한 Uniform 규칙).
            double scale = areaW / src.PixelWidth;
            double areaH = availableSize.Height;
            if (!double.IsInfinity(areaH) && !double.IsNaN(areaH) && areaH > 8)
                scale = Math.Min(scale, areaH / src.PixelHeight);

            return new Size(src.PixelWidth * scale, src.PixelHeight * scale);
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

            double areaW = RenderSize.Width;
            double areaH = RenderSize.Height;
            if (areaW <= 8 || areaH <= 8) return;

            // 종횡비 유지 축소(Uniform) + 좌상단 고정 — 가운데 정렬하지 않는다(원점 고정).
            double scale = Math.Min(areaW / src.PixelWidth, areaH / src.PixelHeight);
            double imgW = src.PixelWidth * scale, imgH = src.PixelHeight * scale;
            double x0 = 0, y0 = 0;

            var imgRect = new Rect(x0, y0, imgW, imgH);
            dc.DrawImage(src, imgRect);

            // 교정 전(µm/px 미설정)에는 px 로 표시해 눈금이 거짓 물리량을 말하지 않게 한다.
            double umPerPx = MicronsPerPixel;
            bool inMicrons = umPerPx > 0 && !double.IsNaN(umPerPx) && !double.IsInfinity(umPerPx);
            if (!inMicrons) umPerPx = 1.0;

            // 세로 스케일이 따로 주어지면 그것을 쓴다 — 시야가 가로·세로 각각 정해지기 때문이다.
            double umPerPxY = MicronsPerPixelY;
            if (!inMicrons || !(umPerPxY > 0) || double.IsNaN(umPerPxY) || double.IsInfinity(umPerPxY))
                umPerPxY = umPerPx;

            double totalX = src.PixelWidth  * umPerPx;
            double totalY = src.PixelHeight * umPerPxY;

            double stepX = NiceStep(totalX * (MinLabelSpacing / imgW));
            double stepY = NiceStep(totalY * (MinLabelSpacing / imgH));

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRectangle(null, BorderPen, imgRect);   // 0 기준선이 어디인지 보이게

            // ── 보조 눈금(라벨 없음) ── 주 눈금 사이를 세분해 눈대중 읽기를 돕는다.
            // 이미지 안쪽으로 그린다(바깥에 여백이 없다).
            foreach (double v in MinorTicks(totalX, stepX))
            {
                double x = x0 + v / totalX * imgW;
                dc.DrawLine(TickPen, new Point(x, y0), new Point(x, y0 + MinorTickLen));
            }
            foreach (double v in MinorTicks(totalY, stepY))
            {
                double y = y0 + v / totalY * imgH;
                dc.DrawLine(TickPen, new Point(x0, y), new Point(x0 + MinorTickLen, y));
            }

            // 촬영 범위 캡션(우상단) 자리를 먼저 잡는다 — X축 끝쪽 라벨과 겹치면 안 되므로
            // 캡션 폭을 재서 그 구간에 걸리는 눈금 라벨은 생략한다(눈금선 자체는 그대로 그린다).
            string unit = inMicrons ? "µm" : "px";
            var capFt = MakeText($"{Math.Round(totalX):0} × {Math.Round(totalY):0} {unit}", dpi);
            double capLeft = x0 + imgW - LabelPadX * 2 - capFt.Width;

            // 라벨은 이미지 안쪽 가장자리에 붙인다.
            double labelTop  = y0 + TickLen + 2;                    // X 라벨 윗변
            double labelLeft = x0 + TickLen + 3;                    // Y 라벨 왼변
            double xLabelBand = labelTop + capFt.Height + 2;        // X 라벨이 차지하는 띠의 아랫변

            // ── 상단(X) 눈금 ──
            foreach (double v in TickValues(totalX, stepX))
            {
                double x = x0 + v / totalX * imgW;
                dc.DrawLine(TickPen, new Point(x, y0), new Point(x, y0 + TickLen));
                dc.DrawLine(GridPen, new Point(x, y0), new Point(x, y0 + imgH));   // 옅은 보조선

                var ft = MakeText(FormatValue(v, stepX), dpi);
                if (x + ft.Width / 2.0 > capLeft - 8) continue;   // 캡션과 충돌 → 라벨만 생략
                DrawFormatted(dc, ft, x, labelTop, centerX: true, rightAlign: false, topAligned: true);
            }

            // ── 좌측(Y) 눈금 ──
            foreach (double v in TickValues(totalY, stepY))
            {
                double y = y0 + v / totalY * imgH;
                dc.DrawLine(TickPen, new Point(x0, y), new Point(x0 + TickLen, y));
                dc.DrawLine(GridPen, new Point(x0, y), new Point(x0 + imgW, y));

                // 원점(0) 라벨은 X축이 이미 찍었다 — 좌상단 구석에서 두 번 겹치지 않게 생략.
                if (y < xLabelBand) continue;
                DrawText(dc, FormatValue(v, stepY), labelLeft, y, dpi, centerX: false, rightAlign: false);
            }

            // 촬영 범위 캡션 — 축 끝 눈금 대신 우상단 안쪽에 한 번만. 단위도 여기 들어가므로
            // 좌상단 구석의 단위 표기는 두지 않는다(중복 + 폭 낭비).
            DrawFormatted(dc, capFt, x0 + imgW - LabelPadX * 2, labelTop,
                          centerX: false, rightAlign: true, topAligned: true);

            DrawNozzleGuides(dc, src, x0, y0, scale, imgW, imgH, dpi);
        }

        /// <summary>
        /// 노즐 측정창(분홍)과 측정 결과(초록)를 그린다.
        /// 분홍은 이미지가 있으면 <b>항상</b> — 라이브든 불러온 파일이든 같은 자리에 서 있어야
        /// 액적이 창 안에 드는지 눈으로 바로 확인된다. 초록은 측정했을 때만 나타난다.
        /// </summary>
        private void DrawNozzleGuides(DrawingContext dc, BitmapSource src,
                                      double x0, double y0, double scale, double imgW, double imgH, double dpi)
        {
            double pitch = NozzlePitchPx;
            if (pitch < 2) return;                       // 미설정 — 가이드 없음

            double winW = NozzleWindowWidthPx > 0 ? NozzleWindowWidthPx : pitch * 0.5;
            winW = Math.Min(winW, pitch);                // 이웃 창과 겹치지 않게
            double originX = NozzleOriginXPx > 0 ? NozzleOriginXPx : pitch / 2.0;

            double top    = Math.Clamp(BandTopPx, 0, src.PixelHeight);
            double bottom = BandBottomPx > top ? Math.Min(BandBottomPx, src.PixelHeight) : src.PixelHeight;

            double ToX(double px) => x0 + px * scale;
            double ToY(double px) => y0 + px * scale;

            double yT = ToY(top), yB = ToY(bottom);

            // 측정창(분홍) — 위/아래 초록 기준선 사이를 채운다.
            var windows = new List<(double Left, double Right)>();
            int index = 0;
            for (double cx = originX; cx < src.PixelWidth; cx += pitch)
            {
                index++;
                double left  = ToX(cx - winW / 2.0);
                double right = ToX(cx + winW / 2.0);
                if (right <= x0 || left >= x0 + imgW) continue;

                left  = Math.Max(left,  x0);
                right = Math.Min(right, x0 + imgW);
                if (right - left < 1) continue;

                windows.Add((left, right));
                dc.DrawRectangle(null, GuidePen, new Rect(left, yT, right - left, Math.Max(1, yB - yT)));

                // 창 번호 — 속도 그래프의 X축(노즐 번호)과 화면의 창을 짝지어 읽으려면 필요하다.
                // 창이 좁으면 숫자끼리 붙어 읽을 수 없으므로 그때는 생략한다.
                if (right - left < 16) continue;
                var idxFt = new FormattedText(index.ToString(CultureInfo.InvariantCulture),
                                              CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                              new Typeface("Segoe UI"), FontSize - 1, TextBrush, dpi);
                dc.DrawText(idxFt, new Point((left + right) / 2.0 - idxFt.Width / 2.0,
                                             Math.Max(y0, yT - idxFt.Height - 1)));
            }

            var marks = Marks;
            if (marks == null) return;

            // 측정 구간 경계(초록) — 낙하거리를 재는 기준선이라 결과와 함께 나와야 의미가 있다.
            dc.DrawLine(ResultPen, new Point(x0, yT), new Point(x0 + imgW, yT));
            dc.DrawLine(ResultPen, new Point(x0, yB), new Point(x0 + imgW, yB));

            // 속도값은 <b>그 액적이 든 창 안쪽</b> 아래에 적는다 — 창과 값이 짝지어 보여야
            // 어느 노즐 값인지 헷갈리지 않는다(LabVIEW 도 창마다 숫자를 하나씩 붙인다).
            foreach (var m in marks)
            {
                if (double.IsNaN(m.Velocity)) continue;

                double mx = ToX(m.XPixel);
                if (mx < x0 || mx > x0 + imgW) continue;

                var ft = new FormattedText(m.Velocity.ToString("F2", CultureInfo.InvariantCulture),
                                           CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           new Typeface("Segoe UI"), FontSize, ResultText, dpi);

                // 액적이 든 창을 찾아 그 안에 넣고, 못 찾으면 액적 중심 기준으로 놓는다.
                double tx = mx - ft.Width / 2.0;
                foreach (var w in windows)
                {
                    if (mx < w.Left || mx > w.Right) continue;
                    tx = w.Left + 2;
                    break;
                }
                tx = Math.Clamp(tx, x0, Math.Max(x0, x0 + imgW - ft.Width));
                dc.DrawText(ft, new Point(tx, Math.Max(y0, yB - ft.Height - 1)));
            }
        }

        /// <summary>
        /// 0 부터 step 간격의 눈금값. 끝값(3642 같은 딱 떨어지지 않는 수)은 눈금으로 찍지 않고
        /// 촬영 범위 캡션(우상단)으로 따로 보여준다 — 축 라벨은 읽기 좋은 값만 남긴다.
        /// </summary>
        private static IEnumerable<double> TickValues(double total, double step)
        {
            if (total <= 0 || step <= 0) yield break;
            for (double v = 0; v < total - 1e-9; v += step) yield return v;
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

        private static FormattedText MakeText(string text, double dpi) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), FontSize, TextBrush, dpi);

        private void DrawText(DrawingContext dc, string text, double x, double y, double dpi,
                              bool centerX, bool rightAlign) =>
            DrawFormatted(dc, MakeText(text, dpi), x, y, centerX, rightAlign);

        /// <param name="topAligned">true 면 y 가 글자의 <b>윗변</b>, false 면 세로 중심.</param>
        private void DrawFormatted(DrawingContext dc, FormattedText ft, double x, double y,
                                   bool centerX, bool rightAlign, bool topAligned = false)
        {
            double tx = centerX ? x - ft.Width / 2.0 : (rightAlign ? x - ft.Width : x);
            double ty = topAligned ? y : y - ft.Height / 2.0;

            // 라벨이 요소 경계를 넘어 잘리기 쉽다(이미지가 폭을 꽉 채울 때) → 안쪽으로 당긴다.
            tx = Math.Clamp(tx, 0, Math.Max(0, RenderSize.Width  - ft.Width));
            ty = Math.Clamp(ty, 0, Math.Max(0, RenderSize.Height - ft.Height));

            // 라벨이 이미지 위에 얹히므로 밝은 배경에서도 읽히도록 반투명 판을 깐다.
            dc.DrawRoundedRectangle(LabelBack, null,
                new Rect(tx - LabelPadX, ty - LabelPadY,
                         ft.Width + LabelPadX * 2, ft.Height + LabelPadY * 2), 2, 2);

            dc.DrawText(ft, new Point(tx, ty));
        }
    }
}
