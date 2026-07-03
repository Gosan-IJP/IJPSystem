using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// DrawingPanelView 코드비하인드.
    /// 그리기 상호작용(클릭/드래그/ROI)과 비트맵 렌더링을 담당(=View 책임).
    /// 로직은 DrawingPanelViewModel 에 있음.
    ///
    /// 사용 예:
    ///   var vm = new DrawingPanelViewModel(5);
    ///   vm.OnApplyDraw = m =&gt; rasterizer.ApplyDrawnPattern(m);
    ///   vm.OnSave      = m =&gt; SavePattern(m);
    ///   view.DataContext = vm;
    /// </summary>
    public partial class DrawingPanelView : UserControl
    {
        private DrawingPanelViewModel? _vm;
        private WriteableBitmap? _bmp;
        private bool _dragging;
        private (int r, int c) _roiStart;

        public DrawingPanelView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += (s, e) => Render();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null) _vm.Changed -= Render;
            _vm = DataContext as DrawingPanelViewModel;
            if (_vm != null) _vm.Changed += Render;
            _bmp = null;
            Render();
        }

        // ---- 좌표 변환: 마우스 → (row, col) ----
        private bool TryGetCell(Point p, out int r, out int c)
        {
            r = c = 0;
            if (_vm == null) return false;
            double w = CanvasHost.ActualWidth, h = CanvasHost.ActualHeight;
            if (w <= 0 || h <= 0) return false;
            c = (int)(p.X / (w / _vm.Grid.Cols));
            r = (int)(p.Y / (h / _vm.Grid.Rows));
            if (r < 0 || c < 0 || r >= _vm.Grid.Rows || c >= _vm.Grid.Cols) return false;
            return true;
        }

        private void CanvasHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            var p = e.GetPosition(CanvasHost);
            _dragging = true;
            CanvasHost.CaptureMouse();
            _vm.BeginStroke();

            if (_vm.Mode == DrawMode.RoiFill)
            {
                if (TryGetCell(p, out int r, out int c)) _roiStart = (r, c);
                RoiRect.Visibility = Visibility.Visible;
                UpdateRoiRect(p, p);
            }
            else if (TryGetCell(p, out int rr, out int cc))
            {
                _vm.PaintAt(rr, cc);
            }
        }

        private void CanvasHost_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _vm == null) return;
            var p = e.GetPosition(CanvasHost);

            if (_vm.Mode == DrawMode.RoiFill)
            {
                var start = CellTopLeft(_roiStart.r, _roiStart.c);
                UpdateRoiRect(start, p);
            }
            else if (TryGetCell(p, out int r, out int c))
            {
                _vm.PaintAt(r, c); // 드래그 연속 그리기
            }
        }

        private void CanvasHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || _vm == null) return;
            _dragging = false;
            CanvasHost.ReleaseMouseCapture();

            if (_vm.Mode == DrawMode.RoiFill)
            {
                var p = e.GetPosition(CanvasHost);
                if (TryGetCell(p, out int r, out int c))
                    _vm.CommitRoi(_roiStart.r, _roiStart.c, r, c);
                RoiRect.Visibility = Visibility.Collapsed;
            }
        }

        private Point CellTopLeft(int r, int c)
        {
            double cw = CanvasHost.ActualWidth / _vm!.Grid.Cols;
            double ch = CanvasHost.ActualHeight / _vm!.Grid.Rows;
            return new Point(c * cw, r * ch);
        }

        private void UpdateRoiRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            Canvas.SetLeft(RoiRect, x);
            Canvas.SetTop(RoiRect, y);
            RoiRect.Width = Math.Abs(a.X - b.X);
            RoiRect.Height = Math.Abs(a.Y - b.Y);
        }

        // ---- 비트맵 렌더링 (1px = 1셀, NearestNeighbor 확대) ----
        private void Render()
        {
            if (_vm == null) return;
            int rows = _vm.Grid.Rows, cols = _vm.Grid.Cols;
            if (_bmp == null || _bmp.PixelWidth != cols || _bmp.PixelHeight != rows)
            {
                _bmp = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
                CanvasImage.Source = _bmp;
            }

            int stride = cols * 4;
            byte[] px = new byte[rows * stride];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * stride + c * 4;
                    byte v = _vm.Grid.Get(r, c) ? (byte)0 : (byte)255; // on=검정, off=흰색
                    px[i + 0] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
                }
            _bmp.WritePixels(new Int32Rect(0, 0, cols, rows), px, stride, 0);
        }
    }
}
