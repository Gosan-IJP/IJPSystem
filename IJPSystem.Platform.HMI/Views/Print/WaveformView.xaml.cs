using IJPSystem.Platform.HMI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class WaveformView : UserControl
    {
        public WaveformView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private WaveformViewModel? _vm;

        private void OnDataContextChanged(object sender,
            System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
                _vm.ChartDataChanged -= OnChartDataChanged;

            _vm = e.NewValue as WaveformViewModel;

            if (_vm != null)
            {
                _vm.ChartDataChanged += OnChartDataChanged;
                ChartA.AxisTitle = "ComA Volts";
                ChartB.AxisTitle = "ComB Volts";
                // 자동 로드된 데이터가 있으면 즉시 그리기
                OnChartDataChanged();
            }
        }

        /// <summary>
        /// 채널마다 따로 그린다. 두 그래프는 <b>같은 시간 눈금</b>을 쓴다 —
        /// 각자 정하면 ComB 가 짧을 때 축이 달라져 위아래 모양을 비교할 수 없다.
        /// </summary>
        private void OnChartDataChanged()
        {
            if (_vm == null) return;

            double maxT = _vm.ChartMaxTimeUs;
            ChartA.FixedMaxTimeUs = maxT > 0 ? maxT : null;
            ChartB.FixedMaxTimeUs = maxT > 0 ? maxT : null;

            ChartA.Highlight = _vm.Editor.HighlightRangeComA;
            ChartB.Highlight = _vm.Editor.HighlightRangeComB;

            ChartA.Refresh(_vm.ComASeries);
            ChartB.Refresh(_vm.ComBSeries);
        }

        /// <summary>
        /// "Copy pulse to ..." — 버튼 아래로 대상 목록을 편다.
        /// 메뉴는 시각 트리 밖이라 DataContext 가 따라오지 않는다 — 여기서 넘겨준다.
        /// </summary>
        private void OnCopyPulseClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.ContextMenu is null) return;

            b.ContextMenu.PlacementTarget = b;
            b.ContextMenu.Placement       = PlacementMode.Bottom;
            b.ContextMenu.DataContext     = b.DataContext;
            b.ContextMenu.IsOpen          = true;
        }
    }
}
