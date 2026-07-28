using IJPSystem.Platform.HMI.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class GlassView : UserControl
    {
        public GlassView()
        {
            InitializeComponent();
        }

        // ── 조그 (누르는 동안 이동 / 떼면 정지) ────────────────────────────────
        // 모터 제어 화면(MotorControlView)과 같은 규약: 버튼 Tag 로 대상 축을 정하고,
        // MouseDown 에서 JogMoveAsync, MouseUp 에서 StopAsync 를 호출한다.
        // 커맨드 대신 마우스 이벤트를 쓰는 이유 — 연속 조그는 "떼는 시점"이 필요하다.

        private AxisViewModel? ResolveAxis(object sender)
        {
            if (DataContext is not GlassViewModel vm) return null;

            return ((sender as Button)?.Tag?.ToString()?.ToUpperInvariant()) switch
            {
                "X" => vm.AxisX,
                "Y" => vm.AxisY,
                "Z" => vm.AxisZ,
                "T" => vm.AxisT,
                _   => null,
            };
        }

        private void ExecuteJog(object sender, bool isForward)
        {
            var axis = ResolveAxis(sender);
            if (axis == null || !axis.CanJog) return;   // 버튼은 이미 비활성이지만 안전망
            _ = axis.JogMoveAsync(isForward);
        }

        private void JogForward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, true);

        private void JogBackward_MouseDown(object sender, MouseButtonEventArgs e)
            => ExecuteJog(sender, false);

        private void JogStop_MouseUp(object sender, MouseButtonEventArgs e)
            => _ = ResolveAxis(sender)?.StopAsync();
    }
}
