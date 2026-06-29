using System;
using System.Windows;
using System.Windows.Controls;

namespace IJPSystem.Platform.HMI.Common.Controls
{
    /// <summary>의료용 10cc 주사기 컨트롤. VolumeCc(0~10) 로 플런저 위치와 액량을 표시.
    /// 배럴 내부 y=40~200(높이 160).</summary>
    public partial class Syringe10cc : UserControl
    {
        private const double BarrelTop = 40;
        private const double BarrelBottom = 200;
        private const double BarrelHeight = BarrelBottom - BarrelTop; // 160
        private const double MaxCc = 10.0;
        private const double SealHeight = 8;
        private const double RodTop = 10;

        public Syringe10cc()
        {
            InitializeComponent();
            UpdateVolume();
        }

        public string TagId   { get => (string)GetValue(TagIdProperty);   set => SetValue(TagIdProperty, value); }
        public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
        public double VolumeCc { get => (double)GetValue(VolumeCcProperty); set => SetValue(VolumeCcProperty, value); }

        public static readonly DependencyProperty TagIdProperty =
            DependencyProperty.Register(nameof(TagId), typeof(string), typeof(Syringe10cc), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty CaptionProperty =
            DependencyProperty.Register(nameof(Caption), typeof(string), typeof(Syringe10cc), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty VolumeCcProperty =
            DependencyProperty.Register(nameof(VolumeCc), typeof(double), typeof(Syringe10cc),
                new PropertyMetadata(5.0, (d, _) => ((Syringe10cc)d).UpdateVolume()));

        private void UpdateVolume()
        {
            if (Liquid == null || Seal == null || PlungerRod == null) return;

            double v = Math.Max(0, Math.Min(MaxCc, VolumeCc));
            double sealY = BarrelBottom - (v / MaxCc) * BarrelHeight; // 액면(=씰 하단) 위치

            // 액체: 씰 아래 ~ 배럴 바닥
            Canvas.SetTop(Liquid, sealY);
            Liquid.Height = BarrelBottom - sealY;

            // 씰: 액면 위에 얹음
            Canvas.SetTop(Seal, sealY - SealHeight);

            // 플런저 로드: 썸레스트(=RodTop) ~ 씰
            PlungerRod.Height = Math.Max(0, (sealY - SealHeight) - RodTop);
        }
    }
}
