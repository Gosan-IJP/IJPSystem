using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace IJPSystem.Platform.Infrastructure.Config
{
    /// <summary>
    /// <b>장비 설정</b> 저장소 — 장비 한 대에 한 벌뿐이고, 제품(레시피)이 바뀌어도 안 바뀌는 값들.
    /// 노즐 헤드 사양, 측정 파라미터 같은 것이 여기 온다.
    ///
    /// <para>
    /// <b>왜 DB 인가</b>: JSON 은 스키마가 자라지 않는다. 새 항목을 넣어도 현장 파일에 그 키가 없으면
    /// 조용히 기본값으로 돌고, 반영됐는지 확인하려면 파일을 열어봐야 한다 — 실제로 FieldOfViewXUm 을
    /// 추가했는데 현장에 안 들어가 스케일 자동 적용이 안 됐고, MeasureAreaXUm 도 같은 이유로
    /// 화면과 검출이 갈라졌다(2026-08-07). 키/값 테이블이면 새 항목이 그냥 기본값으로 읽힌다.
    /// </para>
    /// <para>
    /// <b>왜 레시피 DB 와 분리하는가</b>: 레시피는 호기 간에 복사해 다니지만 장비 설정은 그러면 안 된다.
    /// 다른 장비의 노즐 피치·카메라 배율이 따라오면 모든 측정값이 조용히 틀어진다.
    /// </para>
    /// <para>
    /// <b>여기 두면 안 되는 것</b>: 드라이버 선택(AppConfig.json), EtherCAT ini, 카메라 IP 처럼
    /// <b>앱이 뜨기 전에</b> 필요하거나, 앱이 죽었을 때 메모장으로 고쳐야 하는 값. 그건 파일이어야 한다
    /// — 오늘 ChartFontFile="none" 한 줄로 복구한 것이 그 경우다.
    /// </para>
    /// </summary>
    public sealed class MachineSettingsStore
    {
        private readonly string _connectionString;
        private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public MachineSettingsStore(string dbFilePath)
        {
            string? dir = Path.GetDirectoryName(dbFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={dbFilePath}";
            Init();
        }

        private void Init()
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS MachineSettings (Key TEXT PRIMARY KEY, Value TEXT)";
            cmd.ExecuteNonQuery();

            lock (_lock)
            {
                _cache.Clear();
                using var read = db.CreateCommand();
                read.CommandText = "SELECT Key, Value FROM MachineSettings";
                using var r = read.ExecuteReader();
                while (r.Read()) _cache[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }

        /// <summary>값이 없거나 해석 불가면 <paramref name="fallback"/>. 숫자는 항상 InvariantCulture 로 다룬다
        /// — 한국어 로캘에서 소수점이 쉼표로 저장되면 다른 PC 에서 못 읽는다.</summary>
        public double GetDouble(string key, double fallback = 0)
            => TryGet(key, out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
               ? v : fallback;

        public int GetInt(string key, int fallback = 0)
            => TryGet(key, out string s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
               ? v : fallback;

        public string GetString(string key, string fallback = "")
            => TryGet(key, out string s) ? s : fallback;

        public void Set(string key, double value) => SetRaw(key, value.ToString("R", CultureInfo.InvariantCulture));
        public void Set(string key, int value)    => SetRaw(key, value.ToString(CultureInfo.InvariantCulture));
        public void Set(string key, string value) => SetRaw(key, value ?? "");

        private bool TryGet(string key, out string value)
        {
            lock (_lock) return _cache.TryGetValue(key, out value!) && !string.IsNullOrEmpty(value);
        }

        private void SetRaw(string key, string value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string? old) && old == value) return;
                _cache[key] = value;
            }

            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO MachineSettings (Key, Value) VALUES (@k, @v) " +
                              "ON CONFLICT(Key) DO UPDATE SET Value=@v";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }

        // ── 키 이름 ───────────────────────────────────────────────────────────
        // 문자열을 여기저기 흩어 놓으면 오타 하나로 값이 조용히 사라진다(읽는 쪽은 기본값을 받는다).
        public static class Keys
        {
            /// <summary>같은 열 안에서 인접 노즐 간 거리[µm]. 드랍와처 측정창 간격의 근거값.</summary>
            public const string NozzlePitchUm    = "Nozzle.PitchUm";
            public const string NozzleRows       = "Nozzle.Rows";
            public const string NozzleRowPitchUm = "Nozzle.RowPitchUm";
            public const string NozzleDiameterUm = "Nozzle.DiameterUm";
            public const string NozzleCount      = "Nozzle.Count";
        }
    }

    /// <summary>앱 전역에서 쓰는 장비 설정 인스턴스. 부팅 시 1회 <see cref="Initialize"/>.</summary>
    public static class MachineSettings
    {
        private static MachineSettingsStore? _store;

        /// <summary>초기화 전에 읽어도 죽지 않도록 빈 저장소로 대체하지 않는다 — 잘못된 기본값이
        /// 조용히 쓰이느니 초기화 누락이 드러나는 편이 낫다.</summary>
        public static MachineSettingsStore Current =>
            _store ?? throw new InvalidOperationException("MachineSettings.Initialize 가 먼저 호출돼야 합니다.");

        public static bool IsReady => _store != null;

        public static void Initialize(string dbFilePath) => _store = new MachineSettingsStore(dbFilePath);
    }
}
