using IJPSystem.Platform.Infrastructure.Config;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 장비 설정 저장소 — <b>JSON 설정이 못 하던 것</b>을 고정한다.
    ///
    /// <para>
    /// JSON 은 스키마가 자라지 않아, 새 항목을 추가해도 현장 파일에 그 키가 없으면 조용히
    /// 기본값으로 돈다. 실제로 FieldOfViewXUm 을 넣었는데 현장에 안 들어가 스케일 자동 적용이
    /// 안 됐고, MeasureAreaXUm 도 같은 이유로 화면(60)과 검출(150)이 갈라졌다(2026-08-07).
    /// 키/값 테이블은 새 항목이 그냥 기본값으로 읽히므로 그 실패가 구조적으로 사라진다.
    /// </para>
    /// </summary>
    public class MachineSettingsStoreTests : IDisposable
    {
        private readonly string _path;

        public MachineSettingsStoreTests()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ijp_machine_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { File.Delete(_path); } catch { /* 임시파일 — 남아도 무해 */ }
        }

        [Fact]
        public void Set_ThenReopen_KeepsValue()
        {
            new MachineSettingsStore(_path).Set(MachineSettingsStore.Keys.NozzlePitchUm, 254.0);

            var reopened = new MachineSettingsStore(_path);

            Assert.Equal(254.0, reopened.GetDouble(MachineSettingsStore.Keys.NozzlePitchUm), 3);
        }

        /// <summary>
        /// 아직 저장된 적 없는 키는 기본값으로 읽혀야 한다 — 이것이 JSON 대비 핵심 이점이다.
        /// 새 설정을 코드에 추가해도 현장 DB 를 손대지 않고 그대로 돈다.
        /// </summary>
        [Fact]
        public void UnknownKey_ReturnsFallback_NoMigrationNeeded()
        {
            var store = new MachineSettingsStore(_path);

            Assert.Equal(0.685, store.GetDouble("Vision.NewSettingAddedLater", 0.685), 3);
            Assert.Equal(7, store.GetInt("Print.AnotherNewSetting", 7));
            Assert.Equal("auto", store.GetString("Some.NewText", "auto"));
        }

        /// <summary>
        /// 소수점은 로캘과 무관해야 한다. 한국어 로캘에서 쉼표로 저장되면 다른 PC 에서 못 읽고,
        /// 그러면 피치가 조용히 기본값으로 돌아가 모든 측정이 틀어진다.
        /// </summary>
        [Fact]
        public void Decimal_RoundTripsRegardlessOfCulture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ko-KR");
                var store = new MachineSettingsStore(_path);
                store.Set(MachineSettingsStore.Keys.NozzlePitchUm, 254.5);

                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");   // 소수점이 쉼표인 로캘
                Assert.Equal(254.5, new MachineSettingsStore(_path)
                                        .GetDouble(MachineSettingsStore.Keys.NozzlePitchUm), 3);
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
        }

        [Fact]
        public void Set_Overwrites()
        {
            var store = new MachineSettingsStore(_path);

            store.Set(MachineSettingsStore.Keys.NozzleRows, 1);
            store.Set(MachineSettingsStore.Keys.NozzleRows, 4);

            Assert.Equal(4, new MachineSettingsStore(_path).GetInt(MachineSettingsStore.Keys.NozzleRows));
        }

        /// <summary>키 이름이 겹치면 한쪽 값이 조용히 사라진다 — 상수 목록이 중복 없이 유지돼야 한다.</summary>
        [Fact]
        public void KeyNames_AreDistinct()
        {
            string[] keys =
            {
                MachineSettingsStore.Keys.NozzlePitchUm,
                MachineSettingsStore.Keys.NozzleRows,
                MachineSettingsStore.Keys.NozzleRowPitchUm,
                MachineSettingsStore.Keys.NozzleDiameterUm,
                MachineSettingsStore.Keys.NozzleCount,
            };

            Assert.Equal(keys.Length, new System.Collections.Generic.HashSet<string>(keys).Count);
        }
    }
}
