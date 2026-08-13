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

        private readonly NozzleSelectViewModel _vm;

        public NozzleSelectWindow()
        {
            InitializeComponent();

            _vm = new NozzleSelectViewModel();
            DataContext = _vm;

            Strip.RangeToggled += (_, e) => _vm.ToggleRange(e.From, e.To, e.Add);
            Strip.Hovered      += (_, n) => _vm.SetHover(n);

            FitHeightToRows(_vm.Rows);
        }

        /// <summary>
        /// [확인] — 이때만 선택이 장비에 반영된다.
        /// 취소·Esc·창 닫기(X)는 아무것도 쓰지 않으므로 열기 전 선택이 그대로 남는다.
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _vm.Commit();

            // DialogResult 는 ShowDialog 로 연 창에서만 쓸 수 있다. 지금 호출부는 모두 모달이지만,
            // 누가 Show() 로 열면 여기서 예외가 나 확인 버튼이 죽는다 — 닫기는 어느 쪽이든 된다.
            try { DialogResult = true; }
            catch (InvalidOperationException) { /* 모달이 아니다 */ }
            Close();
        }

        /// <summary>
        /// 줄 수만큼 창을 세로로 늘려 <b>가능하면 막대 전체가 한눈에 보이게</b> 한다.
        ///
        /// <para>여기서 못 늘려도 잘리지는 않는다 — 막대 행만 <c>Height="*"</c> 라 남는 공간을
        /// 가져가고, 모자라면 막대 안에서 스크롤된다. 즉 이 계산은 <b>편의</b>일 뿐이고
        /// [확인]/[취소]가 보이는 것은 레이아웃이 보장한다.</para>
        ///
        /// <para>SizeToContent 를 쓰지 않는 이유: CenterOwner 와 같이 쓰면 창이 중앙에서 어긋난다.</para>
        /// </summary>
        private void FitHeightToRows(int rows)
        {
            // 기준(2줄)보다 늘어난 줄 수만큼. 상한을 두지 않는다 — 큰 화면에서는 8줄을 다 보여 주고,
            // 작은 화면에서는 아래 작업영역 제한에 걸린 뒤 막대가 스크롤로 처리한다.
            double stripGrowth = Math.Max(0, rows - BaselineRows) * NozzleStrip.RowPitchPx;

            // 칩·열 버튼 줄이 늘어난 만큼(칩이 없으면 열 줄 하나뿐).
            double buttonRows = _vm.HasChips ? 2 : 1;

            double want = Height + stripGrowth + buttonRows * GroupButtonRowPx;

            // 화면 밖으로 나가면 늘려 준 의미가 없다 — 작업 영역 안에서 멈춘다.
            Height = Math.Max(MinHeight, Math.Min(want, SystemParameters.WorkArea.Height - 60));
        }

        /// <summary>칩/열 버튼 한 줄이 차지하는 세로 크기(버튼 32 + 위 여백 6~8).</summary>
        private const double GroupButtonRowPx = 40;
    }
}
