using System.Windows;
using System.Windows.Controls;

namespace IJPSystem.Platform.HMI.Common.Controls
{
    /// <summary>
    /// 글라스 정렬 카메라(GVC) 컨트롤 — 몸체 + 아래를 보는 렌즈 + 세로 라벨.
    ///
    /// <para>메인 대시보드처럼 Canvas 위에 <c>Canvas.Left/Top</c> 만 주면 놓인다.
    /// 헤드와 달리 <b>움직이지 않는다</b> — 이동 변환을 걸지 않는 것이 정상이다.</para>
    /// </summary>
    public partial class AlignCamera : UserControl
    {
        public AlignCamera()
        {
            InitializeComponent();
        }

        /// <summary>몸체에 세로로 찍히는 이름(CAM, GVC ...). 글자 수가 늘면 아래로 쌓인다.</summary>
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(AlignCamera),
                new PropertyMetadata("CAM"));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }
    }
}
