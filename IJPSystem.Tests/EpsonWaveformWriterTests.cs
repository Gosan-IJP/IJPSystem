using System;
using System.IO;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print.Waveform;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 파형 파일 쓰기.
    ///
    /// <para>이 파일은 <b>PCC 가 그대로 읽는 파일</b>이다. 형식이 한 자리라도 어긋나면
    /// 화면에서는 멀쩡한 파형이 장비에서 다르게 나온다. 그래서 줄 단위로 못 박는다.</para>
    /// </summary>
    public class EpsonWaveformWriterTests : IDisposable
    {
        private readonly string _dir;

        public EpsonWaveformWriterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ijp_wfw_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>실제 파일(26.06.30_EG+EtoH test1.ComA)의 Pulse1 과 같은 모양.</summary>
        private static EpsonWaveformDocument Sample()
        {
            var doc = new EpsonWaveformDocument { Vst = 24.0 };

            var p = new EpsonWaveformPulse();
            p.Segments.Add(new EpsonWaveformSegment { Slew = 0, HoldVoltage = 24, HoldTimeUs = 1.95 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 6, HoldVoltage =  5, HoldTimeUs = 1.00 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 8, HoldVoltage = 26, HoldTimeUs = 2.00 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 1, HoldVoltage = 24, HoldTimeUs = 4.80 });
            doc.ComA.Pulses.Add(p);

            doc.GreyLevels[1, 0] = GreyLevelAssign.ComA;
            doc.GreyLevels[3, 0] = GreyLevelAssign.ComA;
            return doc;
        }

        private static string[] Lines(EpsonWaveformDocument doc, ComChannelId id) =>
            EpsonWaveformWriter.Build(doc, id)
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        [Fact]
        public void 세그먼트는_시작전압_기울기_끝전압_유지시간_순서다()
        {
            var lines = Lines(Sample(), ComChannelId.ComA);

            // 첫 세그먼트는 Vst 에서 시작한다.
            Assert.Contains("Seg0                     = 24,0,24,1.95", lines);
            Assert.Contains("Seg1                     = 24,-6,5,1", lines);
            Assert.Contains("Seg2                     = 5,8,26,2", lines);
            Assert.Contains("Seg3                     = 26,-1,24,4.8", lines);
        }

        [Fact]
        public void 기울기_부호는_전압_방향이_정한다()
        {
            var lines = Lines(Sample(), ComChannelId.ComA);

            // 24 → 5 는 내려가므로 음수, 5 → 26 은 올라가므로 양수.
            Assert.Contains(lines, l => l.StartsWith("Seg1") && l.Contains(",-6,"));
            Assert.Contains(lines, l => l.StartsWith("Seg2") && l.Contains(",8,"));
        }

        [Fact]
        public void 전압이_그대로인_구간의_기울기는_0_이다()
        {
            var lines = Lines(Sample(), ComChannelId.ComA);
            Assert.Contains(lines, l => l.StartsWith("Seg0") && l.Contains("24,0,24"));
        }

        [Fact]
        public void GL_마스크는_비트가_그레이_레벨이다()
        {
            // GL1 + GL3 → 0b1010 = 0xA
            var lines = Lines(Sample(), ComChannelId.ComA);

            Assert.Contains(lines, l => l.StartsWith("GLMask_A") && l.EndsWith("0xA"));
            Assert.Contains(lines, l => l.StartsWith("GLMask_B") && l.EndsWith("0xA"));
        }

        [Fact]
        public void ComB_파일에는_ComB_에_배정된_레벨만_들어간다()
        {
            var doc = Sample();
            doc.ComB.Pulses.Add(doc.ComA.Pulses[0].Clone());
            doc.GreyLevels[0, 0] = GreyLevelAssign.ComB;

            var a = Lines(doc, ComChannelId.ComA);
            var b = Lines(doc, ComChannelId.ComB);

            Assert.Contains(a, l => l.StartsWith("GLMask_A") && l.EndsWith("0xA"));   // GL1·GL3
            Assert.Contains(b, l => l.StartsWith("GLMask_A") && l.EndsWith("0x1"));   // GL0
            Assert.Contains(b, l => l.Contains("\"COMB\""));
        }

        [Fact]
        public void 파일에서_따라온_값을_되돌려_쓴다()
        {
            var doc = Sample();
            doc.HeadType = "EPSON_S3200";
            doc.Version  = 2;
            doc.ComA.Pulses[0].TempCompMask = 0x3;
            doc.TempComp = new TemperatureCompensation
            {
                Enabled = true, TCompLow = 41.5, TCompHigh = 46, VCompStart = 25, VCompEnd = 30, VTCoef = -0.02,
            };

            var lines = Lines(doc, ComChannelId.ComA);

            Assert.Contains(lines, l => l.StartsWith("HeadType") && l.Contains("EPSON_S3200"));
            Assert.Contains(lines, l => l.StartsWith("Version") && l.EndsWith("2"));
            Assert.Contains(lines, l => l.StartsWith("TempCompMask") && l.EndsWith("0x3"));
            Assert.Contains(lines, l => l.StartsWith("Enabled") && l.EndsWith("1"));
            Assert.Contains(lines, l => l.StartsWith("VTCoef") && l.EndsWith("-0.02"));
        }

        [Fact]
        public void 저장은_ComA_를_쓰고_ComB_가_비면_만들지_않는다()
        {
            string basePath = Path.Combine(_dir, "쓰기시험");

            var written = EpsonWaveformWriter.Save(Sample(), basePath);

            Assert.Single(written);
            Assert.True(File.Exists(basePath + ".ComA"));
            Assert.False(File.Exists(basePath + ".ComB"));
        }

        [Fact]
        public void ComB_가_비면_예전_ComB_파일을_남기지_않는다()
        {
            // 빈 파일이 남으면 다음 로드에서 "ComB 가 있는 파형"으로 보인다.
            string basePath = Path.Combine(_dir, "짝지우기");
            File.WriteAllText(basePath + ".ComB", "옛날 내용");

            EpsonWaveformWriter.Save(Sample(), basePath);

            Assert.False(File.Exists(basePath + ".ComB"));
        }

        [Fact]
        public void 임시_파일을_남기지_않는다()
        {
            string basePath = Path.Combine(_dir, "임시확인");

            EpsonWaveformWriter.Save(Sample(), basePath);

            Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        }

        [Fact]
        public void 펄스가_없으면_저장하지_않는다()
        {
            var doc = new EpsonWaveformDocument();

            Assert.Throws<InvalidOperationException>(
                () => EpsonWaveformWriter.Save(doc, Path.Combine(_dir, "빈것")));
        }

        [Fact]
        public void 저장_직전에_다시_계산한다()
        {
            // 천이시간을 일부러 엉뚱하게 넣어 두고, 저장한 파일이 계산값을 쓰는지 본다.
            var doc = Sample();
            doc.ComA.Pulses[0].Segments[1].SlewTimeUs = 999;

            string basePath = Path.Combine(_dir, "재계산");
            EpsonWaveformWriter.Save(doc, basePath);

            // ΔV 19V ÷ 6 V/µs = 3.1667 → 0.05 격자 → 3.15
            Assert.Equal(3.15, doc.ComA.Pulses[0].Segments[1].SlewTimeUs, 6);
        }
    }
}
