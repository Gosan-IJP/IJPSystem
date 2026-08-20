using IJPSystem.Platform.Common.Constants;
using System;
using System.IO;

namespace IJPSystem.Platform.Common.Utilities
{
    /// <summary>Config 파일 경로 해석 유틸리티</summary>
    public static class PathUtils
    {
        /// <summary>
        /// Config 폴더 내 파일의 절대 경로를 반환합니다.
        /// DEBUG: 프로젝트 루트/Config → 없으면 실행 파일 옆 Config
        /// RELEASE: 실행 파일 옆 Config
        /// </summary>
        public static string GetConfigPath(string fileName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

#if DEBUG
            string projectRoot = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\"));
            string debugPath   = Path.Combine(projectRoot, AppConstants.ConfigFolder, fileName);
            if (File.Exists(debugPath)) return debugPath;
#endif
            return Path.Combine(baseDir, AppConstants.ConfigFolder, fileName);
        }

        /// <summary>
        /// 설정에 적힌 경로를 실제 경로로 푼다.
        /// <list type="bullet">
        ///   <item>비어 있으면 <paramref name="fallbackFileName"/> 을 Config 폴더에서 찾는다</item>
        ///   <item>절대 경로면 그대로 쓴다(제어 PC 의 Meteor 설치 폴더를 그대로 가리킬 수 있게)</item>
        ///   <item>상대 경로면 Config 폴더 기준</item>
        /// </list>
        /// </summary>
        public static string ResolveConfigPath(string? configured, string fallbackFileName)
        {
            if (string.IsNullOrWhiteSpace(configured))
                return GetConfigPath(fallbackFileName);

            // 설정 파일에는 '/' 로 적는 경우가 많다. 섞인 채로 두면 화면·로그·경로 비교가
            // 제각각이 된다(같은 파일인데 문자열이 달라 다른 파일로 보인다).
            string p = configured!.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(p)) return Path.GetFullPath(p);

            // "Config\PccE\x.cfg" 처럼 적어도 Config 가 두 번 붙지 않게 한다.
            string prefix = AppConstants.ConfigFolder + Path.DirectorySeparatorChar;
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                p = p[prefix.Length..];

            return Path.GetFullPath(GetConfigPath(p));
        }
    }
}
