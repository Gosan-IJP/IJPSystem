using IJPSystem.Platform.HMI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.Views
{
    /// <summary>
    /// 조그 팝업 — 다른 화면을 보면서 축을 움직이려고 띄운다.
    ///
    /// <para>
    /// 모달이 아니다(<c>Show</c>). 드랍와처 영상을 보면서 조그해야 하므로 뒤 화면이 계속
    /// 살아 있어야 한다. 대신 <c>Owner</c> 를 잡아 본창 뒤로 숨지 않고, 본창이 닫히면 같이 닫힌다.
    /// </para>
    /// </summary>
    public partial class MotorJogWindow : Window
    {
        public MotorJogWindow(MainViewModel mainVM)
        {
            InitializeComponent();
            DataContext = new MotorJogViewModel(mainVM);
        }

        private MotorJogViewModel? Vm => DataContext as MotorJogViewModel;

        // ── 조그 (누르는 동안 이동 / 떼면 정지) ────────────────────────────────
        // 모터 제어 화면과 같은 규약: 버튼 Tag 로 대상 축을 정하고,
        // MouseDown 에서 JogMoveAsync, MouseUp 에서 StopAsync 를 호출한다.

        private AxisViewModel? ResolveAxis(object sender)
        {
            if (Vm is not { } vm) return null;

            return ((sender as Button)?.Tag?.ToString()?.ToUpperInvariant()) switch
            {
                "X"    => vm.AxisX,
                "Z"    => vm.AxisZ,
                "DW-X" => vm.AxisDwX,
                "DW-Y" => vm.AxisDwY,
                _      => null,
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

        /// <summary>
        /// 창이 닫히면 축을 세운다.
        ///
        /// <para>연속 조그는 버튼에서 손을 떼야 멈춘다. 그런데 누른 채로 창이 닫히면
        /// MouseUp 이 이 창에 오지 않아 <b>축이 계속 움직인다</b> — 화면을 벗어나서 닫히는
        /// 경우도 마찬가지다. 닫히는 길이 무엇이든 여기서 한 번 세운다.</para>
        /// </summary>
        protected override void OnClosed(System.EventArgs e)
        {
            if (Vm is { } vm)
            {
                foreach (var ax in vm.Axes)
                {
                    try { _ = ax.StopAsync(); }
                    catch { /* 한 축이 실패해도 나머지는 세운다 */ }
                }
            }
            base.OnClosed(e);
        }
    }
}
