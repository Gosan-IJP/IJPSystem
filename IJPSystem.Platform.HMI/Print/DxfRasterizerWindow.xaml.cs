using System.Windows;
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
            vm.OpenEditPanel = (w, l) =>
            {
                var edit = new EditPanelWindow(w, l, vm.DropPerInchX) { Owner = this };
                edit.ShowDialog();
            };

            // 창 열 때 현재 선택된 노즐 수 표시 + 초기 DXF 경로
            vm.InitUsingNozzles(NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles);
            if (!string.IsNullOrWhiteSpace(initialDxfPath)) vm.DxfPath = initialDxfPath!;

            DataContext = vm;
        }

        /// <summary>
        /// 패턴 미리보기 — 지금 선택된 노즐과 변환된 BMP 로 발사 지도를 만들어 보여 준다.
        /// BMP 가 아직 없으면 창에서 직접 이미지를 고를 수 있다.
        /// </summary>
        private void PatternPreview_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as DxfRasterizerViewModel;
            new PatternPreviewWindow(vm?.BmpPath,
                                     NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles)
            {
                Owner = this
            }.ShowDialog();
        }
    }
}
