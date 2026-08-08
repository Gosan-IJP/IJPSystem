using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>
    /// 카테고리 축(노즐 번호) + 값 하나짜리 꺾은선 그래프를 <b>WPF 로 직접</b> 그리는 요소.
    ///
    /// <para>
    /// <b>LiveCharts(Skia) 를 쓰지 않는 이유</b>: 제어 PC 는 Skia 의 <b>텍스트 렌더 경로 자체</b>가
    /// 깨져 있어 축 숫자가 통째로 나오지 않는다. 격자선은 그려지고 WPF 한글은 멀쩡하니 OS 글꼴
    /// 문제가 아니다. 글꼴을 지정해 우회하려다(FromFamilyName → FromStream/FromFile) 두 방법 모두
    /// 첫 렌더에서 네이티브 즉사했다(2026-07-23, 2026-08-07). 글꼴 조회가 아니라 렌더 경로가
    /// 문제라 Skia 안에서는 우회로가 없다. WPF FormattedText(DirectWrite)는 같은 PC 에서 멀쩡하므로
    /// 그리는 주체를 바꾼다. [[project-control-pc-skia-font]]
    /// </para>
    /// <para>
    /// 그래서 범용 차트를 만들지 않았다 — 드랍와처가 쓰는 모양(카테고리 축·단일 시리즈)만 그린다.
    /// 축 종류·다중 시리즈·줌을 얹기 시작하면 라이브러리를 다시 만드는 일이 된다.
    /// </para>
    /// </summary>
    public sealed class CategoryLineChart : FrameworkElement
    {
        private const double FontSize    = 10;
        private const double TitleSize   = 10;
        private const double TickLen     = 4;
        private const double PadTop      = 8;
        private const double PadRight    = 10;
        private const double LabelGap    = 3;    // 축과 글자 사이
        private const double MinLabelGap = 24;   // X 라벨이 겹치지 않는 최소 간격[dip]
        private const double MarkerR     = 2.6;
        private const int    TargetTicks = 4;

        private static readonly Brush AxisText  = Frozen(0x94, 0xA3, 0xB8);
        private static readonly Brush EmptyBrsh = Frozen(0x47, 0x55, 0x69);
        private static readonly Brush PointFill = Frozen(0xFF, 0xFF, 0xFF);
        private static readonly Pen   GridPen   = FrozenPen(0x33, 0x41, 0x55, 0.5, 0xFF);
        private static readonly Pen   AxisPen   = FrozenPen(0x47, 0x55, 0x69, 1.0, 0xFF);
        // 0 은 부호가 바뀌는 자리라 격자선보다 진하게 — 낙하 위치가 음수로 내려갔는지 바로 보인다.
        private static readonly Pen   ZeroPen   = FrozenPen(0x64, 0x74, 0x8B, 1.0, 0xFF);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static Pen FrozenPen(byte r, byte g, byte b, double thickness, byte alpha)
        {
            var p = new Pen(new SolidColorBrush(Color.FromArgb(alpha, r, g, b)), thickness);
            p.Freeze();
            return p;
        }

        private static DependencyProperty Reg<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(CategoryLineChart),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ValuesProperty     = Reg<IReadOnlyList<double>?>(nameof(Values), null);
        public static readonly DependencyProperty LabelsProperty     = Reg<IReadOnlyList<string>?>(nameof(Labels), null);
        public static readonly DependencyProperty LineBrushProperty  = Reg<Brush?>(nameof(LineBrush), null);
        public static readonly DependencyProperty YAxisTitleProperty = Reg<string?>(nameof(YAxisTitle), null);
        public static readonly DependencyProperty XAxisTitleProperty = Reg<string?>(nameof(XAxisTitle), null);
        public static readonly DependencyProperty EmptyTextProperty  = Reg<string?>(nameof(EmptyText), "측정 전");

        /// <summary>노즐 순서대로의 값. 비어 있으면 축만 그린다.</summary>
        public IReadOnlyList<double>? Values
        {
            get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        /// <summary>X 축 카테고리 라벨(노즐 번호). <see cref="Values"/> 보다 짧으면 그만큼만 적는다.</summary>
        public IReadOnlyList<string>? Labels
        {
            get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
            set => SetValue(LabelsProperty, value);
        }

        public Brush? LineBrush
        {
            get => (Brush?)GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        public string? YAxisTitle
        {
            get => (string?)GetValue(YAxisTitleProperty);
            set => SetValue(YAxisTitleProperty, value);
        }

        public string? XAxisTitle
        {
            get => (string?)GetValue(XAxisTitleProperty);
            set => SetValue(XAxisTitleProperty, value);
        }

        /// <summary>값이 없을 때 plot 가운데 적는 글. 빈 그래프가 "고장"으로 읽히지 않게 한다.</summary>
        public string? EmptyText
        {
            get => (string?)GetValue(EmptyTextProperty);
            set => SetValue(EmptyTextProperty, value);
        }

        private FormattedText Text(string s, double size, Brush brush) =>
            new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                              new Typeface("Consolas"), size, brush,
                              VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            var values = Values;
            int n = values?.Count ?? 0;

            var (lo, hi) = Range(values, n);
            double[] ticks = NiceTicks(lo, hi, TargetTicks);
            if (ticks.Length >= 2) { lo = ticks[0]; hi = ticks[^1]; }

            // ── 여백 계산 — Y 라벨의 실제 폭에서 왼쪽 여백을 정한다(고정값이면 자릿수가 늘 때 잘린다) ──
            // 자릿수는 <b>눈금 간격</b>으로 정한다 — 값마다 따로 정하면 0 · 0.500 · 1.00 처럼 섞인다.
            int decimals = TickDecimals(ticks);
            double yLabelW = 0;
            var yTexts = new FormattedText[ticks.Length];
            for (int i = 0; i < ticks.Length; i++)
            {
                yTexts[i] = Text(ticks[i].ToString("F" + decimals, CultureInfo.InvariantCulture), FontSize, AxisText);
                yLabelW = Math.Max(yLabelW, yTexts[i].Width);
            }

            double yTitleW = string.IsNullOrEmpty(YAxisTitle) ? 0 : TitleSize + 3;   // 세로쓰기라 높이가 폭이 된다
            double left    = yTitleW + yLabelW + LabelGap + TickLen;
            double xLabelH = FontSize + 4;
            double xTitleH = string.IsNullOrEmpty(XAxisTitle) ? 0 : TitleSize + 3;
            double bottom  = xLabelH + xTitleH + LabelGap;

            var plot = new Rect(left, PadTop,
                                Math.Max(1, w - left - PadRight),
                                Math.Max(1, h - PadTop - bottom));

            // ── 격자 + Y 라벨 ──
            for (int i = 0; i < ticks.Length; i++)
            {
                double y = Snap(ValueToY(ticks[i], lo, hi, plot));
                dc.DrawLine(Math.Abs(ticks[i]) < 1e-12 ? ZeroPen : GridPen,
                            new Point(plot.Left, y), new Point(plot.Right, y));
                dc.DrawLine(AxisPen, new Point(plot.Left - TickLen, y), new Point(plot.Left, y));

                // 값이 없을 때는 숫자를 적지 않는다 — 자리를 채우려고 만든 0~1 범위가
                // "속도가 0~1 m/s" 로 읽힌다. 격자만 남겨 빈 그래프 모양은 유지한다.
                if (n > 0)
                    dc.DrawText(yTexts[i], new Point(plot.Left - TickLen - LabelGap - yTexts[i].Width,
                                                     y - yTexts[i].Height / 2));
            }

            dc.DrawLine(AxisPen, new Point(Snap(plot.Left), plot.Top), new Point(Snap(plot.Left), plot.Bottom));
            dc.DrawLine(AxisPen, new Point(plot.Left, Snap(plot.Bottom)), new Point(plot.Right, Snap(plot.Bottom)));

            DrawXLabels(dc, plot, n, xLabelH);
            DrawTitles(dc, plot, w, h, yTitleW, xTitleH);

            if (n == 0)
            {
                if (!string.IsNullOrEmpty(EmptyText))
                {
                    var ft = Text(EmptyText!, FontSize, EmptyBrsh);
                    dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2,
                                              plot.Top + (plot.Height - ft.Height) / 2));
                }
                return;
            }

            DrawSeries(dc, plot, values!, n, lo, hi);
        }

        /// <summary>카테고리 중심 X — 첫/끝 점이 축선에 딱 붙지 않게 한 칸의 가운데에 놓는다.</summary>
        private static double CategoryX(int i, int n, Rect plot) =>
            plot.Left + plot.Width * (i + 0.5) / n;

        private static double ValueToY(double v, double lo, double hi, Rect plot) =>
            plot.Bottom - (v - lo) / (hi - lo) * plot.Height;

        // 1px 선이 두 픽셀에 반씩 걸려 흐려지는 것을 막는다.
        private static double Snap(double v) => Math.Floor(v) + 0.5;

        private void DrawXLabels(DrawingContext dc, Rect plot, int n, double xLabelH)
        {
            if (n == 0) return;
            var labels = Labels;

            // 다 적으면 겹친다 → 최소 간격이 나오도록 건너뛴다. 노즐이 몇 개든 읽히게 하기 위함.
            int step = Math.Max(1, (int)Math.Ceiling(MinLabelGap / (plot.Width / n)));

            for (int i = 0; i < n; i += step)
            {
                string s = labels != null && i < labels.Count ? labels[i] : i.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(s)) continue;

                double cx = CategoryX(i, n, plot);
                var ft = Text(s, FontSize, AxisText);
                double tx = cx - ft.Width / 2;

                // 양끝 라벨이 그래프 밖으로 새지 않게 가둔다
                tx = Math.Max(0, Math.Min(tx, plot.Right - ft.Width));
                dc.DrawLine(AxisPen, new Point(Snap(cx), plot.Bottom), new Point(Snap(cx), plot.Bottom + TickLen));
                dc.DrawText(ft, new Point(tx, plot.Bottom + LabelGap + TickLen - 1));
            }
        }

        private void DrawTitles(DrawingContext dc, Rect plot, double w, double h, double yTitleW, double xTitleH)
        {
            if (!string.IsNullOrEmpty(XAxisTitle))
            {
                var ft = Text(XAxisTitle!, TitleSize, AxisText);
                dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, h - ft.Height - 1));
            }

            if (!string.IsNullOrEmpty(YAxisTitle))
            {
                var ft = Text(YAxisTitle!, TitleSize, AxisText);
                // 세로쓰기 — plot 세로 가운데에 아래에서 위로.
                double cx = yTitleW - 2;
                double cy = plot.Top + (plot.Height + ft.Width) / 2;
                dc.PushTransform(new RotateTransform(-90, cx, cy));
                dc.DrawText(ft, new Point(cx, cy - ft.Height));
                dc.Pop();
            }
        }

        private void DrawSeries(DrawingContext dc, Rect plot, IReadOnlyList<double> values, int n,
                                double lo, double hi)
        {
            var brush = LineBrush ?? Brushes.White;
            var pen = new Pen(brush, 1.6);
            pen.Freeze();

            var pts = new Point[n];
            for (int i = 0; i < n; i++)
                pts[i] = new Point(CategoryX(i, n, plot), ValueToY(values[i], lo, hi, plot));

            if (n > 1)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(pts[0], false, false);
                    g.PolyLineTo(pts[1..], true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }

            // 점을 찍는 이유: 노즐이 적을 때 꺾은선만으로는 표본이 몇 개인지 안 보인다.
            foreach (var p in pts) dc.DrawEllipse(PointFill, pen, p, MarkerR, MarkerR);
        }

        /// <summary>값 범위. 비었거나 전부 같으면 눈금이 생기도록 벌린다.</summary>
        private static (double lo, double hi) Range(IReadOnlyList<double>? values, int n)
        {
            if (values == null || n == 0) return (0, 1);

            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double v = values[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            if (lo > hi) return (0, 1);                                  // 전부 NaN
            if (Math.Abs(hi - lo) < 1e-9)                                // 전부 같은 값
            {
                double pad = Math.Abs(hi) > 1e-9 ? Math.Abs(hi) * 0.1 : 1;
                return (lo - pad, hi + pad);
            }
            return (lo, hi);
        }

        /// <summary>1·2·5×10ⁿ 간격의 눈금. 축 숫자가 3.7142 처럼 나오면 읽을 수 없다.</summary>
        private static double[] NiceTicks(double lo, double hi, int target)
        {
            if (hi <= lo || target < 2) return new[] { lo, hi };

            double raw  = (hi - lo) / target;
            double mag  = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;

            double first = Math.Floor(lo / step) * step;
            double last  = Math.Ceiling(hi / step) * step;

            var list = new List<double>();
            // 부동소수 누적 오차로 마지막 눈금이 빠지는 것을 막으려고 개수로 돈다.
            int count = (int)Math.Round((last - first) / step);
            for (int i = 0; i <= count; i++)
            {
                double v = first + step * i;
                list.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);             // -0 방지
            }
            return list.Count >= 2 ? list.ToArray() : new[] { lo, hi };
        }

        /// <summary>눈금 간격에 필요한 소수 자릿수. 간격이 0.25 면 2자리, 5 면 0자리.</summary>
        private static int TickDecimals(double[] ticks)
        {
            if (ticks.Length < 2) return 2;
            double step = Math.Abs(ticks[1] - ticks[0]);
            if (step <= 0) return 2;

            for (int d = 0; d <= 4; d++)
                if (Math.Abs(step * Math.Pow(10, d) - Math.Round(step * Math.Pow(10, d))) < 1e-6) return d;
            return 4;
        }
    }
}
