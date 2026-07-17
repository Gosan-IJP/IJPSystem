using System;
using System.Globalization;
using System.Windows.Data;

namespace IJPSystem.Platform.HMI.Common.Converters
{
    /// <summary>
    /// (AxisNo, 값) → 스캔축(Y)이면 값 그대로, 그 외 축이면 "–".
    /// 인쇄(Printing) 프로파일은 스캔축(Y)만 사용하므로 X/Z/T 행의 PRINT 칸은
    /// 값 대신 "–"로 표시해 "해당 없음"임을 알린다.
    /// (AutoPrintSequence.ScanAxisNo 와 동일한 축 — 바뀌면 여기 ScanAxis 도 같이 수정)
    /// </summary>
    public class PrintAxisValueConverter : IMultiValueConverter
    {
        private const string ScanAxis = "Y";

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return "–";
            string axis = values[0]?.ToString()?.Trim() ?? "";
            if (!string.Equals(axis, ScanAxis, StringComparison.OrdinalIgnoreCase)) return "–";
            return values[1]?.ToString() ?? "–";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
