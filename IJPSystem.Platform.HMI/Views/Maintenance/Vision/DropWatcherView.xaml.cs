using System.Windows.Controls;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class DropWatcherView : UserControl
    {
        public DropWatcherView()
        {
            InitializeComponent();
        }

        // ── Nozzle Select (모달 창) ───────────────────────────────────
        // 패턴인쇄 화면과 동일한 창. 선택 결과는 NozzleControlGlobal 싱글턴에 저장되므로
        // 어느 화면에서 열든 사용 노즐 설정은 하나로 공유된다.
        private void NozzleSelect_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var win = new IJPSystem.Platform.HMI.Nozzle.NozzleSelectWindow
            {
                Owner = System.Windows.Window.GetWindow(this)
            };
            win.ShowDialog();
        }
    }
}
