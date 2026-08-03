using IJPSystem.Platform.Common.Constants;
using System;
using System.IO;
using System.Linq;

namespace IJPSystem.Platform.Common.Utilities
{
    /// <summary>
    /// 로그 폴더 보존 정책. <c>AppConfig.LogSaveDays</c> 를 실제로 적용한다.
    ///
    /// 이 기능이 없어 <c>C:\Logs</c> 가 무한히 늘어났다(2026-08-01 실측 584MB, 그중
    /// 비전 이미지 582MB). 설정값은 예전부터 있었지만 읽는 코드가 없었다.
    ///
    /// <para><b>삭제 대상</b> — 수정 시각이 보존 기간을 넘긴 것만:</para>
    /// <list type="bullet">
    ///   <item>로그 루트의 <c>*.txt</c>, <c>*.csv</c> (일자별 로그·내보내기 파일)</item>
    ///   <item><c>Vision\</c> 하위의 이미지(<c>*.bmp *.png *.jpg *.jpeg</c>) — 용량의 대부분</item>
    /// </list>
    /// <para><b>절대 건드리지 않는 것</b>: <c>*.db</c>(SystemLog/NozzleHealth 등 실행 중 DB),
    /// 그 외 알 수 없는 확장자. 로그 정리가 데이터를 지우는 사고로 번지면 안 된다.</para>
    /// </summary>
    public static class LogRetentionService
    {
        private static readonly string[] RootPatterns  = { "*.txt", "*.csv" };
        private static readonly string[] ImagePatterns = { "*.bmp", "*.png", "*.jpg", "*.jpeg" };

        /// <summary>
        /// 보존 기간이 지난 로그/이미지를 삭제한다. 기동 시 1회 백그라운드 호출을 전제로 한다.
        /// </summary>
        /// <param name="keepDays">보존 일수. 0 이하이면 정리하지 않는다(무제한 보존 의도).</param>
        public static void Cleanup(int keepDays)
        {
            if (keepDays <= 0) return;

            try
            {
                string root = AppConstants.LogFolder;
                if (!Directory.Exists(root)) return;

                var cutoff = DateTime.Now.AddDays(-keepDays);
                int files = 0;
                long bytes = 0;

                // 1) 로그 루트 — 텍스트/CSV 만
                foreach (var pattern in RootPatterns)
                    Sweep(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly),
                          cutoff, ref files, ref bytes);

                // 2) 비전 이미지 — 카메라별 하위 폴더까지
                string visionDir = Path.Combine(root, "Vision");
                if (Directory.Exists(visionDir))
                    foreach (var pattern in ImagePatterns)
                        Sweep(Directory.EnumerateFiles(visionDir, pattern, SearchOption.AllDirectories),
                              cutoff, ref files, ref bytes);

                if (files > 0)
                    WriteSummary($"[LOG] 보존 정리 — {keepDays}일 초과 {files}개 삭제 ({bytes / 1024.0 / 1024.0:F1}MB 확보)");
            }
            catch (Exception ex)
            {
                WriteSummary($"[LOG] 보존 정리 실패 — {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 파일 하나가 잠겨 있어도(사용 중) 나머지 정리는 계속되어야 한다.
        private static void Sweep(System.Collections.Generic.IEnumerable<string> paths,
                                  DateTime cutoff, ref int files, ref long bytes)
        {
            foreach (var path in paths.ToList())
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTime >= cutoff) continue;

                    long len = fi.Length;
                    fi.Delete();
                    files++;
                    bytes += len;
                }
                catch { /* 잠긴 파일 등 — 건너뛴다 */ }
            }
        }

        private static void WriteSummary(string message) => LoggerService.WriteToFile("INFO", message);
    }
}
