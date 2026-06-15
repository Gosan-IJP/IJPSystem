using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class MotorControlView : UserControl
    {
        public MotorControlView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Tag 기반으로 대상 축을 찾습니다.
        /// Tag="SEL" 또는 Tag 없음  → SelectedAxis
        /// Tag="X"/"Y"/"Z"/"T"      → AxisList에서 이름에 해당 문자가 포함된 축
        /// </summary>
        private AxisViewModel? ResolveAxis(object sender)
        {
            if (DataContext is not MotorControlViewModel vm) return null;

            string? tag = (sender as Button)?.Tag?.ToString();

            if (string.IsNullOrEmpty(tag) || tag.Equals("SEL", StringComparison.OrdinalIgnoreCase))
                return vm.SelectedAxis;

            return vm.AxisList.FirstOrDefault(a =>
                a.Info?.Name != null &&
                a.Info.Name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void ExecuteJog(object sender, bool isForward)
        {
            if (DataContext is not MotorControlViewModel vm) return;

            var targetAxis = ResolveAxis(sender);
            if (targetAxis == null) return;

            // 서보 OFF·알람·미연결·이동명령(Move) 진행 중이면 조그 차단 (버튼은 이미 비활성이지만 안전망)
            if (!targetAxis.CanJog) return;

            // 선택된 축의 조그 단위를 대상 축에 동기화
            if (vm.SelectedAxis != null)
                targetAxis.JogUnit = vm.SelectedAxis.JogUnit;

            _ = targetAxis.JogMoveAsync(isForward, vm.JogSpeedScale);
        }

        private void JogForward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, true);

        private void JogBackward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, false);

        private void JogStop_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var targetAxis = ResolveAxis(sender);
            if (targetAxis != null)
                _ = targetAxis.StopAsync();
        }
    }
}
