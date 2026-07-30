using IJPSystem.Platform.Common.Utilities;
using System;
using System.Windows;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>
    /// ViewModel/Code-behind 에서 리소스 딕셔너리 문자열을 가져오는 헬퍼.
    /// System.Windows.Application.Current.Resources 에 로드된 언어 파일(ko-KR / en-US)을 그대로 사용합니다.
    /// </summary>
    public static class Loc
    {
        /// <summary>키에 해당하는 현재 언어 문자열을 반환합니다.</summary>
        public static string T(string key)
            => System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

        /// <summary>키에 해당하는 문자열을 string.Format으로 포매팅합니다.</summary>
        public static string T(string key, params object[] args)
        {
            string template = T(key);
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException ex)
            {
                LoggerService.WriteToFile("WARN", $"Loc.T format error for key '{key}' (args={args.Length}): {ex.Message}");
                return template;
            }
        }

        // ── 로그 전용: UI 언어와 무관하게 항상 한국어(ko-KR) 로 남긴다 ──
        // 로그는 유지보수/추적용이라 선택 언어(일본어 등)로 번역되면 안 됨.
        private static ResourceDictionary? _koDict;

        private static ResourceDictionary KoDict
            => _koDict ??= new ResourceDictionary
            {
                Source = new Uri("Common/Resources/Languages/ko-KR.xaml", UriKind.Relative)
            };

        /// <summary>키에 해당하는 한국어(ko-KR) 문자열을 반환합니다. 로그 전용.</summary>
        public static string TLog(string key)
            => KoDict[key] as string ?? key;

        /// <summary>키에 해당하는 한국어 문자열을 string.Format으로 포매팅합니다. 로그 전용.</summary>
        public static string TLog(string key, params object[] args)
        {
            string template = TLog(key);
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException ex)
            {
                LoggerService.WriteToFile("WARN", $"Loc.TLog format error for key '{key}' (args={args.Length}): {ex.Message}");
                return template;
            }
        }
    }
}
