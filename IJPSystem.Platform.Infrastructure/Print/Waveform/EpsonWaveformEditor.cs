using System;

namespace IJPSystem.Platform.Infrastructure.Print.Waveform
{
    /// <summary>
    /// 파형 구조 편집 — Insert Pulse / Delete Pulse.
    ///
    /// <para><b>펄스를 넣고 빼면 GL 배정표의 열도 함께 밀려야 한다.</b> 배정표는 열이 곧
    /// 펄스 번호이므로, 열을 안 밀면 GL2 가 가리키던 펄스가 조용히 옆 펄스로 바뀐다 —
    /// 화면 배정표는 그대로인데 액적 크기만 달라져서 원인을 찾기 어렵다.</para>
    ///
    /// <para>ComA / ComB 는 같은 펄스 번호를 공유한다(배정표 한 장이 두 채널을 함께 가리킨다).
    /// 그래서 삽입·삭제는 두 채널에 같이 건다.</para>
    /// </summary>
    public static class EpsonWaveformEditor
    {
        /// <summary>펄스를 더 넣을 수 있는가(채널 최대 <see cref="EpsonComChannel.MaxPulses"/>).</summary>
        public static bool CanInsertPulse(EpsonWaveformDocument doc)
            => doc.ComA.Pulses.Count < EpsonComChannel.MaxPulses;

        /// <summary>펄스를 지울 수 있는가. 마지막 하나는 남긴다 — 펄스가 없으면 토출이 안 된다.</summary>
        public static bool CanDeletePulse(EpsonWaveformDocument doc)
            => doc.ComA.Pulses.Count > 1;

        /// <summary><paramref name="index"/> 자리에 기본 펄스를 넣는다(ComA·ComB 동시).</summary>
        public static bool InsertPulse(EpsonWaveformDocument doc, int index)
        {
            if (!CanInsertPulse(doc)) return false;

            index = Math.Clamp(index, 0, doc.ComA.Pulses.Count);
            doc.ComA.Pulses.Insert(index, EpsonWaveformPulse.CreateDefault(doc.Vst));
            doc.ComB.Pulses.Insert(Math.Min(index, doc.ComB.Pulses.Count),
                                   EpsonWaveformPulse.CreateDefault(doc.Vst));

            ShiftGreyLevelsOnInsert(doc.GreyLevels, index);
            EpsonWaveformCalculator.ResolveDocument(doc);
            return true;
        }

        /// <summary><paramref name="index"/> 펄스를 지운다(ComA·ComB 동시).</summary>
        public static bool DeletePulse(EpsonWaveformDocument doc, int index)
        {
            if (!CanDeletePulse(doc)) return false;
            if (index < 0 || index >= doc.ComA.Pulses.Count) return false;

            doc.ComA.Pulses.RemoveAt(index);
            if (index < doc.ComB.Pulses.Count) doc.ComB.Pulses.RemoveAt(index);

            ShiftGreyLevelsOnDelete(doc.GreyLevels, index);
            EpsonWaveformCalculator.ResolveDocument(doc);
            return true;
        }

        /// <summary>한 펄스가 가질 수 있는 세그먼트 수. MetWaveEpson 과 같은 범위.</summary>
        public const int MinSegments = 1;
        public const int MaxSegments = 8;

        /// <summary>
        /// Segment Count — 세그먼트 개수를 맞춘다.
        /// <para>늘릴 때는 <b>마지막 앞</b>에 넣는다. 마지막 세그먼트는 Vst 복귀 구간이라
        /// 뒤에 붙이면 복귀가 중간에 끼어 파형 모양이 뒤집힌다.</para>
        /// <para>줄일 때는 마지막 앞에서부터 지운다 — 복귀 구간은 남긴다.</para>
        /// </summary>
        public static bool SetSegmentCount(EpsonWaveformDocument doc, ComChannelId channel,
                                           int pulseIndex, int count)
        {
            var pulses = doc.ChannelOf(channel).Pulses;
            if (pulseIndex < 0 || pulseIndex >= pulses.Count) return false;

            count = Math.Clamp(count, MinSegments, MaxSegments);
            var segs = pulses[pulseIndex].Segments;
            if (segs.Count == count) return false;

            while (segs.Count < count)
            {
                var last = segs[^1];
                segs.Insert(segs.Count - 1, new EpsonWaveformSegment
                {
                    Slew        = last.Slew > 0 ? last.Slew : 8.0,
                    HoldVoltage = doc.Vst,
                    HoldTimeUs  = 1.0,
                });
            }

            while (segs.Count > count)
                segs.RemoveAt(Math.Max(0, segs.Count - 2));

            EpsonWaveformCalculator.ResolveDocument(doc);
            return true;
        }

        /// <summary>
        /// "Copy pulse to ..." — 한 펄스를 다른 펄스 자리로 복사한다(채널이 달라도 된다).
        /// GL 배정표는 건드리지 않는다 — 배정은 "어느 펄스를 쓸지"라 모양과 별개다.
        /// </summary>
        public static bool CopyPulse(EpsonWaveformDocument doc,
                                     ComChannelId fromChannel, int fromIndex,
                                     ComChannelId toChannel,   int toIndex)
        {
            var src = doc.ChannelOf(fromChannel).Pulses;
            var dst = doc.ChannelOf(toChannel).Pulses;

            if (fromIndex < 0 || fromIndex >= src.Count) return false;
            if (toIndex   < 0 || toIndex   >= dst.Count) return false;
            if (fromChannel == toChannel && fromIndex == toIndex) return false;

            dst[toIndex] = src[fromIndex].Clone();
            EpsonWaveformCalculator.ResolveDocument(doc);
            return true;
        }

        /// <summary>
        /// Scale Voltage — Vst 기준 진폭을 배율만큼 키우거나 줄인다.
        ///
        /// <para>Vst 자체는 두고 (V − Vst) 성분에만 배율을 걸어야 한다. 전압 전체에 곱하면
        /// 대기 전압이 함께 움직여 헤드가 늘 다른 전압에 놓인다.</para>
        /// </summary>
        public static bool ScaleVoltage(EpsonWaveformDocument doc, double factor, bool includeComB = true)
        {
            if (factor <= 0 || Math.Abs(factor - 1.0) < 1e-9) return false;

            Scale(doc.ComA);
            if (includeComB) Scale(doc.ComB);

            EpsonWaveformCalculator.ResolveDocument(doc);
            return true;

            void Scale(EpsonComChannel ch)
            {
                foreach (var p in ch.Pulses)
                    foreach (var s in p.Segments)
                        s.HoldVoltage = doc.Vst + (s.HoldVoltage - doc.Vst) * factor;
            }
        }

        /// <summary>삽입 자리부터 오른쪽으로 한 칸씩 밀고, 새 열은 비운다.</summary>
        private static void ShiftGreyLevelsOnInsert(GreyLevelMatrix m, int index)
        {
            for (int g = 0; g < GreyLevelMatrix.Levels; g++)
            {
                for (int p = EpsonComChannel.MaxPulses - 1; p > index; p--)
                    m[g, p] = m[g, p - 1];

                if (index < EpsonComChannel.MaxPulses) m[g, index] = GreyLevelAssign.None;
            }
        }

        /// <summary>지운 자리부터 왼쪽으로 당기고, 마지막 열은 비운다.</summary>
        private static void ShiftGreyLevelsOnDelete(GreyLevelMatrix m, int index)
        {
            for (int g = 0; g < GreyLevelMatrix.Levels; g++)
            {
                for (int p = index; p < EpsonComChannel.MaxPulses - 1; p++)
                    m[g, p] = m[g, p + 1];

                m[g, EpsonComChannel.MaxPulses - 1] = GreyLevelAssign.None;
            }
        }
    }
}
