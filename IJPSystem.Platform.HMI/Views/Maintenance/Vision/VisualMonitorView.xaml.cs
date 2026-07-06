using IJPSystem.Platform.HMI.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Views
{
    /// <summary>
    /// VisualMonitorView 코드비하인드.
    /// 십자선(크로스라인) 렌더/이동, 줌(툴), 팬(드래그) 상호작용 담당.
    /// </summary>
    public partial class VisualMonitorView : UserControl
    {
        private VisualMonitorViewModel? _vm;
        private bool _dragging;
        private Point _panStart;
        private double _panH, _panV;

        public VisualMonitorView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) =>
            {
                if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
                _vm = DataContext as VisualMonitorViewModel;
                if (_vm != null) _vm.PropertyChanged += OnVmChanged;
                UpdateCross();
            };
            Loaded += (s, e) => UpdateCross();
        }

        private void OnVmChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(VisualMonitorViewModel.CrossLineVisible)
                or nameof(VisualMonitorViewModel.CrossXRatio)
                or nameof(VisualMonitorViewModel.CrossYRatio))
                UpdateCross();
            else if (e.PropertyName == nameof(VisualMonitorViewModel.Zoom) && _vm != null)
            {
                ZoomT.ScaleX = _vm.Zoom;
                ZoomT.ScaleY = _vm.Zoom;
            }
        }

        // 십자선 위치/표시 갱신 (화면 좌표 기준)
        private void UpdateCross()
        {
            if (_vm == null) return;
            double w = CrossOverlay.ActualWidth, h = CrossOverlay.ActualHeight;
            var vis = _vm.CrossLineVisible ? Visibility.Visible : Visibility.Collapsed;
            CrossV.Visibility = CrossH.Visibility = vis;
            if (w <= 0 || h <= 0) return;

            double x = _vm.CrossXRatio * w, y = _vm.CrossYRatio * h;
            CrossV.X1 = x; CrossV.X2 = x; CrossV.Y1 = 0; CrossV.Y2 = h;
            CrossH.Y1 = y; CrossH.Y2 = y; CrossH.X1 = 0; CrossH.X2 = w;
        }

        private void Host_SizeChanged(object s, SizeChangedEventArgs e) => UpdateCross();

        private void Host_Down(object s, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            var p = e.GetPosition(ViewHost);
            _dragging = true; ViewHost.CaptureMouse();

            if (_vm.Tool == ViewTool.Select && _vm.CrossLineVisible)
                SetCrossFromPoint(p);
            else if (_vm.Tool == ViewTool.Pan)
            { _panStart = p; _panH = Scroll.HorizontalOffset; _panV = Scroll.VerticalOffset; }
            else if (_vm.Tool == ViewTool.Zoom)
                _vm.Zoom *= 1.25;   // 클릭 확대
        }

        private void Host_Move(object s, MouseEventArgs e)
        {
            if (!_dragging || _vm == null) return;
            var p = e.GetPosition(ViewHost);
            if (_vm.Tool == ViewTool.Select && _vm.CrossLineVisible)
                SetCrossFromPoint(p);
            else if (_vm.Tool == ViewTool.Pan)
            {
                Scroll.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
                Scroll.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
            }
        }

        private void Host_Up(object s, MouseButtonEventArgs e)
        { _dragging = false; ViewHost.ReleaseMouseCapture(); }

        private void Host_Wheel(object s, MouseWheelEventArgs e)
        {
            if (_vm == null || _vm.Tool != ViewTool.Zoom) return;
            _vm.Zoom *= e.Delta > 0 ? 1.1 : 0.9;
            e.Handled = true;
        }

        private void SetCrossFromPoint(Point p)
        {
            double w = CrossOverlay.ActualWidth, h = CrossOverlay.ActualHeight;
            if (w <= 0 || h <= 0) return;
            _vm!.CrossXRatio = p.X / w;
            _vm.CrossYRatio = p.Y / h;
        }
    }
}
