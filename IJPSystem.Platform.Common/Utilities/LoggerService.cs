using IJPSystem.Platform.Common.Constants;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace IJPSystem.Platform.Common.Utilities
{
    /// <summary>
    /// 로그를 물리적 파일(.txt)로 기록하는 서비스.
    ///
    /// 파일은 날짜별(<c>yyyy-MM-dd.txt</c>)이며, 한 파일이 <see cref="MaxFileBytes"/> 를 넘으면
    /// <c>yyyy-MM-dd_1.txt</c>, <c>_2</c> … 로 이어 쓴다(무한히 커지는 단일 파일 방지).
    /// 오래된 파일 삭제는 <see cref="LogRetentionService"/> 가 기동 시 1회 수행한다.
    /// </summary>
    public static class LoggerService
    {
        private static readonly string LogDirectory = AppConstants.LogFolder;

        /// <summary>한 로그 파일의 상한. 넘으면 다음 파트로 넘어간다.</summary>
        public const long MaxFileBytes = 50L * 1024 * 1024;   // 50MB

        // 여러 스레드(시퀀스/폴링/UI)가 동시에 기록한다. 예전엔 잠금이 없어 동시 쓰기 시
        // IOException 이 catch 로 삼켜지며 로그가 조용히 유실될 수 있었다.
        private static readonly object _sync = new();
        private static string? _currentPath;
        private static string  _currentDate = "";
        private static long    _currentSize;

        public static void WriteToFile(string level, string message)
        {
            try
            {
                lock (_sync)
                {
                    if (!Directory.Exists(LogDirectory))
                        Directory.CreateDirectory(LogDirectory);

                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (_currentPath == null || _currentDate != today)
                    {
                        _currentDate = today;
                        _currentPath = ResolveLatestPart(today);
                        _currentSize = File.Exists(_currentPath) ? new FileInfo(_currentPath).Length : 0;
                    }

                    string logLine = $"[{DateTime.Now.ToTimeStampMs()}] [{level}] {message}" + Environment.NewLine;
                    long bytes = Encoding.UTF8.GetByteCount(logLine);

                    if (_currentSize + bytes > MaxFileBytes)
                    {
                        _currentPath = NextPart(today);
                        _currentSize = 0;
                    }

                    File.AppendAllText(_currentPath!, logLine);
                    _currentSize += bytes;
                }
            }
            catch { /* 파일 기록 실패 시 무시 — 로깅이 기능을 막으면 안 된다 */ }
        }

        private static string PartPath(string date, int index) =>
            Path.Combine(LogDirectory, index == 0 ? $"{date}.txt" : $"{date}_{index}.txt");

        /// <summary>그 날짜의 마지막 파트(이어쓸 파일)를 찾는다. 재기동 시 처음부터 덮어쓰지 않기 위함.</summary>
        private static string ResolveLatestPart(string date)
        {
            int last = 0;
            for (int i = 1; i < 10_000; i++)
            {
                if (!File.Exists(PartPath(date, i))) break;
                last = i;
            }
            return PartPath(date, last);
        }

        private static string NextPart(string date)
        {
            int i = 1;
            while (File.Exists(PartPath(date, i))) i++;
            return PartPath(date, i);
        }
    }
}
