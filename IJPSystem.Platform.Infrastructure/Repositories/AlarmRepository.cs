using Dapper;
using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Models.Alarm;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IJPSystem.Platform.Infrastructure.Repositories
{
    public class AlarmRepository
    {
        private const string SeedResourceName =
            "IJPSystem.Platform.Infrastructure.Database.AlarmMaster_Seed.sql";

        private static readonly (string Name, string Ddl)[] AlarmMasterColumns = new[]
        {
            ("Category",          "INTEGER NOT NULL DEFAULT 0"),
            ("CategoryName",      "TEXT"),
            ("Severity",          "TEXT NOT NULL DEFAULT 'Info'"),
            ("AlarmName_KR",      "TEXT"),
            ("AlarmName_EN",      "TEXT"),
            ("ActionGuide_KR",    "TEXT"),
            ("ActionGuide_EN",    "TEXT"),
            ("TriggerCondition",  "TEXT"),
            ("AckRequired",       "INTEGER NOT NULL DEFAULT 1"),
            ("AutoResetDelayMs",  "INTEGER"),
            ("FileLocation",      "TEXT"),
            ("CreatedAt",         "DATETIME DEFAULT CURRENT_TIMESTAMP"),
            ("UpdatedAt",         "DATETIME DEFAULT CURRENT_TIMESTAMP"),
        };

        private readonly string _systemConnStr;
        private readonly string _historyConnStr;

        public AlarmRepository()
        {
            string systemDbPath  = PathUtils.GetConfigPath(AppConstants.AlarmSystemDb);
            string historyDbPath = PathUtils.GetConfigPath(AppConstants.AlarmHistoryDb);

            _systemConnStr  = $"Data Source={systemDbPath}";
            _historyConnStr = $"Data Source={historyDbPath}";

            InitializeDatabases();
        }

        private void InitializeDatabases()
        {
            using (var conn = new SqliteConnection(_systemConnStr))
            {
                conn.Open();

                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS AlarmMaster (
                        AlarmCode TEXT PRIMARY KEY
                    );");

                MigrateAlarmMasterColumns(conn);
                ApplyEmbeddedSeed(conn);
            }

            using (var conn = new SqliteConnection(_historyConnStr))
            {
                conn.Open();
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS AlarmHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        AlarmCode TEXT,
                        AlarmName TEXT,
                        Severity TEXT,
                        StartTime DATETIME,
                        EndTime DATETIME,
                        IsAcknowledged INTEGER DEFAULT 0
                    );");
            }
        }

        // 레거시 DB(5컬럼)를 13컬럼으로 마이그레이션. 누락된 컬럼만 ADD.
        private static void MigrateAlarmMasterColumns(SqliteConnection conn)
        {
            var existing = conn.Query("PRAGMA table_info(AlarmMaster);")
                .Select(r => (string)r.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, ddl) in AlarmMasterColumns)
            {
                if (existing.Contains(name)) continue;
                try
                {
                    conn.Execute($"ALTER TABLE AlarmMaster ADD COLUMN {name} {ddl};");
                }
                catch
                {
                    // 동시 실행/중복 컬럼 등은 무시
                }
            }
        }

        // 임베디드 SQL 시드(83행)를 적재. INSERT OR IGNORE이므로 재실행 안전.
        private static void ApplyEmbeddedSeed(SqliteConnection conn)
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(SeedResourceName);
            if (stream == null) return;

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(sql)) return;

            conn.Execute(sql);
        }

        public AlarmMasterModel? GetAlarmInfo(string code)
        {
            using var conn = new SqliteConnection(_systemConnStr);
            return conn.QueryFirstOrDefault<AlarmMasterModel>(
                @"SELECT AlarmCode, Category, CategoryName, Severity,
                         AlarmName_KR, AlarmName_EN,
                         ActionGuide_KR, ActionGuide_EN,
                         TriggerCondition, AckRequired, AutoResetDelayMs, FileLocation
                  FROM AlarmMaster WHERE AlarmCode = @code", new { code });
        }

        public long LogAlarmStart(string code, string name, string severity)
        {
            using var conn = new SqliteConnection(_historyConnStr);
            return conn.ExecuteScalar<long>(@"
                INSERT INTO AlarmHistory (AlarmCode, AlarmName, Severity, StartTime, IsAcknowledged)
                VALUES (@code, @name, @severity, @start, 0);
                SELECT last_insert_rowid();",
                new { code, name, severity, start = DateTime.Now });
        }

        public void LogAlarmEnd(long id)
        {
            using var conn = new SqliteConnection(_historyConnStr);
            conn.Execute("UPDATE AlarmHistory SET EndTime = @end WHERE Id = @id",
                new { end = DateTime.Now, id });
        }

        public IEnumerable<AlarmModel> GetTotalHistory()
        {
            using var conn = new SqliteConnection(_historyConnStr);
            return conn.Query<AlarmModel>(@"
                SELECT Id AS DbId, AlarmCode, AlarmName, Severity,
                       StartTime AS OccurredTime, EndTime AS ResolvedTime,
                       (CASE WHEN EndTime IS NOT NULL THEN 1 ELSE 0 END) AS IsCleared
                FROM AlarmHistory
                ORDER BY StartTime DESC");
        }

        public void DeleteAllHistory()
        {
            using var conn = new SqliteConnection(_historyConnStr);
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                conn.Execute("DELETE FROM AlarmHistory", transaction: tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}
