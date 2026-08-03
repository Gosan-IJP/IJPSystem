using System.Windows;

namespace IJPSystem.Platform.HMI.Views
{
    public partial class SplashWindow : Window
    {
        // 시각적 중심 비율. 정확히 가운데(0.5)에 두면 사람 눈에는 "아래로 처진" 느낌이 난다.
        // 로고·타이틀이 위쪽에 몰린 카드라 더 그렇다. 0.40 이면 살짝 위로 올라와 안정적으로 보인다.
        private const double VerticalRatio = 0.40;

        public SplashWindow()
        {
            InitializeComponent();

            // SizeToContent 라 최종 크기가 레이아웃 후에야 정해진다.
            // WindowStartupLocation=CenterScreen 은 그 전 크기 기준으로 위치를 잡아 어긋날 수 있어
            // 크기가 확정될 때마다 직접 배치한다. 단계가 추가되며 카드가 커져도 중심이 유지된다.
            SizeChanged += (_, _) => PlaceOnScreen();
            PlaceOnScreen();
        }

        private void PlaceOnScreen()
        {
            var area = SystemParameters.WorkArea;   // 작업 표시줄을 제외한 영역
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            Left = area.Left + (area.Width  - ActualWidth)  / 2;
            Top  = area.Top  + (area.Height - ActualHeight) * VerticalRatio;
        }
    }
}
