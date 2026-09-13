using IJPSystem.Platform.Infrastructure.Config;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 기동 때 남기는 설정 스냅샷. "그날 어떤 설정으로 돌았나" 를 로그로 확정하는 유일한 줄이라,
    /// 호기마다 다른 값이 빠지거나 앱이 읽지 않는 사본이 섞이면 분석을 틀리게 이끈다.
    /// </summary>
    public class ConfigSnapshotTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "IJP_ConfigSnapshot_" + Guid.NewGuid().ToString("N"));

        public ConfigSnapshotTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

        [Fact]
        public void 배열_원소는_이름표로_적고_주석_키는_뺀다()
        {
            Write("MotorConfig.json", """
            {
              "_comment": "무시",
              "MotionAxisList": [
                { "AxisNo": "T", "HwAxis": 2, "Home": { "Absolute": true, "Mode": 117 } }
              ]
            }
            """);

            string values = ConfigSnapshot.Lines(_dir).Single(l => l.Contains("MotorConfig.json:"));

            Assert.Contains("MotionAxisList[T].HwAxis=2", values);
            Assert.Contains("MotionAxisList[T].Home.Mode=117", values);
            Assert.DoesNotContain("_comment", values);
        }

        /// <summary>SimulationMode=1 이면 실장비가 가짜로 움직인다 — ini 값도 반드시 보여야 한다.</summary>
        [Fact]
        public void ini_의_시뮬레이션_모드가_보인다()
        {
            Write("ComiEcatLibCfg.ini", "; 주석\n[LIB_TUNES]\nEmbeddedMode   = 1\nSimulationMode = 0 ; 실장\n");

            string values = ConfigSnapshot.Lines(_dir).Single(l => l.Contains("ComiEcatLibCfg.ini:"));

            Assert.Contains("LIB_TUNES.SimulationMode=0", values);
            Assert.Contains("LIB_TUNES.EmbeddedMode=1", values);
        }

        /// <summary>앱이 읽지 않는 호기 사본이 들어가면 "이 값으로 돌았다" 로 잘못 읽힌다.</summary>
        [Fact]
        public void 호기_사본은_넣지_않는다()
        {
            Write("MeniscusConfig.json",      """{ "ComPort": "COM8" }""");
            Write("MeniscusConfig_11호기.json", """{ "ComPort": "COM10" }""");

            var lines = ConfigSnapshot.Lines(_dir);

            Assert.Contains(lines, l => l.Contains("ComPort=COM8"));
            Assert.DoesNotContain(lines, l => l.Contains("COM10"));
        }

        /// <summary>내용이 바뀌면 이름·크기가 같아도 지문이 달라져야 두 날의 로그를 견줄 수 있다.</summary>
        [Fact]
        public void 내용이_바뀌면_지문이_바뀐다()
        {
            Write("A.json", """{ "UnitId": 1 }""");
            string first = ConfigSnapshot.Lines(_dir).Single(l => l.StartsWith("[CONFIG] A.json ·"));

            Write("A.json", """{ "UnitId": 3 }""");   // 같은 크기
            string second = ConfigSnapshot.Lines(_dir).Single(l => l.StartsWith("[CONFIG] A.json ·"));

            string Hash(string line) => line[(line.LastIndexOf('#') + 1)..];
            Assert.NotEqual(Hash(first), Hash(second));
        }

        [Fact]
        public void 깨진_JSON_이어도_나머지는_남긴다()
        {
            Write("Broken.json", "{ \"ComPort\": ");
            Write("Good.json",   """{ "ComPort": "COM7" }""");

            var lines = ConfigSnapshot.Lines(_dir);

            Assert.Contains(lines, l => l.Contains("Broken.json:") && l.Contains("해석 실패"));
            Assert.Contains(lines, l => l.Contains("ComPort=COM7"));
        }

        [Fact]
        public void 폴더가_없으면_예외_대신_한_줄로_알린다()
        {
            var lines = ConfigSnapshot.Lines(Path.Combine(_dir, "없는폴더"));

            Assert.Single(lines);
            Assert.Contains("Config 폴더 없음", lines[0]);
        }
    }
}
