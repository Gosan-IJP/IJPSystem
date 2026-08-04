using IJPSystem.Platform.HMI.ViewModels;
using System;
using System.Linq;
using System.Windows;
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
        /// 대상 축을 찾습니다(모든 축을 데이터기반으로 지원).
        /// ① 축 리스트(ItemsControl) 행의 버튼 → 그 행의 DataContext(AxisViewModel) 직접 사용 → 축 개수 무관.
        /// ② Tag="SEL" 또는 Tag 없음  → SelectedAxis
        /// ③ Tag="X"/"Y"/"Z"/"T"(D-패드) → 이름에 해당 문자가 포함된 축
        /// </summary>
        private AxisViewModel? ResolveAxis(object sender)
        {
            if (DataContext is not MotorControlViewModel vm) return null;

            // ① 축 리스트 행의 조그 버튼: DataContext 가 곧 그 축(AxisViewModel)이다.
            if ((sender as FrameworkElement)?.DataContext is AxisViewModel rowAxis)
                return rowAxis;

            string? tag = (sender as Button)?.Tag?.ToString();

            if (string.IsNullOrEmpty(tag) || tag.Equals("SEL", StringComparison.OrdinalIgnoreCase))
                return vm.SelectedAxis;

            // AxisNo 정확히 일치를 먼저 — "DW-X AXIS" 가 "X" 에 걸리면 XY 패드가 DW 축을 움직인다.
            // 포함 검색은 옛 이름 표기용 폴백(VM.ResolveByTag 과 같은 규칙).
            return vm.AxisList.FirstOrDefault(a => string.Equals(a.Info?.AxisNo, tag, StringComparison.OrdinalIgnoreCase))
                ?? vm.AxisList.FirstOrDefault(a =>
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

            // 스텝은 화면(VM)이 들고 있다 — 누른 축의 단위(mm/deg)에 맞는 값을 VM 이 골라 준다.
            _ = targetAxis.JogMoveAsync(isForward, vm.JogSpeedScale, vm.JogStepFor(targetAxis));
        }

        private void JogForward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, true);

        private void JogBackward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, false);

        // 연속(Cont.) 모드에서만 손을 떼면 정지한다(dead-man).
        // 스텝 모드에서 정지시키면 짧게 클릭했을 때 이동이 도중에 잘려 '눌렀는데 안 움직인다'가 된다.
        // 스텝 이동은 스스로 끝나므로 건드리지 않는다.
        private void JogStop_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MotorControlViewModel vm) return;
            var targetAxis = ResolveAxis(sender);
            if (targetAxis == null) return;
            if (vm.JogStepFor(targetAxis) <= 0) _ = targetAxis.StopAsync();
        }
    }
}
