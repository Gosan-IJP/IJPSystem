using IJPSystem.Platform.Domain.Enums;
using System.Windows;
using System.Windows.Controls;

namespace IJPSystem.Platform.HMI.Common.Controls
{
    /// <summary>P&amp;ID 배럴(디스펜싱 시린지) — Pulse 단일 헤드용 잉크 배럴.
    /// 시린지 모양: flange + 원통 body + 어깨 콘 + 루어 팁.
    /// body 내부 y=26~180. LevelStatus 로 액위 표시, 좌측 2점 센서(High/Low).</summary>
    public partial class Barrel : UserControl
    {
        // body 내부 영역
        private const double BodyTop = 26;
        private const double BodyBottom = 180;
        private const double BodyHeight = BodyBottom - BodyTop; // 154

        public Barrel()
        {
            InitializeComponent();
            UpdateLiquid();
        }

        public string TagId   { get => (string)GetValue(TagIdProperty);   set => SetValue(TagIdProperty, value); }
        public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
        public LevelStatus LevelStatus { get => (LevelStatus)GetValue(LevelStatusProperty); set => SetValue(LevelStatusProperty, value); }
        public bool SensorHigh { get => (bool)GetValue(SensorHighProperty); set => SetValue(SensorHighProperty, value); }
        public bool SensorLow  { get => (bool)GetValue(SensorLowProperty);  set => SetValue(SensorLowProperty,  value); }

        public static readonly DependencyProperty TagIdProperty =
            DependencyProperty.Register(nameof(TagId), typeof(string), typeof(Barrel), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty CaptionProperty =
            DependencyProperty.Register(nameof(Caption), typeof(string), typeof(Barrel), new PropertyMetadata(string.Empty));
        // 기본값 Empty — 바인딩 평가 전에도 게이지 가시화
        public static readonly DependencyProperty LevelStatusProperty =
            DependencyProperty.Register(nameof(LevelStatus), typeof(LevelStatus), typeof(Barrel),
                new PropertyMetadata(LevelStatus.Empty, (d, _) => ((Barrel)d).UpdateLiquid()));
        public static readonly DependencyProperty SensorHighProperty =
            DependencyProperty.Register(nameof(SensorHigh), typeof(bool), typeof(Barrel), new PropertyMetadata(false));
        public static readonly DependencyProperty SensorLowProperty =
            DependencyProperty.Register(nameof(SensorLow), typeof(bool), typeof(Barrel), new PropertyMetadata(false));

        private void UpdateLiquid()
        {
            if (Liquid == null || Wave == null) return;

            double ratio = LevelStatus switch
            {
                LevelStatus.HH    => 0.92,
                LevelStatus.High  => 0.78,
                LevelStatus.Set   => 0.50,
                LevelStatus.Low   => 0.25,
                LevelStatus.Empty => 0.06,
                _                 => 0.06,
            };
            double h    = BodyHeight * ratio;
            double topY = BodyBottom - h;

            Canvas.SetTop(Liquid, topY);
            Liquid.Height = h;
            // 물결(메니스커스)을 액면 위에 살짝 얹음
            Canvas.SetTop(Wave, topY - 3);
        }
    }
}
