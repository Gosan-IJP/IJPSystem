using System.Windows;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// "Set size of canvas" 다이얼로그. 캔버스 크기(mm, 최대 200×200)를 입력받는다.
    /// DialogResult=true 시 WidthMm/LengthMm 유효값 보장.
    /// </summary>
    public partial class CanvasSizeDialog : Window
    {
        public const double MaxMm = 200.0;

        public double WidthMm { get; private set; }
        public double LengthMm { get; private set; }

        public CanvasSizeDialog()
        {
            InitializeComponent();
            WidthBox.Focus();
            WidthBox.SelectAll();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(WidthBox.Text.Trim(), out double w) ||
                !double.TryParse(LengthBox.Text.Trim(), out double l))
            {
                ErrorText.Text = "숫자를 입력하세요.";
                return;
            }
            if (w <= 0 || l <= 0)
            {
                ErrorText.Text = "크기는 0보다 커야 합니다.";
                return;
            }
            if (w > MaxMm || l > MaxMm)
            {
                ErrorText.Text = $"최대 {MaxMm}mm × {MaxMm}mm 입니다.";
                return;
            }
            WidthMm = w;
            LengthMm = l;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
