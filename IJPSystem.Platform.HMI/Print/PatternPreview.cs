using IJPSystem.Platform.Infrastructure.Print;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// RIP 결과(<see cref="PrintPattern"/>)를 그려 보여 주는 요소.
    ///
    /// <para>
    /// <b>왜 필요한가</b>: 지도가 맞는지 지금은 테스트로만 알 수 있다. 그런데 노즐 번호 규약이나
    /// 열 수 같은 미확인 항목이 틀리면 테스트는 통과하면서 실물만 어긋난다 — 잉크가 나간 뒤에
    /// 알게 되면 늦다. 찍기 전에 "무엇을 찍으려 하는가"를 눈으로 확인하기 위한 화면이다.
    /// </para>
    /// <para>
    /// 가로 = 노즐(왼쪽이 X 작은 쪽), 세로 = 스캔 진행 방향. 밝을수록 큰 방울.
    /// </para>
    /// <para>
    /// 텍스트는 WPF FormattedText(DirectWrite)로 그린다 — 제어 PC 글꼴 문제의 영향을 받지 않는다.
    /// [[project-control-pc-skia-font]]
    /// </para>
    /// </summary>
    public sealed class PatternPreview : FrameworkElement
    {
        // 실제 인쇄 패턴은 수천 스텝 × 수백 노즐이 된다. 32비트 프로세스라 원본 크기 그대로
        // 비트맵을 만들면 주소공간을 크게 먹는다 — 어차피 화면에 그만큼 표시되지도 않으므로
        // 긴 변을 이 값으로 줄여서 만든다. [[project-dw-live-memory]]
        private const int MaxRenderSide = 1400;

        private const double FontSize  = 9;
        private const double Gutter    = 34;   // 왼쪽 µm 눈금 자리
        private const double TopGutter = 16;   // 위쪽 노즐 번호 자리
        private const double MinLabelGap = 46;

        private static readonly Brush TextBrush = Frozen(0x94, 0xA3, 0xB8);
        private static readonly Pen   BorderPen = FrozenPen(0x47, 0x55, 0x69, 1.0);
        private static readonly Brush BackBrush = Frozen(0x0D, 0x11, 0x17);
        private static readonly Brush EmptyText = Frozen(0x47, 0x55, 0x69);

        private static Brush Frozen(byte r, byte g, byte b)
        { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        private static Pen FrozenPen(byte r, byte g, byte b, double t)
        { var p = new Pen(Frozen(r, g, b), t); p.Freeze(); return p; }

        public static readonly DependencyProperty PatternProperty =
            DependencyProperty.Register(nameof(Pattern), typeof(PrintPattern), typeof(PatternPreview),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender, OnPatternChanged));

        public PrintPattern? Pattern
        {
            get => (PrintPattern?)GetValue(PatternProperty);
            set => SetValue(PatternProperty, value);
        }

        /// <summary>패턴이 없을 때 가운데 적을 글.</summary>
        public static readonly DependencyProperty EmptyTextProperty =
            DependencyProperty.Register(nameof(EmptyLabel), typeof(string), typeof(PatternPreview),
                new FrameworkPropertyMetadata("패턴 없음 — 이미지를 불러와 변환하세요",
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public string? EmptyLabel
        {
            get => (string?)GetValue(EmptyTextProperty);
            set => SetValue(EmptyTextProperty, value);
        }

        private BitmapSource? _bmp;
        private double _x0, _pitch = 1;   // 슬롯 격자 — 라벨을 같은 자리에 얹기 위해 보관
        private int _slots = 1;

        private static void OnPatternChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((PatternPreview)d)._bmp = ((PatternPreview)d).Render((PrintPattern?)e.NewValue);

        /// <summary>
        /// 지도를 회색 이미지로.
        ///
        /// <para>
        /// 컬럼을 밀착시켜 그리면 안 된다 — 안 쓰는 노즐이 있어도 그 자리가 메워져 보여
        /// "빠진 노즐"을 눈으로 찾을 수 없다. 이 화면의 목적이 바로 그걸 찾는 것이므로
        /// <b>실제 X 위치</b>에 놓고, 빈 노즐 자리는 빈 칸으로 남긴다.
        /// </para>
        /// <para>
        /// 줄일 때는 <b>최대값</b>을 취한다 — 평균을 내면 드문드문 찍히는 영역이 통째로
        /// 사라져 "안 찍히는 것처럼" 보인다.
        /// </para>
        /// </summary>
        private BitmapSource? Render(PrintPattern? p)
        {
            if (p == null || p.Steps == 0 || p.Nozzles == 0) return null;
            if (p.Columns.Count != p.Nozzles) return null;

            // 노즐 자리 간격 = 이웃 컬럼 X 차의 최솟값. 빠진 노즐이 있어도 남은 것들의
            // 최소 간격이 곧 한 칸이라, 배열을 몰라도 격자를 복원할 수 있다.
            double pitch = double.MaxValue;
            for (int c = 1; c < p.Nozzles; c++)
            {
                double d = p.Columns[c].XUm - p.Columns[c - 1].XUm;
                if (d > 1e-6 && d < pitch) pitch = d;
            }
            if (double.IsInfinity(pitch) || pitch == double.MaxValue) pitch = 1;

            double x0 = p.Columns[0].XUm;
            int slots = (int)Math.Round((p.Columns[^1].XUm - x0) / pitch) + 1;
            if (slots < 1) slots = 1;

            _x0 = x0; _pitch = pitch; _slots = slots;   // 라벨을 같은 격자에 얹기 위해

            int srcH = p.Steps;
            int stride0 = Math.Max(1, (int)Math.Ceiling((double)Math.Max(slots, srcH) / MaxRenderSide));
            int w = Math.Max(1, slots / stride0), h = Math.Max(1, srcH / stride0);

            byte maxLevel = 0;
            for (int y = 0; y < srcH; y++)
                for (int x = 0; x < p.Nozzles; x++)
                    if (p.Levels[y, x] > maxLevel) maxLevel = p.Levels[y, x];
            if (maxLevel == 0) maxLevel = 1;

            var px = new byte[w * h];
            for (int c = 0; c < p.Nozzles; c++)
            {
                int slot = (int)Math.Round((p.Columns[c].XUm - x0) / pitch);
                int dx = Math.Clamp(slot / stride0, 0, w - 1);

                for (int y = 0; y < srcH; y++)
                {
                    byte v = p.Levels[y, c];
                    if (v == 0) continue;
                    int dy = Math.Clamp(y / stride0, 0, h - 1);
                    byte scaled = (byte)(v * 255 / maxLevel);
                    if (scaled > px[dy * w + dx]) px[dy * w + dx] = scaled;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, px, w);
            bmp.Freeze();
            return bmp;
        }

        private FormattedText Text(string s, Brush brush) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));

            var p = Pattern;
            if (_bmp == null || p == null)
            {
                if (!string.IsNullOrEmpty(EmptyLabel))
                {
                    var ft = Text(EmptyLabel!, EmptyText);
                    dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
                }
                return;
            }

            var plot = new Rect(Gutter, TopGutter,
                                Math.Max(1, w - Gutter - 8), Math.Max(1, h - TopGutter - 8));

            // 지도는 원본 비율을 유지한다 — 늘리면 방울 배치가 실제와 다르게 보인다.
            double s = Math.Min(plot.Width / _bmp.PixelWidth, plot.Height / _bmp.PixelHeight);
            var img = new Rect(plot.X, plot.Y, _bmp.PixelWidth * s, _bmp.PixelHeight * s);

            dc.DrawImage(_bmp, img);
            dc.DrawRectangle(null, BorderPen, img);

            DrawNozzleLabels(dc, p, img);
            DrawScanLabels(dc, p, img);
        }

        /// <summary>위쪽에 노즐 번호 — 컬럼이 어느 노즐인지 눈으로 확인해야 번호 규약을 검증할 수 있다.</summary>
        private void DrawNozzleLabels(DrawingContext dc, PrintPattern p, Rect img)
        {
            if (p.Nozzles == 0 || p.Columns.Count != p.Nozzles) return;

            // 라벨도 <b>슬롯</b> 위치에 얹는다 — 그림은 X 격자로 그렸는데 라벨만 컬럼 순서로
            // 놓으면 빠진 노즐 구간에서 번호와 그림이 어긋난다.
            double per = img.Width / Math.Max(1, _slots);
            int step = Math.Max(1, (int)Math.Ceiling(MinLabelGap / Math.Max(per, 0.01)));

            for (int c = 0; c < p.Nozzles; c += step)
            {
                var ft = Text(p.Columns[c].Number.ToString(CultureInfo.InvariantCulture), TextBrush);
                int slot = (int)Math.Round((p.Columns[c].XUm - _x0) / _pitch);
                double x = img.X + (slot + 0.5) * per - ft.Width / 2;
                x = Math.Max(0, Math.Min(x, img.Right - ft.Width));
                dc.DrawText(ft, new Point(x, img.Y - ft.Height - 1));
            }
        }

        /// <summary>왼쪽에 스캔 방향 거리 [mm] — 스텝 수만 적으면 실제 길이를 알 수 없다.</summary>
        private void DrawScanLabels(DrawingContext dc, PrintPattern p, Rect img)
        {
            if (p.Steps == 0 || p.ScanStepUm <= 0) return;

            double totalMm = p.Steps * p.ScanStepUm / 1000.0;
            int divisions = Math.Max(1, Math.Min(8, (int)(img.Height / 40)));

            for (int i = 0; i <= divisions; i++)
            {
                double t = (double)i / divisions;
                var ft = Text((totalMm * t).ToString("F1", CultureInfo.InvariantCulture), TextBrush);
                double y = img.Y + img.Height * t;
                dc.DrawLine(BorderPen, new Point(img.X - 4, y), new Point(img.X, y));
                dc.DrawText(ft, new Point(img.X - 6 - ft.Width, y - ft.Height / 2));
            }

            var unit = Text("mm", TextBrush);
            dc.DrawText(unit, new Point(Math.Max(0, img.X - 6 - unit.Width), img.Y - unit.Height - 1));
        }
    }
}
