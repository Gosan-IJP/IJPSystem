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

            // 화면에 보일 때만 라이브 — 들어오면 켜고 나가면 끈다(VisualMonitorView 와 같은 규약).
            Loaded   += (_, __) => (DataContext as GlassViewModel)?.Activate();
            Unloaded += (_, __) => (DataContext as GlassViewModel)?.Deactivate();
        }

        /// <summary>
        /// 노출 입력칸에서 Enter 를 치면 곧바로 적용한다.
        ///
        /// <para>바인딩은 기본값(LostFocus)으로 둔다 — PropertyChanged 로 두면 "15" 를 치는 동안
        /// 1ms → 15ms 로 <b>키를 누를 때마다</b> 카메라에 쓰기가 나간다. 대신 Enter 를 받아 준다:
        /// 값을 고치고 다른 곳을 눌러야 듣는 칸은, 안 듣는 칸으로 오해받는다.</para>
        /// </summary>
        private void ExposureBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox box) return;

            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
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
