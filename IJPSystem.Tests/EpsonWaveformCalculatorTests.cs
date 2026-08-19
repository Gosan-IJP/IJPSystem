using System;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print.Waveform;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// Epson 구동 파형 계산.
    ///
    /// <para>기준값은 실제 MetWaveEpson v4.7 화면(26.06.30_EG+EtoH test1.ComA)에서 읽은 것이다 —
    /// 우리 계산이 장비 도구와 같은 숫자를 내야 화면 그래프와 실제 토출이 일치한다.
    /// 여기가 틀리면 그래프는 멀쩡한데 액적만 안 맞고, 화면에는 원인이 안 보인다.</para>
    /// </summary>
    public class EpsonWaveformCalculatorTests
    {
        const double Vst = 24.0;

        /// <summary>화면의 ComA Pulse2 — Slew/HoldVoltage/HoldTime 만 넣고 SlewTime 은 계산시킨다.</summary>
        static EpsonWaveformPulse ComAPulse2()
        {
            var p = new EpsonWaveformPulse();
            p.Segments.Add(new EpsonWaveformSegment { Slew = 8.00, HoldVoltage = 24.00, HoldTimeUs = 1.95 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 6.00, HoldVoltage =  5.00, HoldTimeUs = 1.00 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 8.00, HoldVoltage = 26.00, HoldTimeUs = 1.95 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 1.00, HoldVoltage = 24.00, HoldTimeUs = 4.80 });
            return p;
        }

        static EpsonWaveformPulse Flat(double us)
        {
            var p = new EpsonWaveformPulse();
            p.Segments.Add(new EpsonWaveformSegment { Slew = 8, HoldVoltage = Vst, HoldTimeUs = us });
            return p;
        }

        // ── 격자 양자화 ───────────────────────────────────────────────────
        [Theory]
        [InlineData(3.16666, 3.15)]   // 19/6 — 내림 쪽
        [InlineData(2.625,   2.65)]   // 21/8 — 정확히 격자 절반, 올려야 장비와 같다
        [InlineData(2.0,     2.00)]
        [InlineData(0.0,     0.00)]
        public void 시간은_0_05us_격자로_반올림한다(double raw, double expected)
            => Assert.Equal(expected, EpsonWaveformCalculator.QuantizeTime(raw), precision: 10);

        [Fact]
        public void 절반은_올린다()
        {
            // 은행가 반올림(ToEven)이면 2.625 → 2.60 이 되어 장비 표기와 어긋난다.
            Assert.Equal(2.65, EpsonWaveformCalculator.QuantizeTime(2.625), precision: 10);
            Assert.Equal(0.05, EpsonWaveformCalculator.QuantizeTime(0.025), precision: 10);
        }

        // ── 규칙 2·3: 천이 시간 유도 ──────────────────────────────────────
        [Fact]
        public void ConstantSlew_는_화면의_천이시간을_그대로_재현한다()
        {
            var p = ComAPulse2();
            EpsonWaveformCalculator.ResolvePulse(p, Vst, VoltageAdjustMode.ConstantSlew);

            Assert.Equal(0.00, p.Segments[0].SlewTimeUs, precision: 10);  // 24→24, ΔV=0
            Assert.Equal(3.15, p.Segments[1].SlewTimeUs, precision: 10);  // 24→5,  19/6
            Assert.Equal(2.65, p.Segments[2].SlewTimeUs, precision: 10);  // 5→26,  21/8
            Assert.Equal(2.00, p.Segments[3].SlewTimeUs, precision: 10);  // 26→24, 2/1
        }

        [Fact]
        public void 시작전압은_직전_세그먼트의_도달전압이다()
        {
            // 전부 Vst 기준으로 계산하면 Seg3 가 |26-24|/8 = 0.25 가 되어 화면(2.65)과 완전히 다르다.
            var p = ComAPulse2();
            EpsonWaveformCalculator.ResolvePulse(p, Vst, VoltageAdjustMode.ConstantSlew);
            Assert.Equal(2.65, p.Segments[2].SlewTimeUs, precision: 10);
        }

        [Fact]
        public void 전압변화가_없으면_천이시간은_0()
        {
            var p = ComAPulse2();
            EpsonWaveformCalculator.ResolvePulse(p, Vst, VoltageAdjustMode.ConstantSlew);
            Assert.Equal(0.0, p.Segments[0].SlewTimeUs, precision: 10);
        }

        [Fact]
        public void ConstantDuration_은_기울기를_계산한다()
        {
            var p = ComAPulse2();
            p.Segments[1].SlewTimeUs = 3.15;
            EpsonWaveformCalculator.ResolvePulse(p, Vst, VoltageAdjustMode.ConstantDuration);

            // 19V / 3.15µs = 6.0317… → 0.01 격자 → 6.03
            Assert.Equal(6.03, p.Segments[1].Slew, precision: 10);
        }

        // ── 규칙 1: Vst 복귀 ──────────────────────────────────────────────
        [Fact]
        public void 마지막_세그먼트는_Vst_로_강제된다()
        {
            // 복귀를 강제하지 않으면 파형에 DC 성분이 남아 헤드에 전압이 계속 걸린다.
            var p = ComAPulse2();
            p.Segments[3].HoldVoltage = 30.0;      // 사용자가 잘못 넣은 값
            EpsonWaveformCalculator.ResolvePulse(p, Vst, VoltageAdjustMode.ConstantSlew);

            Assert.Equal(Vst, p.Segments[3].HoldVoltage, precision: 10);
        }

        // ── 규칙 5: 최대 주파수 ───────────────────────────────────────────
        [Fact]
        public void 화면의_최대주파수_29_76kHz_를_재현한다()
        {
            var doc = new EpsonWaveformDocument { Vst = Vst, ComAbMode = ComAbMode.Independent };

            // Pulse1 은 화면에서 값을 못 읽었으므로 총 16.10µs 가 되도록 구성한다.
            // (29.76kHz → 주기 33.60µs, Pulse2 가 17.50µs 이므로 나머지가 16.10µs)
            doc.ComA.Pulses.Add(Flat(16.10));
            doc.ComA.Pulses.Add(ComAPulse2());

            EpsonWaveformCalculator.ResolveDocument(doc);

            Assert.Equal(17.50, doc.ComA.Pulses[1].TotalTimeUs, precision: 10);
            Assert.Equal(33.60, doc.ComA.TotalTimeUs,           precision: 10);
            Assert.Equal(29.76, EpsonWaveformCalculator.MaxFrequencyKHz(doc), precision: 2);
        }

        [Fact]
        public void 최대주파수는_긴_채널이_정한다()
        {
            // 짧은 쪽은 기다린다 — 짧은 채널로 계산하면 실제로 못 내는 주파수를 표시하게 된다.
            var doc = new EpsonWaveformDocument { Vst = Vst, ComAbMode = ComAbMode.Independent };
            doc.ComA.Pulses.Add(Flat(10.0));
            doc.ComB.Pulses.Add(Flat(50.0));

            Assert.Equal(20.0, EpsonWaveformCalculator.MaxFrequencyKHz(doc), precision: 6);   // 1000/50
        }

        [Fact]
        public void 빈_파형은_주파수_0()
            => Assert.Equal(0.0, EpsonWaveformCalculator.MaxFrequencyKHz(new EpsonWaveformDocument()));

        // ── Synchronous ───────────────────────────────────────────────────
        [Fact]
        public void Synchronous_면_ComB_는_ComA_의_복제가_된다()
        {
            // 따로 편집한 ComB 를 남겨 두면 다시 로드할 때 조용히 사라져
            // "저장했는데 안 바뀐다" 로 보인다.
            var doc = new EpsonWaveformDocument { Vst = Vst, ComAbMode = ComAbMode.Synchronous };
            doc.ComA.Pulses.Add(ComAPulse2());
            doc.ComB.Pulses.Add(Flat(99.0));

            EpsonWaveformCalculator.ResolveDocument(doc);

            Assert.Equal(doc.ComA.TotalTimeUs, doc.ComB.TotalTimeUs, precision: 10);
            Assert.NotSame(doc.ComA, doc.ComB);   // 복제여야 한다 — 같은 객체면 한쪽 편집이 양쪽에 샌다
        }

        // ── 그래프 좌표 ───────────────────────────────────────────────────
        [Fact]
        public void 그래프는_Vst_에서_시작해_Vst_로_끝난다()
        {
            var doc = new EpsonWaveformDocument { Vst = Vst };
            doc.ComA.Pulses.Add(ComAPulse2());
            EpsonWaveformCalculator.ResolveDocument(doc);

            var trace = EpsonWaveformCalculator.BuildTrace(doc.ComA, doc.Vst);

            Assert.Equal(0.0,   trace[0].TimeUs, precision: 10);
            Assert.Equal(Vst,   trace[0].Volts,  precision: 10);
            Assert.Equal(Vst,   trace[trace.Count - 1].Volts,  precision: 10);
            Assert.Equal(17.50, trace[trace.Count - 1].TimeUs, precision: 10);
        }

        [Fact]
        public void 그래프_시간은_단조증가한다()
        {
            var doc = new EpsonWaveformDocument { Vst = Vst };
            doc.ComA.Pulses.Add(ComAPulse2());
            EpsonWaveformCalculator.ResolveDocument(doc);

            var trace = EpsonWaveformCalculator.BuildTrace(doc.ComA, doc.Vst);
            for (int i = 1; i < trace.Count; i++)
                Assert.True(trace[i].TimeUs >= trace[i - 1].TimeUs,
                            $"시간이 뒤로 갔다: {trace[i - 1].TimeUs} → {trace[i].TimeUs}");
        }

        [Fact]
        public void 시간0의_전압변화도_점으로_남긴다()
        {
            // 수직 계단은 잘못된 편집값이지만, 점을 안 찍으면 그래프가 이전 전압을 유지한 것처럼
            // 보여 사용자가 잘못을 못 알아챈다.
            var doc = new EpsonWaveformDocument
            {
                Vst = Vst,
                VoltageAdjustMode = VoltageAdjustMode.ConstantDuration,
            };
            var p = new EpsonWaveformPulse();
            p.Segments.Add(new EpsonWaveformSegment { SlewTimeUs = 0, HoldVoltage = 10,  HoldTimeUs = 1 });
            p.Segments.Add(new EpsonWaveformSegment { SlewTimeUs = 1, HoldVoltage = Vst, HoldTimeUs = 1 });
            doc.ComA.Pulses.Add(p);
            EpsonWaveformCalculator.ResolveDocument(doc);

            var trace = EpsonWaveformCalculator.BuildTrace(doc.ComA, doc.Vst);
            Assert.Contains(trace, pt => Math.Abs(pt.TimeUs) < 1e-9 && Math.Abs(pt.Volts - 10) < 1e-9);
        }

        [Fact]
        public void Y축은_최고전압을_5V_단위로_올려_담는다()
        {
            var doc = new EpsonWaveformDocument { Vst = Vst };
            doc.ComA.Pulses.Add(ComAPulse2());          // 최고 26V
            var (min, max) = EpsonWaveformCalculator.VoltageAxis(doc);

            Assert.Equal(0,  min);
            Assert.Equal(30, max);
        }

        // ── GL 배정표 ─────────────────────────────────────────────────────
        [Fact]
        public void GL_배정은_세_상태_토글이다()
        {
            var m = new GreyLevelMatrix();
            m.Toggle(0, 0, GreyLevelAssign.ComA);
            Assert.Equal(GreyLevelAssign.ComA, m[0, 0]);

            m.Toggle(0, 0, GreyLevelAssign.ComA);           // 같은 값 → 해제
            Assert.Equal(GreyLevelAssign.None, m[0, 0]);
        }

        [Fact]
        public void 같은_칸에_ComA_와_ComB_는_공존하지_않는다()
        {
            var m = new GreyLevelMatrix();
            m.Toggle(1, 2, GreyLevelAssign.ComA);
            m.Toggle(1, 2, GreyLevelAssign.ComB);
            Assert.Equal(GreyLevelAssign.ComB, m[1, 2]);
        }

        [Fact]
        public void 배정이_없는_GL_은_토출이_안_된다()
        {
            // 저장 전에 경고해야 하는 상태다 — 그대로 내려보내면 그 레벨만 조용히 안 나간다.
            var m = new GreyLevelMatrix();
            Assert.False(m.HasAnyPulse(2));

            m.Toggle(2, 0, GreyLevelAssign.ComA);
            Assert.True(m.HasAnyPulse(2));
        }
    }
}
