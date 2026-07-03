using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class PatternPrintView : UserControl
    {
        public PatternPrintView()
        {
            InitializeComponent();
        }

        /// <summary>버튼 Tag(X/Y/Z/T)로 대상 축을 찾는다. 일치 축이 없으면 null(무동작).</summary>
        private AxisViewModel? ResolveAxis(object sender)
        {
            if (DataContext is not PatternPrintViewModel vm) return null;
            string? tag = (sender as Button)?.Tag?.ToString();
            if (string.IsNullOrEmpty(tag)) return null;
            return vm.AxisList.FirstOrDefault(a =>
                a.Info?.Name != null &&
                a.Info.Name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void ExecuteJog(object sender, bool isForward)
        {
            if (DataContext is not PatternPrintViewModel vm) return;

            var axis = ResolveAxis(sender);
            if (axis == null) return;

            // 서보 OFF·알람·미연결·이동 중이면 조그 차단 (안전망)
            if (!axis.CanJog) return;

            axis.JogUnit = vm.JogUnit;                       // 선택된 UNIT 적용
            _ = axis.JogMoveAsync(isForward, vm.JogSpeedScale); // 선택된 SPEED 배율 적용
        }

        private void JogForward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, true);

        private void JogBackward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, false);

        private void JogStop_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var axis = ResolveAxis(sender);
            if (axis != null) _ = axis.StopAsync();
        }

        // ── Nozzle Select (모달 창) ───────────────────────────────────
        private void NozzleSelect_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var win = new IJPSystem.Platform.HMI.Nozzle.NozzleSelectWindow
            {
                Owner = System.Windows.Window.GetWindow(this)
            };
            win.ShowDialog();
        }

        // ── Print Image Design (DXF Rasterizer 모달 창) ───────────────
        private void PrintImageDesign_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            string? initialPath = (DataContext as PatternPrintViewModel)?.PrintDataPath;
            var win = new IJPSystem.Platform.HMI.Print.DxfRasterizerWindow(initialPath)
            {
                Owner = System.Windows.Window.GetWindow(this)
            };
            win.ShowDialog();
        }
    }
}
