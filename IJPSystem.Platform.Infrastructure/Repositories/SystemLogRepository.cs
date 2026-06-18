using Dapper;
using IJPSystem.Platform.Common.Constants;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace IJPSystem.Platform.Infrastructure.Repositories
{
    /// <summary>
    /// 시스템 로그 DB 적재 — LoggerService(.txt) 와 짝으로 동작합니다.
    /// txt 는 fail-safe 백업, DB 는 화면 필터/검색용.
    /// </summary>
    public static class SystemLogRepository
    {
        private const int RetentionDays = 30;   // 보관 기간 (시작 시 1회 정리)

        private static readonly object _initLock = new();
        private static bool   _initialized;
        private static string _connStr = string.Empty;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                if (!Directory.Exists(AppConstants.LogFolder))
                    Directory.CreateDirectory(AppConstants.LogFolder);
                string path = Path.Combine(AppConstants.LogFolder, AppConstants.SystemLogDb);
                _connStr = $"Data Source={path}";

                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS SystemLog (
                        Id      INTEGER PRIMARY KEY AUTOINCREMENT,
                        Time    DATETIME NOT NULL,
                        Level   TEXT     NOT NULL,
                        Message TEXT
                    );
                    CREATE INDEX IF NOT EXISTS IX_SystemLog_Time ON SystemLog(Time);");

                // 보관 기간 초과 자동 정리 — 누적으로 인한 DB 비대화 방지
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                conn.Execute("DELETE FROM SystemLog WHERE Time < @cutoff", new { cutoff });

                _initialized = true;
            }
        }

        /// <summary>로그 1건을 DB 에 적재합니다. 실패해도 예외 던지지 않음(txt 가 백업).</summary>
        public static void Write(DateTime time, string level, string message)
        {
            try
            {
                EnsureInitialized();
                using var conn = new SqliteConnection(_connStr);
                conn.Execute(
                    "INSERT INTO SystemLog (Time, Level, Message) VALUES (@time, @level, @message)",
                    new { time, level, message = message ?? string.Empty });
            }
            catch
            {
                // 무시 — txt 가 fail-safe  
            }
        }

        // ── 조회 (LogView 화면용) ──────────────────────────────────────
        public class SystemLogEntry
        {
            public long     Id      { get; set; }
            public DateTime Time    { get; set; }
            public string   Level   { get; set; } = "";
            public string   Message { get; set; } = "";
        }

        /// <summary>최근 N건 (시간 내림차순)</summary>
        public static IEnumerable<SystemLogEntry> GetRecent(int count = 500)
        {
            try
            {
                EnsureInitialized();
                using var conn = new SqliteConnection(_connStr);
                return conn.Query<SystemLogEntry>(
                    "SELECT Id, Time, Level, Message FROM SystemLog ORDER BY Time DESC LIMIT @count",
                    new { count });
            }
            catch
            {
                return Array.Empty<SystemLogEntry>();
            }
        }

        /// <summary>
        /// 기간/레벨/키워드/메시지패턴 필터 — null/empty 인 인자는 무시.
        /// patterns 가 주어지면 (Message LIKE p1 OR Message LIKE p2 ...) 로 OR 결합 (카테고리 quick-filter 용).
        /// </summary>
        public static IEnumerable<SystemLogEntry> Query(
            DateTime? from = null, DateTime? to = null,
            string? level = null, string[]? levels = null,
            string? keyword = null,
            string[]? patterns = null, int limit = 10000)
        {
            try
            {
                EnsureInitialized();
                var sb = new System.Text.StringBuilder(
                    "SELECT Id, Time, Level, Message FROM SystemLog WHERE 1=1");
                var args = new DynamicParameters();

                if (from.HasValue)                       { sb.Append(" AND Time >= @from");    args.Add("from",  from.Value); }
                if (to.HasValue)                         { sb.Append(" AND Time <= @to");      args.Add("to",    to.Value);   }

                // levels 배열이 있으면 그것을 우선 (Level IN (...)). 없으면 단일 level=
                if (levels != null && levels.Length > 0)
                {
                    var inList = new List<string>();
                    for (int i = 0; i < levels.Length; i++)
                    {
                        string name = $"lv{i}";
                        inList.Add($"@{name}");
                        args.Add(name, levels[i]);
                    }
                    sb.Append(" AND Level IN (").Append(string.Join(", ", inList)).Append(')');
                }
                else if (!string.IsNullOrWhiteSpace(level))
                {
                    sb.Append(" AND Level = @level");
                    args.Add("level", level);
                }

                if (!string.IsNullOrWhiteSpace(keyword)) { sb.Append(" AND Message LIKE @kw"); args.Add("kw",    $"%{keyword}%"); }

                if (patterns != null && patterns.Length > 0)
                {
                    var or = new List<string>();
                    for (int i = 0; i < patterns.Length; i++)
                    {
                        string name = $"p{i}";
                        or.Add($"Message LIKE @{name}");
                        args.Add(name, $"%{patterns[i]}%");
                    }
                    sb.Append(" AND (").Append(string.Join(" OR ", or)).Append(')');
                }

                sb.Append(" ORDER BY Time DESC LIMIT @limit");
                args.Add("limit", limit);

                using var conn = new SqliteConnection(_connStr);
                return conn.Query<SystemLogEntry>(sb.ToString(), args);
            }
            catch
            {
                return Array.Empty<SystemLogEntry>();
            }
        }

        /// <summary>전체 삭제 (관리자용)</summary>
        public static void DeleteAll()
        {
            try
            {
                EnsureInitialized();
                using var conn = new SqliteConnection(_connStr);
                conn.Execute("DELETE FROM SystemLog");
            }
            catch { }
        }
    }
}
