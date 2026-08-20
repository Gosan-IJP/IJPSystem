using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IJPSystem.Platform.Infrastructure.Print.Meteor
{
    public enum EngineLogSeverity { Info, Warning, Error }

    /// <summary>엔진 로그 한 줄.</summary>
    public sealed record EngineLogEntry(DateTime Timestamp, EngineLogSeverity Severity, string Text)
    {
        public override string ToString() => $"{Timestamp:HH:mm:ss.fff}  {Text}";
    }

    /// <summary>
    /// 엔진 로그 버퍼.
    ///
    /// <para>엔진은 오류 줄 앞에 <c>***</c> 를 붙인다. 오류만 따로 모으는 수집기를 두지 않고
    /// <b>같은 버퍼를 걸러서</b> 오류 목록을 만든다 — 둘을 따로 모으면 한쪽에만 있는 줄이
    /// 생겨서 "오류 목록엔 없는데 로그엔 있다" 같은 상황이 된다.</para>
    ///
    /// <para>링 버퍼라 오래 켜 둬도 메모리가 늘지 않는다.</para>
    /// </summary>
    public sealed class EngineLogView
    {
        private readonly Queue<EngineLogEntry> _entries = new();
        private readonly object _gate = new();
        private int _errorCount;

        /// <summary>보관할 최대 줄 수. 넘으면 오래된 것부터 버린다.</summary>
        public int Capacity { get; init; } = 5000;

        public int Count      { get { lock (_gate) return _entries.Count; } }
        public bool HasErrors { get { lock (_gate) return _errorCount > 0; } }

        public EngineLogEntry Append(string rawLine, DateTime? timestamp = null)
        {
            var entry = new EngineLogEntry(
                timestamp ?? ParseTimestamp(rawLine) ?? DateTime.Now,
                Classify(rawLine),
                rawLine.TrimEnd());

            lock (_gate)
            {
                _entries.Enqueue(entry);
                if (entry.Severity == EngineLogSeverity.Error) _errorCount++;

                while (_entries.Count > Capacity)
                {
                    var dropped = _entries.Dequeue();
                    if (dropped.Severity == EngineLogSeverity.Error) _errorCount--;
                }
            }
            return entry;
        }

        public IReadOnlyList<EngineLogEntry> All()
        {
            lock (_gate) return _entries.ToArray();
        }

        /// <summary>오류만. 별도 수집이 아니라 <see cref="All"/> 의 필터다.</summary>
        public IReadOnlyList<EngineLogEntry> Errors()
        {
            lock (_gate) return _entries.Where(e => e.Severity == EngineLogSeverity.Error).ToArray();
        }

        /// <summary>화면 버퍼만 비운다. 디스크의 로그 파일은 그대로다.</summary>
        public void Clear()
        {
            lock (_gate) { _entries.Clear(); _errorCount = 0; }
        }

        /// <summary>
        /// 로그 파일의 마지막 부분을 읽어 채운다.
        ///
        /// <para>엔진이 파일을 열어 둔 채로 쓰고 있으므로 공유 모드로 연다.
        /// 파일이 수백 MB 가 되기도 해서 통째로 읽지 않고 뒤에서부터 잘라 읽는다.</para>
        /// </summary>
        /// <returns>읽어 들인 줄 수. 파일이 없으면 0.</returns>
        public int LoadTail(string path, int maxLines = 1000, long maxBytes = 2 * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

            string text;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);

                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            catch (IOException)            { return 0; }
            catch (UnauthorizedAccessException) { return 0; }

            var lines = text.Split('\n')
                            .Select(l => l.TrimEnd('\r'))
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToList();

            // 뒤에서 잘라 읽었으면 첫 줄이 반 토막일 수 있다.
            if (lines.Count > 0 && text.Length >= maxBytes) lines.RemoveAt(0);

            Clear();
            foreach (string l in lines.Skip(Math.Max(0, lines.Count - maxLines))) Append(l);
            return Count;
        }

        /// <summary>
        /// 디스크의 로그 파일 내용을 비운다(삭제가 아니라 길이 0).
        /// 엔진이 파일을 열어 둔 채여도 되도록 지우지 않고 자른다.
        /// </summary>
        /// <returns>비운 파일 수.</returns>
        public static int PurgeLogFiles(string directory, string searchPattern = "*.log")
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;

            int purged = 0;
            foreach (string file in Directory.EnumerateFiles(directory, searchPattern))
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    fs.SetLength(0);
                    purged++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return purged;
        }

        // ── 분류 ─────────────────────────────────────────────────────────

        /// <summary>엔진이 오류에 붙이는 표시는 줄 앞의 <c>***</c> 다.</summary>
        public static EngineLogSeverity Classify(string line)
        {
            string t = line.TrimStart();

            if (t.StartsWith("***", StringComparison.Ordinal)) return EngineLogSeverity.Error;

            if (Has(t, "fault") || Has(t, "failed") || Has(t, "error") || Has(t, "KUSB"))
                return EngineLogSeverity.Error;

            if (Has(t, "warn")) return EngineLogSeverity.Warning;

            return EngineLogSeverity.Info;

            static bool Has(string s, string w) => s.Contains(w, StringComparison.OrdinalIgnoreCase);
        }

        // "14:09:39,238 ..." 형식
        private static readonly Regex TimePattern =
            new(@"^\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})", RegexOptions.Compiled);

        public static DateTime? ParseTimestamp(string line)
        {
            var m = TimePattern.Match(line);
            if (!m.Success) return null;

            return DateTime.Today
                .AddHours(int.Parse(m.Groups[1].Value))
                .AddMinutes(int.Parse(m.Groups[2].Value))
                .AddSeconds(int.Parse(m.Groups[3].Value))
                .AddMilliseconds(int.Parse(m.Groups[4].Value));
        }
    }
}
