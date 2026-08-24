using IJPSystem.Platform.HMI.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class DropWatcherView : UserControl
    {
        /// <summary>떠 있는 조그 창. 이 화면을 나가면 같이 닫는다.</summary>
        private MotorJogWindow? _jog;

        public DropWatcherView()
        {
            InitializeComponent();

            // 화면을 벗어나면 조그 창도 닫는다 — 드랍와처를 보려고 띄운 창이므로
            // 화면이 바뀌면 남아 있을 이유가 없고, 어느 화면에 딸린 창인지 헷갈리면
            // 서 있는 줄 알고 축을 놓치게 된다.
            Unloaded += (s, e) => CloseJog();
        }

        // ── Nozzle Select (모달 창) ───────────────────────────────────
        // 패턴인쇄 화면과 동일한 창. 선택 결과는 NozzleControlGlobal 싱글턴에 저장되므로
        // 어느 화면에서 열든 사용 노즐 설정은 하나로 공유된다.
        private void NozzleSelect_Click(object sender, RoutedEventArgs e)
        {
            var win = new IJPSystem.Platform.HMI.Nozzle.NozzleSelectWindow
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
        }

        // ── 모터 조그 (모달 아님) ─────────────────────────────────────
        // ShowDialog 가 아니라 Show 다. 영상을 보면서 조그해야 하므로 뒤 화면이 계속 살아 있어야 한다.
        private void MotorJog_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DropWatcherViewModel vm) return;

            // 이미 떠 있으면 새로 만들지 않고 앞으로 가져온다 — 창이 둘이면
            // 어느 쪽 단위 설정이 살아 있는지 알 수 없다.
            if (_jog != null)
            {
                if (_jog.WindowState == WindowState.Minimized) _jog.WindowState = WindowState.Normal;
                _jog.Activate();
                return;
            }

            _jog = new MotorJogWindow(vm.MainVM) { Owner = Window.GetWindow(this) };
            _jog.Closed += (_, _) => _jog = null;   // 사용자가 직접 닫은 경우
            _jog.Show();
        }

        private void CloseJog()
        {
            var win = _jog;
            _jog = null;
            win?.Close();   // 닫히면서 축을 세운다(MotorJogWindow.OnClosed)
        }
    }
}
