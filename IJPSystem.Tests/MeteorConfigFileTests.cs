using System;
using System.IO;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// Meteor 엔진 설정(.cfg) 읽기.
    ///
    /// <para>이 파일은 <b>우리가 만드는 파일이 아니다</b> — Meteor 설치가 관리한다.
    /// 화면은 "PCC 가 실제로 무엇을 읽는지"를 보여 주는 것이 목적이라, 잘못 읽으면
    /// 멀쩡한 설정을 틀렸다고 표시하게 된다.</para>
    /// </summary>
    public class MeteorConfigFileTests : IDisposable
    {
        private readonly string _dir;

        public MeteorConfigFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ijp_cfg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Write(string name, string text)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, text);
            return p;
        }

        private const string Sample = """
            ; Default PCCE configuration file for the Epson S3200 head.
            [Encoder]
            PrintClock          = 0     ; 0 = External Encoder
            Multiplier          = 3     ; Encoder multiplier
            Divider             = 127   ; Encoder divider
            Quadrature          = 1     ; Encoder is quadrature

            [System]
            PccType             = "PCC2E" ; PCC2E or PCCE
            HeadType            = "EPSON_S3200"

            [Planes]
            PlanesPerHdc        = 1
            Plane1              = 1:1

            [EPSON_S3200]
            Xdpi                = 600
            BitsPerPixel        = 2
            Waveform1           = "Waveform\Epson\S3200\TestS3200_Waveform.ComA" ; Default
            Waveform2           = "Waveform\Epson\S3200\Second.ComA"

            [DefaultParameterValues]
            WaveformFileIdx       = 2

            [Ethernet]
            Adapter1                = "PCC-E Network"
            ; Adapter2              = "Local Area Connection"
            """;

        [Fact]
        public void 없는_파일도_예외가_아니다()
        {
            var cfg = MeteorConfigFile.Load(Path.Combine(_dir, "nope.cfg"));

            Assert.False(cfg.Exists);
            Assert.Empty(cfg.Waveforms);
            Assert.Equal("", cfg.HeadType);
        }

        [Fact]
        public void 빈_경로도_예외가_아니다()
            => Assert.False(MeteorConfigFile.Load("").Exists);

        [Fact]
        public void 따옴표와_주석을_벗긴다()
        {
            var cfg = MeteorConfigFile.Load(Write("a.cfg", Sample));

            Assert.Equal("PCC2E", cfg.PccType);
            Assert.Equal("EPSON_S3200", cfg.HeadType);
            Assert.Equal("PCC-E Network", cfg.EthernetAdapter);
        }

        [Fact]
        public void 주석만_있는_줄은_설정이_아니다()
        {
            // "; Adapter2 = ..." 를 값으로 읽으면 없는 어댑터를 있다고 표시한다.
            var cfg = MeteorConfigFile.Load(Write("b.cfg", Sample));

            Assert.Equal("", cfg.Get("Ethernet", "Adapter2"));
        }

        [Fact]
        public void 헤드_설정은_헤드이름_섹션에서_읽는다()
        {
            // [EPSON_S3200] — 헤드를 바꾸면 섹션 이름도 바뀐다.
            var cfg = MeteorConfigFile.Load(Write("c.cfg", Sample));

            Assert.Equal(600, cfg.Xdpi);
            Assert.Equal(2, cfg.BitsPerPixel);
        }

        [Fact]
        public void 계조는_비트수의_거듭제곱이다()
        {
            // BPP 2 → 4단계(GL0~GL3). 파형 화면의 GL 배정표 칸 수와 같아야 한다.
            var cfg = MeteorConfigFile.Load(Write("d.cfg", Sample));

            Assert.Equal(4, cfg.GreyLevels);
        }

        [Fact]
        public void 파형_목록을_번호와_함께_읽는다()
        {
            var cfg = MeteorConfigFile.Load(Write("e.cfg", Sample));
            var list = cfg.Waveforms;

            Assert.Equal(2, list.Count);
            Assert.Equal(1, list[0].Index);
            Assert.Equal("TestS3200_Waveform", list[0].Name);
            Assert.Equal("Second", list[1].Name);
        }

        [Fact]
        public void 기본_파형은_WaveformFileIdx_가_정한다()
        {
            // 헤드는 레시피가 아니라 이 번호로 파형을 고른다 — 1번을 기본으로 못 박으면 안 된다.
            var cfg = MeteorConfigFile.Load(Write("f.cfg", Sample));

            Assert.False(cfg.Waveforms[0].IsDefault);
            Assert.True(cfg.Waveforms[1].IsDefault);
        }

        [Fact]
        public void 파형_경로는_cfg_폴더_기준으로_푼다()
        {
            string cfgPath = Write("g.cfg", Sample);
            var w = MeteorConfigFile.Load(cfgPath).Waveforms[0];

            Assert.Equal(
                Path.GetFullPath(Path.Combine(_dir, @"Waveform\Epson\S3200\TestS3200_Waveform.ComA")),
                w.FullPath);
        }

        [Fact]
        public void 없는_파형은_없다고_표시한다()
        {
            // 목록에 적혀 있어도 파일이 없으면 헤드는 그 파형을 못 쓴다.
            var cfg = MeteorConfigFile.Load(Write("h.cfg", Sample));

            Assert.All(cfg.Waveforms, w => Assert.False(w.Exists));
        }

        [Fact]
        public void 있는_파형은_있다고_표시한다()
        {
            string cfgPath = Write("i.cfg", Sample);
            string full = Path.Combine(_dir, @"Waveform\Epson\S3200\TestS3200_Waveform.ComA");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "[generic]");

            Assert.True(MeteorConfigFile.Load(cfgPath).Waveforms[0].Exists);
        }

        [Fact]
        public void 원문을_그대로_보관한다()
        {
            // 화면이 우리가 못 읽은 항목까지 보여 줄 수 있어야 한다 — 요약만 남기면
            // "화면에 안 뜨니 설정이 없다" 는 오해가 생긴다.
            var cfg = MeteorConfigFile.Load(Write("j.cfg", Sample));

            Assert.Equal(Sample, cfg.RawText);
        }

        [Fact]
        public void 실제_배포_파일을_읽는다()
        {
            // 저장소에 들어 있는 진짜 파일 — 형식이 바뀌면 여기서 먼저 걸린다.
            string p = Path.Combine(RepoRoot(), "Config", "PccE", "DefaultEpsonS3200_PccE.cfg");
            if (!File.Exists(p)) return;   // 배포본에 따라 없을 수 있다

            var cfg = MeteorConfigFile.Load(p);

            Assert.Equal("PCC2E", cfg.PccType);
            Assert.Equal("EPSON_S3200", cfg.HeadType);
            Assert.Equal(4, cfg.GreyLevels);
            Assert.Equal(600, cfg.Xdpi);
            Assert.Equal("PCC-E Network", cfg.EthernetAdapter);
            Assert.Single(cfg.Waveforms);
            Assert.True(cfg.Waveforms[0].IsDefault);
        }

        private static string RepoRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\"));
    }
}
