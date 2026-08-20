using System;
using System.IO;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>PCC Fault Register 해석. 화면에 숫자만 띄우면 아무도 못 읽는다.</summary>
    public class PccFaultDecoderTests
    {
        [Fact]
        public void 정상이면_비어_있다()
            => Assert.Empty(PccFaultDecoder.Decode(0));

        [Fact]
        public void 헤드1_언더런은_bit3()
        {
            // 헤드 n 은 4비트씩 쓴다: +0 preload, +1 command, +2 integrity, +3 under-run
            var f = Assert.Single(PccFaultDecoder.Decode(1u << 3));

            Assert.Equal(3, f.Bit);
            Assert.Equal(1, f.HeadNumber);
            Assert.Equal(PccFaultType.FifoDataUnderrun, f.Type);
        }

        [Fact]
        public void 헤드번호는_1부터다()
        {
            // 0부터 매기면 로그·매뉴얼의 Head1 과 한 칸씩 어긋난다.
            var f = Assert.Single(PccFaultDecoder.Decode(1u << 20));   // 헤드 6, +0

            Assert.Equal(6, f.HeadNumber);
            Assert.Equal(PccFaultType.PreloadIntegrityTest, f.Type);
        }

        [Fact]
        public void 헤드는_여섯까지만_본다()
        {
            // bit 24 이상은 헤드가 아니라 PLL 계통이다.
            var f = Assert.Single(PccFaultDecoder.Decode(1u << 24));

            Assert.Null(f.HeadNumber);
            Assert.Equal(PccFaultType.PllSystemClock, f.Type);
        }

        [Fact]
        public void 여러_비트를_모두_푼다()
        {
            var list = PccFaultDecoder.Decode((1u << 1) | (1u << 27));

            Assert.Equal(2, list.Count);
            Assert.Contains(list, f => f.Type == PccFaultType.FifoCommandSequence);
            Assert.Contains(list, f => f.Type == PccFaultType.PllDdramClock);
        }

        [Fact]
        public void 상태비트는_Monitor_와_같은_형식으로_적는다()
            => Assert.Equal("0x22F0 0A00", PccFaultDecoder.FormatStatusBits(0x22F00A00));
    }

    /// <summary>엔진 로그 버퍼. 오류 표시는 줄 앞의 ***.</summary>
    public class EngineLogViewTests
    {
        [Fact]
        public void 별표_세개는_오류다()
            => Assert.Equal(EngineLogSeverity.Error,
                            EngineLogView.Classify("*** KUSB write failed"));

        [Fact]
        public void 평범한_줄은_정보다()
            => Assert.Equal(EngineLogSeverity.Info,
                            EngineLogView.Classify("14:09:39,238 Wrote PCC:1, Hdc:5 Fwd X-offset = 600"));

        [Fact]
        public void 경고와_오류를_구분한다()
        {
            Assert.Equal(EngineLogSeverity.Warning, EngineLogView.Classify("PCC1 warning: retry"));
            Assert.Equal(EngineLogSeverity.Error,   EngineLogView.Classify("PCC1 fault register 0x8"));
        }

        [Fact]
        public void 시각을_읽는다()
        {
            var t = EngineLogView.ParseTimestamp("14:09:39,238 PCC1 Firmware version: 0xed5acbdc");

            Assert.NotNull(t);
            Assert.Equal(14, t!.Value.Hour);
            Assert.Equal(238, t.Value.Millisecond);
        }

        [Fact]
        public void 시각이_없으면_null()
            => Assert.Null(EngineLogView.ParseTimestamp("PrintEngine started"));

        [Fact]
        public void 오류목록은_전체목록의_필터다()
        {
            // 따로 모으면 한쪽에만 있는 줄이 생긴다.
            var v = new EngineLogView();
            v.Append("ok 1");
            v.Append("*** bad");
            v.Append("ok 2");

            Assert.Equal(3, v.All().Count);
            Assert.Equal("*** bad", Assert.Single(v.Errors()).Text);
            Assert.True(v.HasErrors);
        }

        [Fact]
        public void 용량을_넘으면_오래된_것부터_버린다()
        {
            var v = new EngineLogView { Capacity = 3 };
            for (int i = 0; i < 10; i++) v.Append("line " + i);

            Assert.Equal(3, v.Count);
            Assert.Equal("line 7", v.All()[0].Text);
        }

        [Fact]
        public void 버린_줄의_오류는_카운트에서도_빠진다()
        {
            // 안 빼면 오류를 다 흘려보낸 뒤에도 오류 표시등이 켜져 있다.
            var v = new EngineLogView { Capacity = 2 };
            v.Append("*** bad");
            v.Append("ok 1");
            v.Append("ok 2");

            Assert.False(v.HasErrors);
        }

        [Fact]
        public void 화면만_지운다()
        {
            var v = new EngineLogView();
            v.Append("*** bad");
            v.Clear();

            Assert.Equal(0, v.Count);
            Assert.False(v.HasErrors);
        }

        [Fact]
        public void 없는_파일은_0줄()
            => Assert.Equal(0, new EngineLogView().LoadTail(@"C:\없는폴더\none.log"));

        [Fact]
        public void 파일_끝을_읽는다()
        {
            string p = Path.Combine(Path.GetTempPath(), "ijp_log_" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllLines(p, new[] { "a", "b", "*** c" });
            try
            {
                var v = new EngineLogView();

                Assert.Equal(3, v.LoadTail(p));
                Assert.True(v.HasErrors);
            }
            finally { File.Delete(p); }
        }
    }

    /// <summary>로그 상세 항목 ↔ cfg 의 LogCtrlBits.</summary>
    public class PrintEngineLogModuleTests : IDisposable
    {
        private readonly string _dir;

        public PrintEngineLogModuleTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ijp_lm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void 항목이_열넷이다()
            => Assert.Equal(14, PrintEngineLogModuleSettings.All.Count);

        [Fact]
        public void 십육진수와_십진수를_모두_받는다()
        {
            Assert.Equal(PrintEngineLogModules.Setup | PrintEngineLogModules.Commands,
                         PrintEngineLogModuleSettings.Parse("0x5"));
            Assert.Equal(PrintEngineLogModules.Setup | PrintEngineLogModules.Commands,
                         PrintEngineLogModuleSettings.Parse("5"));
        }

        [Fact]
        public void 값이_없으면_null()
            => Assert.Null(PrintEngineLogModuleSettings.Parse("   "));

        [Fact]
        public void 무거운_항목을_가려낸다()
        {
            Assert.True(PrintEngineLogModuleSettings.IsHeavy(PrintEngineLogModules.WaveformData));
            Assert.False(PrintEngineLogModuleSettings.IsHeavy(PrintEngineLogModules.Setup));
        }

        [Fact]
        public void 값을_적는_형식은_0x_다()
            => Assert.Equal("0x5", PrintEngineLogModuleSettings.Format(
                   PrintEngineLogModules.Setup | PrintEngineLogModules.Commands));

        [Fact]
        public void 없으면_기본값을_쓴다()
        {
            string p = Path.Combine(_dir, "a.cfg");
            File.WriteAllText(p, "[Test]\nLogToDisk = 1\n");

            Assert.Equal(PrintEngineLogModuleSettings.Default,
                         PrintEngineLogModuleSettings.Read(MeteorConfigFile.Load(p)));
        }

        [Fact]
        public void 저장하면_그_키만_바뀐다()
        {
            // 이 파일은 현장에서 손으로 편집한다 — 통째로 다시 쓰면 주석과 순서가 날아간다.
            string p = Path.Combine(_dir, "b.cfg");
            File.WriteAllText(p,
                "; 머리 주석\n[Test]\nLogToDisk = 1     ; 로그 파일로\nLogCtrlBits = 0x1 ; 예전 값\n\n[System]\nHeadType = \"EPSON_S3200\"\n");

            PrintEngineLogModuleSettings.Save(p, PrintEngineLogModules.Waveforms);

            string[] lines = File.ReadAllLines(p);
            Assert.Equal("; 머리 주석", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("LogToDisk = 1"));
            Assert.Contains(lines, l => l.StartsWith("LogCtrlBits = 0x10"));
            Assert.Contains(lines, l => l.Contains("[System]"));
            Assert.Contains(lines, l => l.Contains("EPSON_S3200"));
        }

        [Fact]
        public void 줄_끝_주석을_살린다()
        {
            string p = Path.Combine(_dir, "c.cfg");
            File.WriteAllText(p, "[Test]\nLogCtrlBits = 0x1 ; 왜 이 값인지 적어 둔 것\n");

            PrintEngineLogModuleSettings.Save(p, PrintEngineLogModules.Setup);

            Assert.Contains("왜 이 값인지 적어 둔 것", File.ReadAllText(p));
        }

        [Fact]
        public void 키가_없으면_그_섹션_안에_넣는다()
        {
            string p = Path.Combine(_dir, "d.cfg");
            File.WriteAllText(p, "[Test]\nLogToDisk = 1\n\n[System]\nHeadType = \"X\"\n");

            PrintEngineLogModuleSettings.Save(p, PrintEngineLogModules.Setup);

            string[] lines = File.ReadAllLines(p);
            int keyLine = Array.FindIndex(lines, l => l.StartsWith("LogCtrlBits"));
            int sysLine = Array.FindIndex(lines, l => l.Contains("[System]"));

            Assert.True(keyLine > 0);
            Assert.True(keyLine < sysLine, "다른 섹션으로 넘어가 버렸다");
        }

        [Fact]
        public void 섹션이_없으면_만든다()
        {
            string p = Path.Combine(_dir, "e.cfg");
            File.WriteAllText(p, "[System]\nHeadType = \"X\"\n");

            PrintEngineLogModuleSettings.Save(p, PrintEngineLogModules.Setup);

            var cfg = MeteorConfigFile.Load(p);
            Assert.Equal(PrintEngineLogModules.Setup, PrintEngineLogModuleSettings.Read(cfg));
            Assert.Equal("X", cfg.HeadType);
        }

        [Fact]
        public void 저장한_값을_다시_읽는다()
        {
            string p = Path.Combine(_dir, "f.cfg");
            File.WriteAllText(p, "[Test]\nLogCtrlBits = 0x0\n");

            var picked = PrintEngineLogModules.Setup | PrintEngineLogModules.Waveforms |
                         PrintEngineLogModules.PccConnection;
            PrintEngineLogModuleSettings.Save(p, picked);

            Assert.Equal(picked, PrintEngineLogModuleSettings.Read(MeteorConfigFile.Load(p)));
        }

        [Fact]
        public void 임시파일을_남기지_않는다()
        {
            string p = Path.Combine(_dir, "g.cfg");
            File.WriteAllText(p, "[Test]\n");

            PrintEngineLogModuleSettings.Save(p, PrintEngineLogModules.Setup);

            Assert.False(File.Exists(p + ".tmp"));
        }

        [Fact]
        public void 로그_파일_경로는_cfg_폴더_기준이다()
        {
            string p = Path.Combine(_dir, "h.cfg");
            File.WriteAllText(p, "[Test]\nLogToDisk = 1\nLogFile = \"PrintEngine.Log\"\n");

            var cfg = MeteorConfigFile.Load(p);

            Assert.True(cfg.LogToDisk);
            Assert.Equal(Path.Combine(_dir, "PrintEngine.Log"), cfg.LogFilePath);
        }

        [Fact]
        public void 로그를_안_쓰는_설정도_읽는다()
        {
            // LogToDisk = 0 이면 파일이 아예 안 생긴다 — 화면이 "파일 없음"과 구분해야 한다.
            string p = Path.Combine(_dir, "i.cfg");
            File.WriteAllText(p, "[Test]\nLogToDisk = 0\nLogFile = \"PrintEngine.Log\"\n");

            Assert.False(MeteorConfigFile.Load(p).LogToDisk);
        }
    }
}
