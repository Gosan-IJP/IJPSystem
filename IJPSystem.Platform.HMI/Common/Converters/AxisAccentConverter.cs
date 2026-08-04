using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.Common.Converters
{
    /// <summary>
    /// 축 이름(AxisNo) → 표시 색상. ConverterParameter 로 용도를 고른다: "fg"(글자) / "bg"(배경) / "bd"(테두리).
    ///
    /// 축 구성이 config(MotorConfig.json) 기반이라 화면이 축을 하드코딩하지 않는데, 색까지 데이터로
    /// 들고 다니게 하면 config 가 표시 관심사로 오염된다. 그래서 색만 여기에 모아두고
    /// <b>목록에 없는 축은 기본 슬레이트</b>로 떨어뜨린다 — 축이 늘어도 화면은 깨지지 않는다.
    /// </summary>
    public class AxisAccentConverter : IValueConverter
    {
        private static readonly Dictionary<string, (string Fg, string Bg, string Bd)> Palette =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["X"]    = ("#22C55E", "#0B2818", "#166534"),
                ["Y"]    = ("#38BDF8", "#0C1F2E", "#1D4ED8"),
                ["Z"]    = ("#F59E0B", "#1C1200", "#92400E"),
                ["T"]    = ("#C084FC", "#1A0C2E", "#6D28D9"),
                ["DW-X"] = ("#2DD4BF", "#062B27", "#0F766E"),
                ["DW-Y"] = ("#F472B6", "#2A0E1D", "#9D174D"),
            };

        private static readonly (string Fg, string Bg, string Bd) Fallback = ("#94A3B8", "#161E2E", "#334155");

        // Brush 는 매 호출 새로 만들면 바인딩마다 객체가 쌓인다(위치 카드는 100ms 폴링 대상).
        // Freeze 해서 캐시 — 스레드 affinity 도 사라진다.
        private static readonly Dictionary<string, Brush> Cache = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var (fg, bg, bd) = Palette.TryGetValue(value as string ?? "", out var p) ? p : Fallback;
            string hex = (parameter as string)?.ToLowerInvariant() switch
            {
                "bg" => bg,
                "bd" => bd,
                _    => fg,
            };

            lock (Cache)
            {
                if (Cache.TryGetValue(hex, out var cached)) return cached;
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                Cache[hex] = brush;
                return brush;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
