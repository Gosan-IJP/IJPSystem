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
        public NozzleSelectWindow()
        {
            InitializeComponent();

            var vm = new NozzleSelectViewModel();
            DataContext = vm;

            Strip.RangeToggled += (_, e) => vm.ToggleRange(e.From, e.To, e.Add);
            Strip.Hovered      += (_, n) => vm.SetHover(n);
        }
    }
}
