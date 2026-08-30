using System;
using System.Windows;
using System.Windows.Threading;
using IJPSystem.Platform.Application.Printing;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 인쇄 원점 설정 모달. (LabVIEW "21_Screen_Set Print Origin.vi")
    /// 현재 스테이지 위치를 실시간 표시하고, Set 으로 그 위치를 인쇄 원점으로 확정한다.
    /// 로직은 HW 무관한 <see cref="PrintOriginManager"/>(Platform.Application) 에 있다.
    /// </summary>
    public partial class PrintOriginWindow : Window
    {
        private readonly PrintOriginManager _mgr;
        private readonly DispatcherTimer _timer;

        /// <summary>어디에 적히는지 — 창을 열 때와 저장 성공 뒤에 이 문구로 되돌린다.</summary>
        private const string Destination =
            "레시피 티칭의 PRINT ORIGIN (X·Y) 에 저장됩니다 — Z 는 티칭값 그대로.";

        public PrintOriginWindow(PrintOriginManager manager)
        {
            InitializeComponent();
            _mgr = manager ?? throw new ArgumentNullException(nameof(manager));

            // 창을 열 때마다 티칭값을 다시 읽는다. 관리자는 처음 만들 때 한 번만 읽는데,
            // 그 사이 티칭 화면에서 자리를 옮겼으면 여기에는 옛 값이 뜬다 — 그리고 그
            // 어긋남은 인쇄가 엉뚱한 자리에서 시작해야 드러난다.
            _mgr.Load();


            ShowOrigin();

            // 현재 위치 실시간 갱신(약 5fps). 창이 닫히면 정지한다.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_, _) => ShowCurrent();
            _timer.Start();
            ShowCurrent();

            Closed += (_, _) => _timer.Stop();
        }

        private void ShowCurrent()
        {
            var p = _mgr.GetCurrentPosition();
            CurX.Text = $"{p.X:F3} mm";
            CurY.Text = $"{p.Y:F3} mm";
            CurZ.Text = $"{p.Z:F3} mm";
        }

        private void ShowOrigin()
        {
            var p = _mgr.PrintOrigin;
            OrgX.Text = $"{p.X:F3} mm";
            OrgY.Text = $"{p.Y:F3} mm";
            OrgZ.Text = $"{p.Z:F3} mm";
        }

        private void Set_Click(object sender, RoutedEventArgs e)
        {
            // 되돌리기가 없는 저장이다 — 레시피 티칭의 PRINT ORIGIN 을 그 자리에서 덮어쓴다.
            // 옛 값이 무엇이었는지 함께 보여야, 실수로 눌렀을 때 그 자리에서 알아챈다.
            var now = _mgr.GetCurrentPosition();
            var old = _mgr.PrintOrigin;

            var answer = MessageBox.Show(
                this,
                "현재 스테이지 위치를 인쇄 원점으로 저장할까요?\n\n" +
                $"    X   {old.X:F3}  →  {now.X:F3} mm\n" +
                $"    Y   {old.Y:F3}  →  {now.Y:F3} mm\n\n" +
                "레시피 티칭의 PRINT ORIGIN 이 바뀝니다. (Z 는 그대로)",
                "인쇄 원점 저장",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);   // 기본은 취소 — 엔터를 잘못 쳐서 덮어쓰지 않도록

            if (answer != MessageBoxResult.OK) return;

            _mgr.SetPrintOrigin();   // 현재 위치 → 원점 확정 + 저장(관리자가 처리)
            ShowOrigin();

            // 저장이 실패했는데 화면만 바뀌면 다음 인쇄에서야 드러난다.
            OrgWhere.Text = string.IsNullOrEmpty(_mgr.LastError)
                ? Destination
                : "저장 실패 — " + _mgr.LastError;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _mgr.ResetToDefault();
            ShowOrigin();
        }
    }
}
