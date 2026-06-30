using System.Windows;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// Nozzle Select 모달 창. 로직은 NozzleSelectViewModel(MVVM)에 있다.
    /// </summary>
    public partial class NozzleSelectWindow : Window
    {
        public NozzleSelectWindow()
        {
            InitializeComponent();
            DataContext = new NozzleSelectViewModel();
        }
    }
}
