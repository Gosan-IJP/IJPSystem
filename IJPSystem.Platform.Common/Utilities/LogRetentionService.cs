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
    /// <para><b>삭제 대상</b> — 수정 시각이 보존 기간을 넘긴 것, 그리고 남은 총량이 상한
    /// (<c>AppConfig.LogMaxTotalMB</c>)을 넘으면 오래된 것부터(최근 24시간 제외):</para>
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

        /// <summary>용량 정리에서도 이보다 최근 파일은 지우지 않는다 — 상한을 작게 잡아도 오늘 로그는 남긴다.</summary>
        private static readonly TimeSpan ProtectRecent = TimeSpan.FromHours(24);

        /// <summary>
        /// 보존 기간이 지난 로그/이미지를 삭제하고, 그래도 용량 상한을 넘으면 오래된 것부터 더 지운다.
        /// 기동 시 1회 백그라운드 호출을 전제로 한다.
        ///
        /// <para><b>용량 상한을 더한 이유</b>(2026-09-13): 출하 후에는 고객이 한 달 넘게 지나 불량을 신고하는
        /// 일이 흔한데, 일수로만 지우면 그때는 로그가 이미 없다. 그렇다고 일수를 크게 잡으면 비전 이미지가
        /// 디스크를 채운다. 그래서 <b>일수는 넉넉히, 넘치는 것은 용량으로</b> 막는다 — 로그가 작은
        /// 장비는 오래 남고, 이미지를 많이 찍는 장비는 용량 안에서 최신 것부터 남는다.</para>
        /// </summary>
        /// <param name="keepDays">보존 일수. 0 이하이면 일수로는 지우지 않는다.</param>
        /// <param name="maxTotalMb">정리 대상 파일의 총량 상한[MB]. 0 이하이면 용량으로는 지우지 않는다.</param>
        public static void Cleanup(int keepDays, int maxTotalMb = 0)
        {
            if (keepDays <= 0 && maxTotalMb <= 0) return;

            try
            {
                string root = AppConstants.LogFolder;
                if (!Directory.Exists(root)) return;

                // 1) 일수 — 수정 시각이 보존 기간을 넘긴 것
                if (keepDays > 0)
                {
                    var cutoff = DateTime.Now.AddDays(-keepDays);
                    int files = 0;
                    long bytes = 0;
                    Sweep(Candidates(root), cutoff, ref files, ref bytes);

                    if (files > 0)
                        WriteSummary($"[LOG] 보존 정리 — {keepDays}일 초과 {files}개 삭제 ({bytes / 1024.0 / 1024.0:F1}MB 확보)");
                }

                // 2) 용량 — 남은 것이 상한을 넘으면 오래된 것부터
                if (maxTotalMb > 0)
                    TrimToSize(root, maxTotalMb * 1024L * 1024L);
            }
            catch (Exception ex)
            {
                WriteSummary($"[LOG] 보존 정리 실패 — {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>정리 대상 — 로그 루트의 텍스트/CSV, 비전 이미지(카메라별 하위 폴더까지). DB 는 들어오지 않는다.</summary>
        private static System.Collections.Generic.IEnumerable<string> Candidates(string root)
        {
            foreach (var pattern in RootPatterns)
                foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
                    yield return f;

            string visionDir = Path.Combine(root, "Vision");
            if (Directory.Exists(visionDir))
                foreach (var pattern in ImagePatterns)
                    foreach (var f in Directory.EnumerateFiles(visionDir, pattern, SearchOption.AllDirectories))
                        yield return f;
        }

        private static void TrimToSize(string root, long maxBytes)
        {
            var all = Candidates(root)
                .Select(p => { try { return new FileInfo(p); } catch { return null; } })
                .Where(fi => fi != null && fi.Exists)
                .Select(fi => fi!)
                .ToList();

            long total = all.Sum(fi => fi.Length);
            if (total <= maxBytes) return;

            var protectFrom = DateTime.Now - ProtectRecent;
            int files = 0;
            long freed = 0;

            foreach (var fi in all.OrderBy(f => f.LastWriteTime))
            {
                if (total <= maxBytes) break;
                if (fi.LastWriteTime >= protectFrom) break;   // 여기부터는 최근 파일 — 더 지우지 않는다

                try
                {
                    long len = fi.Length;
                    fi.Delete();
                    total -= len;
                    freed += len;
                    files++;
                }
                catch { /* 잠긴 파일 — 건너뛴다 */ }
            }

            WriteSummary(
                $"[LOG] 용량 정리 — 상한 {maxBytes / 1024 / 1024}MB, {files}개 삭제 ({freed / 1024.0 / 1024.0:F1}MB 확보), " +
                $"남은 {total / 1024.0 / 1024.0:F1}MB" +
                (total > maxBytes ? " ⚠ 최근 24시간 파일만으로 상한을 넘습니다" : ""));
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
