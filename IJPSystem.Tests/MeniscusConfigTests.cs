using System;
using System.IO;
using IJPSystem.Platform.Domain.Models.Config;
using IJPSystem.Platform.Infrastructure.Config;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 메니스커스(DMD) 설정이 AppConfig 에서 MeniscusConfig.json 으로 옮겨 간 것에 대한 고정.
    ///
    /// <para>
    /// 여기서 지키는 것은 <b>하위호환</b>이다. 제어 PC 의 AppConfig.json 에는 이미 COM 포트가
    /// 현장 값으로 들어 있어서, 파일이 없다고 코드 기본값으로 떨어지면 배포 직후 조용히
    /// 연결이 끊긴다 — 로그만 보고는 "장비가 안 붙네" 로만 보인다.
    /// </para>
    /// </summary>
    public class MeniscusConfigTests
    {
        private static string WriteTemp(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"meni_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void 파일이_없으면_null_이라_호출부가_폴백할_수_있다()
        {
            var cfg = new ConfigLoader().LoadMeniscusConfig(
                Path.Combine(Path.GetTempPath(), $"없는파일_{Guid.NewGuid():N}.json"));

            Assert.Null(cfg);
        }

        [Fact]
        public void 시리얼과_레지스터를_파일에서_읽는다()
        {
            string path = WriteTemp("""
                {
                  "ComPort": "COM6", "BaudRate": 19200,
                  "Parity": 2, "DataBits": 8, "StopBits": 2, "TimeoutMs": 500,
                  "UnitId": 3,
                  "PressureReadAddress": 16, "PressureSetAddress": 272, "ControlAddress": 273,
                  "PressureScale": 0.01, "PressureOffset": -100.0
                }
                """);
            try
            {
                var cfg = new ConfigLoader().LoadMeniscusConfig(path);

                Assert.NotNull(cfg);
                Assert.Equal("COM6", cfg!.ComPort);
                Assert.Equal(19200, cfg.BaudRate);
                Assert.Equal(System.IO.Ports.Parity.Even, cfg.Parity);
                Assert.Equal(System.IO.Ports.StopBits.Two, cfg.StopBits);
                Assert.Equal(500, cfg.TimeoutMs);
                Assert.Equal(3, cfg.UnitId);

                // 레지스터를 빌드 없이 고칠 수 있어야 한다 — 주소·스케일이 아직 미검증이다.
                Assert.Equal(16, cfg.PressureReadAddress);
                Assert.Equal(272, cfg.PressureSetAddress);
                Assert.Equal(273, cfg.ControlAddress);
                Assert.Equal(0.01, cfg.PressureScale);
                Assert.Equal(-100.0, cfg.PressureOffset);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void 주석_키는_무시하고_적힌_값만_덮어쓴다()
        {
            string path = WriteTemp("""
                { "_comment1": "설명", "ComPort": "COM9" }
                """);
            try
            {
                var cfg = new ConfigLoader().LoadMeniscusConfig(path);

                Assert.Equal("COM9", cfg!.ComPort);
                Assert.Equal(9600, cfg.BaudRate);        // 안 적은 값은 기본값 유지
                Assert.Equal(1, cfg.UnitId);
            }
            finally { File.Delete(path); }
        }

        // ── DriverMode.Meniscus 하위호환 ──────────────────────────────────────
        // 판정 규칙은 PatternPrintViewModel.MeniscusEnabled 에 있지만 그 화면은 IO/모션이
        // 붙어야 만들어져 테스트에서 세울 수 없다. 규칙 자체를 여기 옮겨 적어 고정한다.
        private static bool Enabled(AppSettings cfg)
        {
            string mode = cfg.DriverMode?.Meniscus ?? "";
            return string.IsNullOrWhiteSpace(mode)
                 ? cfg.MeniscusEnabled
                 : mode.Trim().Equals("Dmd", StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Dmd", true)]
        [InlineData("dmd", true)]
        [InlineData("Virtual", false)]
        [InlineData("None", false)]
        public void DriverMode_가_있으면_그것을_따른다(string mode, bool expected)
        {
            var cfg = new AppSettings { MeniscusEnabled = !expected };   // 옛 키는 반대로 둬 본다
            cfg.DriverMode.Meniscus = mode;

            Assert.Equal(expected, Enabled(cfg));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DriverMode_가_비어_있으면_옛_키를_따른다(bool legacy)
        {
            var cfg = new AppSettings { MeniscusEnabled = legacy };
            cfg.DriverMode.Meniscus = "";

            Assert.Equal(legacy, Enabled(cfg));
        }

        /// <summary>기본 AppSettings 는 꺼져 있어야 한다 — 설정 없는 장비에서 COM 을 열면 안 된다.</summary>
        [Fact]
        public void 기본값은_연결하지_않는다()
        {
            Assert.False(Enabled(new AppSettings()));
            Assert.Equal("", new AppSettings().DriverMode.Meniscus);
        }
    }
}
