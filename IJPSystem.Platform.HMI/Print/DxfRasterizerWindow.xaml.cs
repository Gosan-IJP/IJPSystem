using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IJPSystem.Platform.HMI.Nozzle;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "DXF Rasterizer" 모달 창. Print Image Design 버튼이 띄운다.
    /// 로직은 DxfRasterizerViewModel(MVVM)에 있다.
    /// </summary>
    public partial class DxfRasterizerWindow : Window
    {
        public DxfRasterizerWindow(string? initialDxfPath = null)
        {
            InitializeComponent();

            var vm = new DxfRasterizerViewModel(new DxfRasterizer());

            // Nozzle Select → 노즐 선택 창을 띄우고, 선택 결과(전역)를 반환
            vm.NozzleSelectAction = () =>
            {
                var win = new NozzleSelectWindow { Owner = this };
                win.ShowDialog();
                return NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles;
            };

            // Create Empty Layer → 캔버스 크기 다이얼로그 → Edit Panel
            vm.RequestCanvasSize = () =>
            {
                var dlg = new CanvasSizeDialog { Owner = this };
                return dlg.ShowDialog() == true
                    ? (dlg.WidthMm, dlg.LengthMm)
                    : ((double, double)?)null;
            };
            vm.OpenEditPanel = (w, l, target) =>
            {
                var edit = new EditPanelWindow(w, l, vm.DropPerInchX, target) { Owner = this };
                edit.ShowDialog();
                return edit.SavedImagePath;   // 그린 것을 래스터라이저가 이어받는다
            };

            // 알림 상자 — 이 창이 모달이라 소유자를 여기서 잡아 줘야 뒤로 숨지 않는다.
            vm.Notify = (caption, text) =>
                MessageBox.Show(this, text, caption, MessageBoxButton.OK, MessageBoxImage.Information);

            // 창 열 때 현재 선택된 노즐 수 표시 + 초기 DXF 경로
            vm.InitUsingNozzles(NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles);

            // 넘겨받은 경로가 실제 DXF 면 바로 연다. 아니면 칸에만 채운다 —
            // 인쇄 데이터 경로가 폴더나 BMP 일 수도 있어 무턱대고 열면 창이 뜨자마자 오류가 난다.
            if (!string.IsNullOrWhiteSpace(initialDxfPath))
            {
                if (System.IO.File.Exists(initialDxfPath) &&
                    initialDxfPath!.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                    vm.LoadDxfFrom(initialDxfPath);
                else
                    vm.DxfPath = initialDxfPath!;
            }

            // Zoom To Fit 버튼과 새 이미지 도착을 같은 동작으로 묶는다.
            vm.ZoomToFitRequested += (_, _) => ZoomToFit();
            vm.PropertyChanged += Vm_PropertyChanged;

            DataContext = vm;
        }

        private DxfRasterizerViewModel? Vm => DataContext as DxfRasterizerViewModel;

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DxfRasterizerViewModel.PreviewImage):
                    ZoomToFit();                 // 새 그림은 일단 전체가 보여야 한다
                    break;
                case nameof(DxfRasterizerViewModel.DropPerInchX):
                case nameof(DxfRasterizerViewModel.DropPerInchY):
                case nameof(DxfRasterizerViewModel.ShowGrid):
                case nameof(DxfRasterizerViewModel.MeasuredLengthMm):
                    UpdatePreviewLayout();       // 격자 간격은 DPI 에서, 읽음표는 이 값들에서 나온다
                    break;
            }
        }

        // ──────────── 미리보기 확대/이동 ────────────
        // 이미지 좌상단의 화면 좌표(_ox,_oy)와 배율(_zoom) 두 값이 전부다.
        // 격자·측정선도 같은 값에서 그리므로 셋이 어긋날 수 없다.
        private double _zoom = 1, _ox, _oy;
        private bool  _panning;
        private Point _dragFrom;
        private double _dragOx, _dragOy;

        private const double MinZoom = 0.02, MaxZoom = 40.0;

        private double ImgW => Vm?.PreviewWidthPx  ?? 0;
        private double ImgH => Vm?.PreviewHeightPx ?? 0;

        /// <summary>이미지 전체가 보이도록 배율·위치를 다시 잡는다.</summary>
        private void ZoomToFit()
        {
            double vw = PreviewViewport.ActualWidth, vh = PreviewViewport.ActualHeight;
            if (ImgW <= 0 || ImgH <= 0 || vw <= 0 || vh <= 0) { UpdatePreviewLayout(); return; }

            const double pad = 12;
            _zoom = Math.Max(MinZoom, Math.Min((vw - pad) / ImgW, (vh - pad) / ImgH));
            CenterImage();
        }

        private void CenterImage()
        {
            _ox = (PreviewViewport.ActualWidth  - ImgW * _zoom) / 2;
            _oy = (PreviewViewport.ActualHeight - ImgH * _zoom) / 2;
            UpdatePreviewLayout();
        }

        /// <summary>배율/위치를 화면에 반영한다 — 이미지·격자·측정선·읽음표를 한 번에.</summary>
        private void UpdatePreviewLayout()
        {
            PreviewScale.ScaleX = PreviewScale.ScaleY = _zoom;
            PreviewPan.X = _ox;
            PreviewPan.Y = _oy;

            // 크게 볼 때는 보간하지 않는다 — 방울 하나하나를 보려고 확대하는 것이다.
            RenderOptions.SetBitmapScalingMode(PreviewImg,
                _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

            double w = ImgW * _zoom, h = ImgH * _zoom;

            GridOverlay.OriginX    = _ox;
            GridOverlay.OriginY    = _oy;
            GridOverlay.AreaWidth  = w;
            GridOverlay.AreaHeight = h;
            GridOverlay.PitchX     = MmInPixels(Vm?.DropPerInchX ?? 0) * _zoom;
            GridOverlay.PitchY     = MmInPixels(Vm?.DropPerInchY ?? 0) * _zoom;

            MeasureLine.X1 = _ox;      MeasureLine.Y1 = _oy + h;
            MeasureLine.X2 = _ox + w;  MeasureLine.Y2 = _oy;

            ZoomBadge.Text = ImgW <= 0
                ? ""
                : $"{_zoom * 100:0}%   대각선 {Vm?.MeasuredLengthMm ?? 0:F3}mm" +
                  (Vm?.ShowGrid == true ? "   격자 1mm" : "");
        }

        /// <summary>이미지에서 1mm 가 몇 픽셀인지. 격자 간격의 기준.</summary>
        private static double MmInPixels(double dpi) => dpi > 0 ? dpi / 25.4 : 0;

        private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e) => ZoomToFit();

        private void PreviewViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ImgW <= 0) return;
            double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
            ZoomAt(e.GetPosition(PreviewViewport), _zoom * factor);
            e.Handled = true;
        }

        /// <summary>커서 아래 화소가 제자리에 남도록 확대한다 — 안 그러면 볼 곳을 계속 놓친다.</summary>
        private void ZoomAt(Point at, double newZoom)
        {
            newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
            if (Math.Abs(newZoom - _zoom) < 1e-9) return;

            double ix = (at.X - _ox) / _zoom, iy = (at.Y - _oy) / _zoom;
            _zoom = newZoom;
            _ox = at.X - ix * _zoom;
            _oy = at.Y - iy * _zoom;
            UpdatePreviewLayout();
        }

        private Point ViewportCenter =>
            new(PreviewViewport.ActualWidth / 2, PreviewViewport.ActualHeight / 2);

        private void ZoomIn_Click(object sender, RoutedEventArgs e)     => ZoomAt(ViewportCenter, _zoom * 1.5);
        private void ZoomOut_Click(object sender, RoutedEventArgs e)    => ZoomAt(ViewportCenter, _zoom / 1.5);
        private void ZoomActual_Click(object sender, RoutedEventArgs e) => ZoomAt(ViewportCenter, 1.0);
        private void ZoomToFit_Click(object sender, RoutedEventArgs e)  => ZoomToFit();

        private void PreviewViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ImgW <= 0) return;
            _panning  = true;
            _dragFrom = e.GetPosition(PreviewViewport);
            _dragOx   = _ox;
            _dragOy   = _oy;
            PreviewViewport.CaptureMouse();
            PreviewViewport.Cursor = Cursors.ScrollAll;
        }

        private void PreviewViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning) return;
            var p = e.GetPosition(PreviewViewport);
            _ox = _dragOx + (p.X - _dragFrom.X);
            _oy = _dragOy + (p.Y - _dragFrom.Y);
            UpdatePreviewLayout();
        }

        private void PreviewViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_panning) return;
            _panning = false;
            PreviewViewport.ReleaseMouseCapture();
            PreviewViewport.Cursor = Cursors.Arrow;
        }

        /// <summary>
        /// 패턴 미리보기 — 변환한 그 패턴을 그대로 띄운다.
        ///
        /// <para>변환 전이라면 이미지에서 만들어 볼 수 있게 예전처럼 연다. 다만 그때는
        /// 창 안의 값으로 다시 RIP 하는 것이라 화면 설정과 다를 수 있다.</para>
        /// </summary>
        private void PatternPreview_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as DxfRasterizerViewModel;
            var pattern = vm?.LastPattern;

            var win = pattern != null
                ? new PatternPreviewWindow(pattern, vm!.LastLayout, vm.LastIgnoredNozzles,
                                           $"변환 결과 — {System.IO.Path.GetFileName(vm.BmpPath)}")
                : new PatternPreviewWindow(vm?.BmpPath,
                                           NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles);

            win.Owner = this;
            win.ShowDialog();
        }
    }
}
