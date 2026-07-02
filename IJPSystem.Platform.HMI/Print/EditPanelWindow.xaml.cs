using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "Edit Panel" (Drawing Panel.vi) — 빈 캔버스에 도형/자유선을 그려 패턴 BMP 를 만든다.
    /// Create Empty Layer → 캔버스 크기 다이얼로그 → 이 창.
    /// </summary>
    public partial class EditPanelWindow : Window
    {
        private enum Tool { None, Pen, Line, Rectangle, Diamond, Ellipse, Eraser }

        private readonly double _widthMm, _lengthMm, _dpi;
        private readonly int _pxW, _pxH;

        private Tool _tool = Tool.Pen;
        private bool _drawing;
        private Point _start;
        private Shape? _shape;        // line/rect/ellipse/diamond
        private Polyline? _stroke;    // pen/eraser

        // Undo/Redo (추가 순서 기준)
        private readonly List<UIElement> _added = new();
        private readonly Stack<UIElement> _redo = new();

        public EditPanelWindow(double widthMm, double lengthMm, double dpi)
        {
            InitializeComponent();
            _widthMm = widthMm; _lengthMm = lengthMm; _dpi = dpi <= 0 ? 600 : dpi;

            _pxW = (int)Math.Round(_widthMm * _dpi / 25.4);
            _pxH = (int)Math.Round(_lengthMm * _dpi / 25.4);

            // 화면 표시 크기(비율 유지, 긴 변 760)
            const double maxDisp = 760.0;
            double aspect = _widthMm / _lengthMm;
            double dispW, dispH;
            if (aspect >= 1) { dispW = maxDisp; dispH = maxDisp / aspect; }
            else { dispH = maxDisp; dispW = maxDisp * aspect; }
            DrawCanvas.Width = dispW;
            DrawCanvas.Height = dispH;

            StatusInfo.Text = $"{_pxW}x{_pxH}  {_dpi:0}DPI  {_widthMm:0.##}x{_lengthMm:0.##}mm  32-bit RGB";
            UpdateLineWidthMm();
            UpdateBoundaryMm();
        }

        // ── 도구 선택 ────────────────────────────────────────────────
        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag &&
                Enum.TryParse(tag, out Tool t))
                _tool = t;
        }

        private double LineWidth =>
            double.TryParse(LineWidthBox.Text, out double v) && v > 0 ? v : 1;

        // ── 캔버스 드로잉 ────────────────────────────────────────────
        private void DrawCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_tool == Tool.None) return;

            _start = e.GetPosition(DrawCanvas);
            _drawing = true;
            DrawCanvas.CaptureMouse();

            if (_tool == Tool.Pen || _tool == Tool.Eraser)
            {
                _stroke = new Polyline
                {
                    Stroke = _tool == Tool.Eraser ? Brushes.White : Brushes.Black,
                    StrokeThickness = _tool == Tool.Eraser ? LineWidth * 3 : LineWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _stroke.Points.Add(_start);
                DrawCanvas.Children.Add(_stroke);
            }
            else
            {
                _shape = _tool switch
                {
                    Tool.Line => new Line { Stroke = Brushes.Black, StrokeThickness = LineWidth, X1 = _start.X, Y1 = _start.Y, X2 = _start.X, Y2 = _start.Y },
                    Tool.Diamond => new Polygon { Stroke = Brushes.Black, StrokeThickness = LineWidth, Fill = Brushes.Transparent },
                    Tool.Ellipse => new Ellipse { Stroke = Brushes.Black, StrokeThickness = LineWidth, Fill = Brushes.Transparent },
                    _ => new Rectangle { Stroke = Brushes.Black, StrokeThickness = LineWidth, Fill = Brushes.Transparent }
                };
                if (_shape is not Line) { Canvas.SetLeft(_shape, _start.X); Canvas.SetTop(_shape, _start.Y); }
                DrawCanvas.Children.Add(_shape);
            }
        }

        private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(DrawCanvas);
            StatusInfo.Text = $"{_pxW}x{_pxH}  {_dpi:0}DPI  {_widthMm:0.##}x{_lengthMm:0.##}mm   ({(int)(p.X / DrawCanvas.Width * _pxW)},{(int)(p.Y / DrawCanvas.Height * _pxH)})";
            if (!_drawing) return;

            if (_stroke != null)
            {
                _stroke.Points.Add(p);
            }
            else if (_shape is Line line)
            {
                line.X2 = p.X; line.Y2 = p.Y;
            }
            else if (_shape is Polygon poly)
            {
                double l = Math.Min(_start.X, p.X), t = Math.Min(_start.Y, p.Y);
                double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
                Canvas.SetLeft(poly, 0); Canvas.SetTop(poly, 0);
                poly.Points = new PointCollection
                {
                    new Point(l + w / 2, t), new Point(l + w, t + h / 2),
                    new Point(l + w / 2, t + h), new Point(l, t + h / 2)
                };
            }
            else if (_shape != null)
            {
                double l = Math.Min(_start.X, p.X), t = Math.Min(_start.Y, p.Y);
                Canvas.SetLeft(_shape, l); Canvas.SetTop(_shape, t);
                _shape.Width = Math.Abs(p.X - _start.X);
                _shape.Height = Math.Abs(p.Y - _start.Y);
            }
        }

        private void DrawCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;
            DrawCanvas.ReleaseMouseCapture();

            UIElement? el = (UIElement?)_stroke ?? _shape;
            if (el != null) { _added.Add(el); _redo.Clear(); }
            _stroke = null; _shape = null;
        }

        // ── 액션 버튼 ────────────────────────────────────────────────
        private void ApplyDraw_Click(object sender, RoutedEventArgs e)
            => StatusInfo.Text = $"Apply Draw — 요소 {_added.Count}개 반영";

        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            DrawCanvas.Children.Clear();
            _added.Clear(); _redo.Clear();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_added.Count == 0) return;
            var el = _added[_added.Count - 1];
            _added.RemoveAt(_added.Count - 1);
            DrawCanvas.Children.Remove(el);
            _redo.Push(el);
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0) return;
            var el = _redo.Pop();
            DrawCanvas.Children.Add(el);
            _added.Add(el);
        }

        private void Fill_Click(object sender, RoutedEventArgs e)
            => StatusInfo.Text = "Fill — 영역 채움(구현 예정)";

        private void AutoFill_Click(object sender, RoutedEventArgs e)
            => StatusInfo.Text = "Auto Fill — 자동 채움(구현 예정)";

        private void PatternDittering_Click(object sender, RoutedEventArgs e)
            => StatusInfo.Text = "Pattern Dittering — 디더링/패턴 변환(구현 예정)";

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save BMP",
                Filter = "BMP (*.bmp)|*.bmp",
                FileName = $"Empty BMP_{DateTime.Now:yyMMdd_HHmmss}.bmp"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                // 표시 캔버스를 목표 픽셀(mm×DPI)로 스케일 렌더 → BMP
                var rtb = new RenderTargetBitmap(_pxW, _pxH, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var brush = new VisualBrush(DrawCanvas) { Stretch = Stretch.Fill };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, _pxW, _pxH));
                }
                rtb.Render(visual);

                var enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(dlg.FileName);
                enc.Save(fs);

                StatusInfo.Text = "저장 완료: " + dlg.FileName;
            }
            catch (Exception ex)
            {
                StatusInfo.Text = "저장 실패: " + ex.Message;
            }
        }

        // ── 입력 변환 표시 ───────────────────────────────────────────
        private void LineWidth_Changed(object sender, TextChangedEventArgs e) => UpdateLineWidthMm();
        private void Boundary_Changed(object sender, TextChangedEventArgs e) => UpdateBoundaryMm();

        private void UpdateLineWidthMm()
        {
            if (LineWidthMm == null) return;
            double px = double.TryParse(LineWidthBox.Text, out double v) ? v : 0;
            LineWidthMm.Text = $"{px * 25.4 / _dpi:0.0000} mm";
        }

        private void UpdateBoundaryMm()
        {
            if (BoundaryMm == null) return;
            double px = double.TryParse(BoundaryBox.Text, out double v) ? v : 0;
            BoundaryMm.Text = $"{px * 25.4 / _dpi:0.0000} mm";
        }
    }
}
