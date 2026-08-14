using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;

namespace IJPSystem.Platform.HMI.Common.Converters
{
    /// <summary>
    /// <see cref="TriggerLamp"/> → 표시등 색.
    ///
    /// <para>회색(미기동)을 빨강과 <b>반드시</b> 구분한다 — "안 돌리는 중" 과 "돌렸는데 끊김" 이
    /// 같은 색이면 표시등이 아무 정보도 주지 못한다. 노랑은 "오긴 오는데 수가 안 맞는다" 로,
    /// 볼 곳이 빨강과 완전히 다르다(배선/설정 vs 프레임 누락).</para>
    /// </summary>
    public sealed class TriggerLampToBrushConverter : IValueConverter
    {
        // Freeze 해 둔다 — 초당 2회 갱신되는 바인딩이라 매번 새 브러시를 만들면 낭비다.
        private static readonly SolidColorBrush Idle = Frozen("#475569");
        private static readonly SolidColorBrush Ok   = Frozen("#22C55E");
        private static readonly SolidColorBrush Warn = Frozen("#F59E0B");
        private static readonly SolidColorBrush Fail = Frozen("#EF4444");

        private static SolidColorBrush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is TriggerLamp lamp
                ? lamp switch
                {
                    TriggerLamp.Ok   => Ok,
                    TriggerLamp.Warn => Warn,
                    TriggerLamp.Fail => Fail,
                    _                => Idle,
                }
                : Idle;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
