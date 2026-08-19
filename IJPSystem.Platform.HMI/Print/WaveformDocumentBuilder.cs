using System;
using System.Collections.Generic;
using System.Linq;
using IJPSystem.Platform.HMI.Models;
using IJPSystem.Platform.Infrastructure.Print.Waveform;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 파싱된 <c>.ComA</c>/<c>.ComB</c> 파일을 편집 가능한 <see cref="EpsonWaveformDocument"/> 로 옮긴다.
    ///
    /// <para><b>왜 변환이 필요한가</b>: 파일 모델은 세그먼트를
    /// (시작전압, 기울기, 끝전압, 유지시간)으로 적는데, 편집 화면은
    /// (기울기, 천이시간, 도달전압, 유지시간)으로 다룬다. 시작전압은 직전 세그먼트에서
    /// 따라오므로 편집값으로 들고 있으면 두 곳이 갈라진다 — 그래서 도달전압만 남긴다.</para>
    /// </summary>
    public static class WaveformDocumentBuilder
    {
        /// <summary>
        /// 두 채널 파일에서 문서를 만든다. 한쪽이 없으면 그 채널은 비워 둔다.
        /// </summary>
        /// <param name="name">문서 이름(파일 베이스명).</param>
        public static EpsonWaveformDocument Build(WaveformFile? comA, WaveformFile? comB, string name = "")
        {
            var doc = new EpsonWaveformDocument { Name = name };

            // 대기 전압은 파일에 따로 없다 — 첫 세그먼트의 시작 전압이 곧 Vst 다.
            doc.Vst = FirstStartVoltage(comA) ?? FirstStartVoltage(comB) ?? 24.0;

            Fill(doc.ComA, comA);
            Fill(doc.ComB, comB);

            // ComB 가 ComA 와 같은 내용이면 Synchronous 로 본다. 편집 화면에서 ComB 를 잠가
            // "따로 고쳤는데 저장하면 사라지는" 상황을 미리 없앤다.
            doc.ComAbMode = comB == null || SameShape(doc.ComA, doc.ComB)
                ? ComAbMode.Synchronous
                : ComAbMode.Independent;

            ApplyGreyLevels(doc, comA, comB);

            EpsonWaveformCalculator.ResolveDocument(doc);
            return doc;
        }

        private static double? FirstStartVoltage(WaveformFile? f)
            => f?.Pulses.FirstOrDefault()?.Segments.FirstOrDefault()?.StartVoltage;

        private static void Fill(EpsonComChannel channel, WaveformFile? file)
        {
            channel.Pulses.Clear();
            if (file == null) return;

            // 최대 4 펄스 — 그 이상은 헤드가 받지 않으므로 잘라내되 조용히 버리지 않는다.
            foreach (var src in file.Pulses.Take(EpsonComChannel.MaxPulses))
            {
                var p = new EpsonWaveformPulse();
                foreach (var s in src.Segments)
                {
                    p.Segments.Add(new EpsonWaveformSegment
                    {
                        // 기울기는 항상 양수로 둔다 — 방향은 전압 차가 정한다.
                        Slew        = Math.Abs(s.SlewRate),
                        HoldVoltage = s.EndVoltage,
                        HoldTimeUs  = s.HoldTime,
                    });
                }
                channel.Pulses.Add(p);
            }
        }

        /// <summary>펄스 수·세그먼트 값이 같은가(Synchronous 판정용).</summary>
        private static bool SameShape(EpsonComChannel a, EpsonComChannel b)
        {
            if (a.Pulses.Count != b.Pulses.Count) return false;
            for (int i = 0; i < a.Pulses.Count; i++)
            {
                var pa = a.Pulses[i].Segments;
                var pb = b.Pulses[i].Segments;
                if (pa.Count != pb.Count) return false;
                for (int j = 0; j < pa.Count; j++)
                {
                    if (Math.Abs(pa[j].Slew        - pb[j].Slew)        > 1e-9) return false;
                    if (Math.Abs(pa[j].HoldVoltage - pb[j].HoldVoltage) > 1e-9) return false;
                    if (Math.Abs(pa[j].HoldTimeUs  - pb[j].HoldTimeUs)  > 1e-9) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 파일의 GL 마스크를 배정표로 옮긴다. 마스크는 <b>비트 = 그레이 레벨</b>이다
        /// (bit0 → GL0). 펄스마다 A/B 마스크가 따로 있어 같은 GL 에서 어느 Com 으로
        /// 쏠지가 정해진다.
        /// </summary>
        private static void ApplyGreyLevels(EpsonWaveformDocument doc, WaveformFile? comA, WaveformFile? comB)
        {
            Apply(comA, GreyLevelAssign.ComA);
            Apply(comB, GreyLevelAssign.ComB);

            void Apply(WaveformFile? file, GreyLevelAssign assign)
            {
                if (file == null) return;
                for (int p = 0; p < file.Pulses.Count && p < EpsonComChannel.MaxPulses; p++)
                {
                    int mask = assign == GreyLevelAssign.ComA
                        ? file.Pulses[p].GLMask_A
                        : file.Pulses[p].GLMask_B;

                    for (int g = 0; g < GreyLevelMatrix.Levels; g++)
                        if ((mask & (1 << g)) != 0) doc.GreyLevels[g, p] = assign;
                }
            }
        }
    }
}
