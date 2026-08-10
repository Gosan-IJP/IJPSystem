using System;
using System.Windows;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// Nozzle Select 모달 창. 로직은 NozzleSelectViewModel(MVVM)에 있다.
    /// 막대의 드래그·호버는 커맨드로 표현하기 어색한 연속 동작이라 이벤트로 받아 VM 에 넘긴다
    /// (조그 버튼과 같은 규약).
    /// </summary>
    public partial class NozzleSelectWindow : Window
    {
        /// <summary>XAML 의 Height 가 담고 있는 열 수. 이보다 많으면 늘어난 만큼만 더한다.</summary>
        private const int BaselineRows = 2;

        public NozzleSelectWindow()
        {
            InitializeComponent();

            var vm = new NozzleSelectViewModel();
            DataContext = vm;

            Strip.RangeToggled += (_, e) => vm.ToggleRange(e.From, e.To, e.Add);
            Strip.Hovered      += (_, n) => vm.SetHover(n);

            FitHeightToRows(vm.Rows);
        }

        /// <summary>
        /// 열 수만큼 창을 세로로 늘린다. 막대는 열마다 <see cref="NozzleStrip.RowPitchPx"/> 씩
        /// 커지는데 창이 고정이면 4열부터 아래 버튼이 잘려, 열 때마다 손으로 늘려야 했다.
        /// SizeToContent 를 쓰지 않는 이유: CenterOwner 와 같이 쓰면 창이 중앙에서 어긋난다.
        /// </summary>
        private void FitHeightToRows(int rows)
        {
            double want = Height + Math.Max(0, rows - BaselineRows) * NozzleStrip.RowPitchPx;

            // 화면 밖으로 나가면 늘려 준 의미가 없다 — 작업 영역 안에서 멈춘다.
            Height = Math.Max(MinHeight, Math.Min(want, SystemParameters.WorkArea.Height - 60));
        }
    }
}
