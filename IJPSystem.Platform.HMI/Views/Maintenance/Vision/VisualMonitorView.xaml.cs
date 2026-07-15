using IJPSystem.Platform.HMI.ViewModels;
using System;
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
        private double _panX0, _panY0;   // 드래그 시작 시점의 Translate 값

        /// <summary>
        /// 내장 툴바(소스/툴/크로스/줌) 표시 여부. 화면이 자체 툴바를 갖고 있으면 False 로 두고
        /// 뷰 영역만 사용한다(패턴인쇄 화면).
        /// </summary>
        public static readonly DependencyProperty ShowToolbarProperty =
            DependencyProperty.Register(nameof(ShowToolbar), typeof(bool), typeof(VisualMonitorView),
                new PropertyMetadata(true));

        public bool ShowToolbar
        {
            get => (bool)GetValue(ShowToolbarProperty);
            set => SetValue(ShowToolbarProperty, value);
        }

        public VisualMonitorView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) =>
            {
                if (_vm != null) { _vm.PropertyChanged -= OnVmChanged; _vm.FitRequested -= OnFitRequested; }
                _vm = DataContext as VisualMonitorViewModel;
                if (_vm != null) { _vm.PropertyChanged += OnVmChanged; _vm.FitRequested += OnFitRequested; }
                FitToWindow();
                UpdateCross();
            };
            // 화면에 보일 때만 라이브 캡쳐 — 화면을 벗어나면 정지해 불필요한 폴링/디스크 쓰기를 막는다.
            // (PatternPrintViewModel 은 캐시되어 Dispose 되지 않으므로 이 제어가 없으면 영구 폴링된다)
            Loaded += (s, e) => { UpdateCross(); _vm?.Start(); };
            Unloaded += (s, e) => _vm?.Stop();
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
                if (_vm.Zoom <= 1.0) { PanT.X = 0; PanT.Y = 0; }   // 맞춤 배율로 돌아오면 이동도 초기화
            }
        }

        private void OnFitRequested(object? s, EventArgs e) => FitToWindow();

        /// <summary>
        /// 화면 맞춤 — Stretch=Uniform 이라 배율 1 이 곧 "비율 유지한 채 패널을 채운 상태"다.
        /// 이동(Translate)도 함께 초기화한다.
        /// </summary>
        private void FitToWindow()
        {
            PanT.X = 0; PanT.Y = 0;
            if (_vm != null) _vm.Zoom = 1.0;
            ZoomT.ScaleX = 1.0; ZoomT.ScaleY = 1.0;
        }

        // 십자선 위치/표시 갱신 (화면 좌표 기준)
        private void UpdateCross()
        {
            if (_vm == null) return;
            // 기준은 ViewHost — SizeChanged 시점에 자식(Canvas)의 ActualWidth 는 아직 갱신 전일 수 있다.
            double w = ViewHost.ActualWidth, h = ViewHost.ActualHeight;
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
            { _panStart = p; _panX0 = PanT.X; _panY0 = PanT.Y; }
            else if (_vm.Tool == ViewTool.Zoom)
                _vm.Zoom *= e.ChangedButton == MouseButton.Right ? 1 / 1.25 : 1.25;   // 좌클릭 확대 / 우클릭 축소
        }

        private void Host_Move(object s, MouseEventArgs e)
        {
            if (!_dragging || _vm == null) return;
            var p = e.GetPosition(ViewHost);
            if (_vm.Tool == ViewTool.Select && _vm.CrossLineVisible)
                SetCrossFromPoint(p);
            else if (_vm.Tool == ViewTool.Pan)
            {
                // 확대 상태에서만 의미 있음(맞춤 배율이면 이미 전체가 보임)
                PanT.X = _panX0 + (p.X - _panStart.X);
                PanT.Y = _panY0 + (p.Y - _panStart.Y);
            }
        }

        private void Host_Up(object s, MouseButtonEventArgs e)
        { _dragging = false; ViewHost.ReleaseMouseCapture(); }

        // 휠은 툴 모드와 무관하게 항상 확대/축소 (뷰어 관례)
        private void Host_Wheel(object s, MouseWheelEventArgs e)
        {
            if (_vm == null) return;
            _vm.Zoom *= e.Delta > 0 ? 1.1 : 0.9;
            e.Handled = true;
        }

        private void SetCrossFromPoint(Point p)
        {
            double w = ViewHost.ActualWidth, h = ViewHost.ActualHeight;
            if (w <= 0 || h <= 0) return;
            _vm!.CrossXRatio = p.X / w;
            _vm.CrossYRatio = p.Y / h;
        }
    }
}
