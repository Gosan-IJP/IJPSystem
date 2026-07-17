using System;
using System.Globalization;
using System.Windows.Data;

namespace IJPSystem.Platform.HMI.Common.Converters
{
    /// <summary>
    /// AxisNo → 스캔축(Y)이면 true. 인쇄 프로파일 편집 가능 여부 판정에 쓴다.
    /// (AutoPrintSequence.ScanAxisNo 와 동일한 축)
    /// </summary>
    public class IsScanAxisConverter : IValueConverter
    {
        private const string ScanAxis = "Y";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals(value?.ToString()?.Trim(), ScanAxis, StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
