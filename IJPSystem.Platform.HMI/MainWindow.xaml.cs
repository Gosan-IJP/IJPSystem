using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.ViewModels;
using static IJPSystem.Platform.HMI.Common.Loc;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace IJPSystem.Platform.HMI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // 창 제목에 본체 DLL 의 수정시각을 붙인다 — 실장에서 DLL 을 복사한 뒤
            // 로그를 열지 않고도 "그 파일이 실제로 도는지"를 바로 확인하기 위해서다.
            Title = $"{Title} — {BuildInfo.Stamp}";
            BuildStampText.Text = BuildInfo.Stamp;

            // DLL 을 손으로 복사하다 일부만 바꾸면 어긋난 조합이 되고, 그 조합은 한참 뒤
            // 엉뚱한 화면에서 MethodNotFound 로 죽는다. 여기서 눈에 띄게 세워 둔다.
            string? mismatch = BuildInfo.MismatchSummary();
            if (mismatch != null)
            {
                BuildStampText.Text = $"⚠ {BuildInfo.Stamp} · 빌드 불일치";
                BuildStampText.Foreground = System.Windows.Media.Brushes.Tomato;
                BuildStampText.ToolTip = mismatch;
                IJPSystem.Platform.Common.Utilities.LoggerService.WriteToFile("WARN", "[BOOT] " + mismatch);
            }
        }
        // 로그가 추가될 때마다 스크롤을 끝으로 내리는 핸들러
        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange > 0)
                (sender as ScrollViewer)?.ScrollToEnd();
        }

        // 모든 종료 경로(메뉴 EXIT / X 버튼 / Alt+F4)의 단일 확인 지점
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            var vm = DataContext as MainViewModel;

            // 운전 중 종료 차단 — 메인 대시보드 Auto Print / Sequence·Pnid 화면의 Initialize·Purge 등
            // 어느 경로로 실행 중이든 데이터 유실·라인 정지 방지를 위해 종료를 막고 사유 안내
            if (vm?.IsOperationRunning == true)
            {
                Dialogs.Show(T("Msg_ExitBlockedRunning"), T("Msg_ExitBlockedTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }

            var result = Dialogs.Show(T("Msg_ExitConfirm"), T("Msg_ExitTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }


            vm?.OnApplicationClosing();
            // 드라이버 정리(IO/Motion/Vision)는 App.OnExit가 처리
        }
    }
}