using System.Linq;
using IJPSystem.Platform.Infrastructure.Print.Waveform;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// Insert / Delete Pulse.
    ///
    /// <para>여기서 지키는 것은 <b>GL 배정표의 열이 펄스를 따라 움직인다</b>는 것이다.
    /// 열을 안 밀면 GL2 가 가리키던 펄스가 조용히 옆 펄스로 바뀐다 — 배정표 화면은
    /// 그대로인데 액적 크기만 달라져 원인을 찾기 어렵다.</para>
    /// </summary>
    public class EpsonWaveformEditorTests
    {
        /// <summary>펄스 n 개짜리 문서. 펄스를 구분하려고 유지시간을 다르게 준다.</summary>
        private static EpsonWaveformDocument Doc(int pulses)
        {
            var doc = new EpsonWaveformDocument { Vst = 24.0 };
            for (int i = 0; i < pulses; i++)
            {
                var p = new EpsonWaveformPulse();
                p.Segments.Add(new EpsonWaveformSegment { Slew = 8, HoldVoltage = 5,  HoldTimeUs = 1 + i });
                p.Segments.Add(new EpsonWaveformSegment { Slew = 8, HoldVoltage = 24, HoldTimeUs = 1 });
                doc.ComA.Pulses.Add(p);
                doc.ComB.Pulses.Add(p.Clone());
            }
            return doc;
        }

        [Fact]
        public void InsertPulse_는_ComA_ComB_에_같이_들어간다()
        {
            var doc = Doc(2);

            Assert.True(EpsonWaveformEditor.InsertPulse(doc, 1));

            Assert.Equal(3, doc.ComA.Pulses.Count);
            Assert.Equal(3, doc.ComB.Pulses.Count);
        }

        [Fact]
        public void InsertPulse_가_GL_배정_열을_오른쪽으로_민다()
        {
            var doc = Doc(2);
            doc.GreyLevels[1, 0] = GreyLevelAssign.ComA;   // 펄스0
            doc.GreyLevels[2, 1] = GreyLevelAssign.ComB;   // 펄스1

            EpsonWaveformEditor.InsertPulse(doc, 1);       // 펄스0 과 1 사이

            Assert.Equal(GreyLevelAssign.ComA, doc.GreyLevels[1, 0]);   // 그대로
            Assert.Equal(GreyLevelAssign.None, doc.GreyLevels[2, 1]);   // 새 열은 비어 있고
            Assert.Equal(GreyLevelAssign.ComB, doc.GreyLevels[2, 2]);   // 원래 배정은 한 칸 밀렸다
        }

        [Fact]
        public void DeletePulse_가_GL_배정_열을_왼쪽으로_당긴다()
        {
            var doc = Doc(3);
            doc.GreyLevels[0, 0] = GreyLevelAssign.ComA;
            doc.GreyLevels[1, 2] = GreyLevelAssign.ComB;

            EpsonWaveformEditor.DeletePulse(doc, 1);       // 가운데를 지운다

            Assert.Equal(GreyLevelAssign.ComA, doc.GreyLevels[0, 0]);
            Assert.Equal(GreyLevelAssign.ComB, doc.GreyLevels[1, 1]);   // 한 칸 당겨졌고
            Assert.Equal(GreyLevelAssign.None, doc.GreyLevels[1, 2]);   // 뒤는 비었다
        }

        [Fact]
        public void 마지막_펄스는_지우지_않는다()
        {
            var doc = Doc(1);

            Assert.False(EpsonWaveformEditor.CanDeletePulse(doc));
            Assert.False(EpsonWaveformEditor.DeletePulse(doc, 0));
            Assert.Single(doc.ComA.Pulses);
        }

        [Fact]
        public void 최대_펄스_수를_넘기지_않는다()
        {
            var doc = Doc(EpsonComChannel.MaxPulses);

            Assert.False(EpsonWaveformEditor.CanInsertPulse(doc));
            Assert.False(EpsonWaveformEditor.InsertPulse(doc, 1));
            Assert.Equal(EpsonComChannel.MaxPulses, doc.ComA.Pulses.Count);
        }

        [Fact]
        public void 새로_넣은_펄스는_Vst_로_돌아온다()
        {
            var doc = Doc(1);

            EpsonWaveformEditor.InsertPulse(doc, 1);

            var added = doc.ComA.Pulses[1];
            Assert.Equal(doc.Vst, added.Segments.Last().HoldVoltage, 6);
        }

        // ── Segment Count ────────────────────────────────────────────────

        [Fact]
        public void 세그먼트를_늘리면_복귀_구간_앞에_들어간다()
        {
            var doc = Doc(1);   // 세그먼트 2개: [5V 로 내림] [Vst 복귀]

            Assert.True(EpsonWaveformEditor.SetSegmentCount(doc, ComChannelId.ComA, 0, 4));

            var segs = doc.ComA.Pulses[0].Segments;
            Assert.Equal(4, segs.Count);
            // 마지막은 여전히 Vst 복귀 — 뒤에 붙이면 복귀가 중간에 끼어 모양이 뒤집힌다.
            Assert.Equal(doc.Vst, segs[^1].HoldVoltage, 6);
            Assert.Equal(5, segs[0].HoldVoltage, 6);
        }

        [Fact]
        public void 세그먼트를_줄여도_복귀_구간은_남는다()
        {
            var doc = Doc(1);
            EpsonWaveformEditor.SetSegmentCount(doc, ComChannelId.ComA, 0, 6);

            EpsonWaveformEditor.SetSegmentCount(doc, ComChannelId.ComA, 0, 2);

            var segs = doc.ComA.Pulses[0].Segments;
            Assert.Equal(2, segs.Count);
            Assert.Equal(doc.Vst, segs[^1].HoldVoltage, 6);
        }

        [Fact]
        public void 세그먼트_수는_범위를_벗어나지_않는다()
        {
            var doc = Doc(1);

            EpsonWaveformEditor.SetSegmentCount(doc, ComChannelId.ComA, 0, 99);
            Assert.Equal(EpsonWaveformEditor.MaxSegments, doc.ComA.Pulses[0].Segments.Count);

            EpsonWaveformEditor.SetSegmentCount(doc, ComChannelId.ComA, 0, 0);
            Assert.Single(doc.ComA.Pulses[0].Segments);   // MinSegments = 1
        }

        // ── Copy pulse to ────────────────────────────────────────────────

        [Fact]
        public void CopyPulse_는_값을_복사하고_원본과_끊는다()
        {
            var doc = Doc(2);
            doc.ComA.Pulses[0].Segments[0].HoldTimeUs = 7.0;

            Assert.True(EpsonWaveformEditor.CopyPulse(doc, ComChannelId.ComA, 0, ComChannelId.ComB, 1));

            Assert.Equal(7.0, doc.ComB.Pulses[1].Segments[0].HoldTimeUs, 6);

            // 복사본이지 참조가 아니어야 한다 — 아니면 한쪽을 고칠 때 둘 다 바뀐다.
            doc.ComA.Pulses[0].Segments[0].HoldTimeUs = 1.0;
            Assert.Equal(7.0, doc.ComB.Pulses[1].Segments[0].HoldTimeUs, 6);
        }

        [Fact]
        public void CopyPulse_는_자기_자신에게는_하지_않는다()
            => Assert.False(EpsonWaveformEditor.CopyPulse(Doc(2), ComChannelId.ComA, 1, ComChannelId.ComA, 1));

        [Fact]
        public void CopyPulse_는_GL_배정을_건드리지_않는다()
        {
            var doc = Doc(2);
            doc.GreyLevels[0, 1] = GreyLevelAssign.ComB;

            EpsonWaveformEditor.CopyPulse(doc, ComChannelId.ComA, 0, ComChannelId.ComA, 1);

            Assert.Equal(GreyLevelAssign.ComB, doc.GreyLevels[0, 1]);
        }

        // ── Scale Voltage ────────────────────────────────────────────────

        [Fact]
        public void ScaleVoltage_는_Vst_기준_진폭만_바꾼다()
        {
            var doc = Doc(1);            // Vst 24, 첫 세그먼트 5V (진폭 -19V)

            EpsonWaveformEditor.ScaleVoltage(doc, 0.5);

            // 24 + (5 - 24) × 0.5 = 14.5
            Assert.Equal(14.5, doc.ComA.Pulses[0].Segments[0].HoldVoltage, 6);
            Assert.Equal(24.0, doc.Vst, 6);                                  // 대기 전압은 그대로
            Assert.Equal(24.0, doc.ComA.Pulses[0].Segments[^1].HoldVoltage, 6);
        }

        [Fact]
        public void ScaleVoltage_는_ComB_도_함께_바꾼다()
        {
            var doc = Doc(1);

            EpsonWaveformEditor.ScaleVoltage(doc, 2.0);

            Assert.Equal(doc.ComA.Pulses[0].Segments[0].HoldVoltage,
                         doc.ComB.Pulses[0].Segments[0].HoldVoltage, 6);
        }

        [Fact]
        public void ScaleVoltage_배율이_1_이거나_음수면_아무것도_안_한다()
        {
            var doc = Doc(1);
            double before = doc.ComA.Pulses[0].Segments[0].HoldVoltage;

            Assert.False(EpsonWaveformEditor.ScaleVoltage(doc, 1.0));
            Assert.False(EpsonWaveformEditor.ScaleVoltage(doc, -0.5));
            Assert.Equal(before, doc.ComA.Pulses[0].Segments[0].HoldVoltage, 6);
        }

        [Fact]
        public void 삽입_후_천이시간이_다시_계산된다()
        {
            var doc = Doc(1);

            EpsonWaveformEditor.InsertPulse(doc, 1);

            // ΔV 가 있는 세그먼트는 천이시간이 0 이 아니어야 한다(계산이 돌았다는 뜻).
            var seg = doc.ComA.Pulses[1].Segments[1];
            Assert.True(seg.SlewTimeUs > 0, $"천이시간이 {seg.SlewTimeUs} 다");
        }
    }
}
