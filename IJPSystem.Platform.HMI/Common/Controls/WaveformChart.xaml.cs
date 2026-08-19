using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IJPSystem.Platform.HMI.Common;

namespace IJPSystem.Platform.HMI.Common.Controls
{
    public partial class WaveformChart : UserControl
    {
        private const double PadLeft   = 38;
        private const double PadRight  = 10;
        private const double PadTop    = 8;
        private const double PadBottom = 20;

        private static readonly Brush GridBrush  = new SolidColorBrush(Color.FromArgb(45, 148, 163, 184));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
        private static readonly Brush AxisBrush  = new SolidColorBrush(Color.FromRgb(51, 65, 85));
        // 강조 구간 밖을 덮는 막 — 선이 사라지지 않을 만큼만 어둡게.
        private static readonly Brush MaskBrush     = new SolidColorBrush(Color.FromArgb(170, 6, 11, 20));
        private static readonly Brush HighlightEdge = new SolidColorBrush(Color.FromArgb(120, 148, 163, 184));

        public WaveformChart()
        {
            InitializeComponent();
        }

        private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

        public void Refresh(IReadOnlyList<WaveformSeries>? series)
        {
            _series = series;
            Refresh();
        }

        private IReadOnlyList<WaveformSeries>? _series;

        /// <summary>Y축 이름. 두 개를 위아래로 놓을 때 어느 채널인지 구분한다.</summary>
        public string AxisTitle
        {
            get => (string)PART_YTitle.Text;
            set => PART_YTitle.Text = value;
        }

        /// <summary>
        /// Graph Highlight — 강조할 시간 구간 [µs]. 그 구간만 밝게 두고 나머지를 덮는다.
        /// </summary>
        public (double StartUs, double EndUs)? Highlight { get; set; }

        /// <summary>
        /// 시간축 상한 고정 [µs]. 위아래 두 그래프가 <b>같은 눈금</b>을 써야 모양을 비교할 수 있다 —
        /// 각자 계산하면 ComB 가 짧을 때 시간축이 달라져 겹쳐 보이지 않는다.
        /// </summary>
        public double? FixedMaxTimeUs { get; set; }

        private void Refresh()
        {
            PART_Chart.Children.Clear();

            double w = PART_Chart.ActualWidth;
            double h = PART_Chart.ActualHeight;
            if (w < 10 || h < 10) return;

            double plotW = w - PadLeft - PadRight;
            double plotH = h - PadTop  - PadBottom;

            // ── 데이터 범위 ───────────────────────────────────────
            double maxT = 0;
            double maxV = 40;

            if (_series != null)
            {
                foreach (var s in _series.Where(s => s.IsVisible && s.Points.Count > 0))
                {
                    maxT = Math.Max(maxT, s.Points.Max(p => p.T));
                    maxV = Math.Max(maxV, s.Points.Max(p => p.V));
                }
            }

            if (maxT <= 0) maxT = 60;
            maxT = Math.Ceiling(maxT / 10.0) * 10 + 2;
            maxV = Math.Ceiling(maxV / 5.0) * 5;

            if (FixedMaxTimeUs is > 0) maxT = FixedMaxTimeUs.Value;

            // ── 그리드 + 레이블 ───────────────────────────────────
            DrawHGrid(plotW, plotH, 0, maxV);
            DrawVGrid(plotW, plotH, maxT);

            // ── 시리즈 ────────────────────────────────────────────
            if (_series != null)
            {
                foreach (var s in _series.Where(s => s.IsVisible && s.Points.Count > 0))
                    DrawSeries(s, plotW, plotH, 0, maxV, maxT);
            }

            // ── 강조 구간 ─────────────────────────────────────────
            // 고른 구간 밖을 덮는다. 시리즈보다 나중에 그려야 나머지가 흐려진다.
            DrawHighlightMask(plotW, plotH, maxT);

            // ── 축 선 ─────────────────────────────────────────────
            Add(new Line { X1 = PadLeft, Y1 = PadTop, X2 = PadLeft, Y2 = PadTop + plotH,
                Stroke = AxisBrush, StrokeThickness = 1.5 });
            Add(new Line { X1 = PadLeft, Y1 = PadTop + plotH, X2 = PadLeft + plotW, Y2 = PadTop + plotH,
                Stroke = AxisBrush, StrokeThickness = 1.5 });
        }

        // ─────────────────────────────────────────────────────────────────
        private void DrawHGrid(double plotW, double plotH, double minV, double maxV)
        {
            int step = 5;
            for (double v = minV; v <= maxV + 0.01; v += step)
            {
                double y = PadTop + plotH * (1.0 - (v - minV) / (maxV - minV));

                Add(new Line { X1 = PadLeft, X2 = PadLeft + plotW, Y1 = y, Y2 = y,
                    Stroke = GridBrush, StrokeThickness = 1 });

                var lbl = new TextBlock { Text = v.ToString("F0"), FontSize = 9.5,
                    Foreground = LabelBrush, TextAlignment = TextAlignment.Right, Width = PadLeft - 4 };
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, y - 7);
                PART_Chart.Children.Add(lbl);
            }
        }

        private void DrawVGrid(double plotW, double plotH, double maxT)
        {
            double step = NiceStep(maxT, 9);
            for (double t = 0; t <= maxT + step * 0.01; t += step)
            {
                double x = PadLeft + plotW * (t / maxT);

                Add(new Line { X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + plotH,
                    Stroke = GridBrush, StrokeThickness = 1 });

                var lbl = new TextBlock { Text = t.ToString("F0"), FontSize = 9.5, Foreground = LabelBrush };
                Canvas.SetLeft(lbl, x - 8);
                Canvas.SetTop(lbl, PadTop + plotH + 3);
                PART_Chart.Children.Add(lbl);
            }
        }

        private void DrawSeries(WaveformSeries s, double plotW, double plotH,
                                 double minV, double maxV, double maxT)
        {
            var poly = new Polyline
            {
                Stroke          = s.Stroke,
                StrokeThickness = s.StrokeThickness,
                StrokeDashArray = s.DashArray,
                StrokeLineJoin  = PenLineJoin.Round,
            };

            foreach (var (t, v) in s.Points)
            {
                double x = PadLeft + plotW * (t / maxT);
                double y = PadTop  + plotH * (1.0 - (v - minV) / (maxV - minV));
                poly.Points.Add(new Point(x, y));
            }

            PART_Chart.Children.Add(poly);
        }

        /// <summary>강조 구간 밖을 어둡게 덮는다. 구간이 없으면 아무것도 하지 않는다.</summary>
        private void DrawHighlightMask(double plotW, double plotH, double maxT)
        {
            if (Highlight is not { } h || maxT <= 0) return;
            if (h.EndUs <= h.StartUs) return;

            double x0 = PadLeft + plotW * Math.Clamp(h.StartUs / maxT, 0, 1);
            double x1 = PadLeft + plotW * Math.Clamp(h.EndUs   / maxT, 0, 1);

            AddMask(PadLeft, x0 - PadLeft);          // 왼쪽
            AddMask(x1, PadLeft + plotW - x1);       // 오른쪽

            // 강조 구간의 경계를 얇게 그어 어디를 보고 있는지 분명히 한다.
            foreach (double x in new[] { x0, x1 })
                Add(new Line { X1 = x, X2 = x, Y1 = PadTop, Y2 = PadTop + plotH,
                    Stroke = HighlightEdge, StrokeThickness = 1 });

            void AddMask(double left, double width)
            {
                if (width <= 0.5) return;
                var r = new Rectangle { Width = width, Height = plotH, Fill = MaskBrush };
                Canvas.SetLeft(r, left);
                Canvas.SetTop(r, PadTop);
                PART_Chart.Children.Add(r);
            }
        }

        private void Add(UIElement el) => PART_Chart.Children.Add(el);

        private static double NiceStep(double range, int targetSteps)
        {
            double rough = range / targetSteps;
            double[] niceVals = { 1, 2, 5, 10, 20, 25, 50, 100 };
            foreach (var n in niceVals)
                if (rough <= n) return n;
            return niceVals[^1];
        }
    }
}
