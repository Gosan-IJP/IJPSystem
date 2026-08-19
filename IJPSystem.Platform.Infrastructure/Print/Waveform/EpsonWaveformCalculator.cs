using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print.Waveform
{
    /// <summary>
    /// 파형 계산 — 편집값에서 <b>실제 하드웨어가 낼 파형</b>을 유도한다.
    ///
    /// <para><b>왜 별도 클래스인가</b>: 화면이 보여주는 그래프와 헤드로 내려가는 값이 같은 계산에서
    /// 나와야 한다. 화면은 화면대로 그리고 저장은 저장대로 반올림하면, 그래프상 멀쩡한 파형이
    /// 실제로는 다른 모양으로 나간다 — 액적이 안 맞는데 화면에는 원인이 안 보인다.</para>
    ///
    /// <para>계산 규칙(MetWaveEpson 동작 기준):
    /// <list type="number">
    ///   <item>펄스는 Vst 에서 시작해 Vst 로 끝난다 — 마지막 세그먼트의 도달 전압은 Vst 로 강제.</item>
    ///   <item>세그먼트의 시작 전압은 직전 세그먼트의 도달 전압(첫 세그먼트는 Vst).</item>
    ///   <item>ConstantSlew 면 시간을, ConstantDuration 이면 기울기를 계산한다.</item>
    ///   <item>계산값·저장값 모두 하드웨어 격자로 양자화한다.</item>
    /// </list></para>
    /// </summary>
    public static class EpsonWaveformCalculator
    {
        // ── 하드웨어 격자 ─────────────────────────────────────────────────
        // 격자에 맞추지 않으면 장비가 자기 기준으로 반올림해 버리고, 그 결과가 화면과 달라진다.
        // 그래서 화면 표시가 아니라 <b>모델에 저장할 때</b> 맞춘다.
        public const double TimeGridUs   = 0.05;
        public const double VoltageGridV = 0.05;
        public const double SlewGrid     = 0.01;

        /// <summary>격자에 맞춰 반올림. 0.5 는 올린다(장비 표기와 일치 — 2.625 → 2.65).</summary>
        public static double Quantize(double value, double grid)
        {
            if (grid <= 0) return value;
            return Math.Round(Math.Round(value / grid, MidpointRounding.AwayFromZero) * grid, 10);
        }

        public static double QuantizeTime(double us) => Quantize(us, TimeGridUs);
        public static double QuantizeVolt(double v)  => Quantize(v,  VoltageGridV);
        public static double QuantizeSlew(double s)  => Quantize(s,  SlewGrid);

        /// <summary>
        /// 한 펄스의 파생값을 확정한다. 마지막 세그먼트는 Vst 로 복귀시킨다.
        /// </summary>
        /// <remarks>
        /// Vst 복귀를 강제하지 않으면 파형에 DC 성분이 남아 헤드에 지속적으로 전압이 걸린다.
        /// 편집 중 실수 하나로 헤드를 상하게 할 수 있는 자리라 사용자 입력을 그대로 두지 않는다.
        /// </remarks>
        public static void ResolvePulse(EpsonWaveformPulse pulse, double vst, VoltageAdjustMode mode)
        {
            if (pulse == null || pulse.Segments.Count == 0) return;

            var segs = pulse.Segments;
            segs[^1].HoldVoltage = vst;                     // 규칙 1

            double start = vst;
            foreach (var s in segs)
            {
                s.HoldVoltage = QuantizeVolt(s.HoldVoltage);
                double dv = Math.Abs(s.HoldVoltage - start);   // 규칙 2

                if (mode == VoltageAdjustMode.ConstantSlew)
                {
                    s.Slew = QuantizeSlew(s.Slew);
                    // ΔV 가 0 이면 천이가 없다 — 기울기와 무관하게 시간 0.
                    s.SlewTimeUs = dv <= 0 || s.Slew <= 0 ? 0 : QuantizeTime(dv / s.Slew);
                }
                else
                {
                    s.SlewTimeUs = QuantizeTime(s.SlewTimeUs);
                    // 시간이 0 인데 전압이 변하면 기울기가 무한대다. 그건 표현할 수 없으므로
                    // 0 으로 두고 검증에서 잡는다(여기서 예외를 던지면 편집 도중에 화면이 죽는다).
                    s.Slew = dv <= 0 || s.SlewTimeUs <= 0 ? 0 : QuantizeSlew(dv / s.SlewTimeUs);
                }

                s.HoldTimeUs = QuantizeTime(s.HoldTimeUs);
                start = s.HoldVoltage;
            }
        }

        /// <summary>문서 전체 재계산. 편집 뒤에는 반드시 부른다.</summary>
        public static void ResolveDocument(EpsonWaveformDocument doc)
        {
            if (doc == null) return;
            doc.Vst = QuantizeVolt(doc.Vst);

            // Synchronous 면 ComB 는 ComA 의 복제다. 따로 편집한 값을 남겨 두면 다시 로드할 때
            // 조용히 사라져, "저장했는데 안 바뀐다" 로 보인다 — 저장 시점에 복제로 통일한다.
            if (doc.ComAbMode == ComAbMode.Synchronous)
                doc.ComB = doc.ComA.Clone();

            foreach (var p in doc.ComA.Pulses) ResolvePulse(p, doc.Vst, doc.VoltageAdjustMode);
            foreach (var p in doc.ComB.Pulses) ResolvePulse(p, doc.Vst, doc.VoltageAdjustMode);
        }

        /// <summary>
        /// 채널의 그래프 좌표. 천이는 기울어진 선, 유지는 수평선이 되도록 꺾임점만 낸다.
        /// </summary>
        public static IReadOnlyList<WaveformPoint> BuildTrace(EpsonComChannel channel, double vst)
        {
            var pts = new List<WaveformPoint>();
            if (channel == null) return pts;

            double t = 0, v = vst;
            pts.Add(new WaveformPoint(t, v));

            foreach (var pulse in channel.Pulses)
            {
                foreach (var s in pulse.Segments)
                {
                    if (s.SlewTimeUs > 0) { t += s.SlewTimeUs; v = s.HoldVoltage; pts.Add(new WaveformPoint(t, v)); }
                    else if (Math.Abs(s.HoldVoltage - v) > 1e-9)
                    {
                        // 시간 0 의 전압 변화 = 수직 계단. 점을 안 찍으면 그래프가 이전 전압을
                        // 유지한 것처럼 보여, 편집값이 잘못됐다는 사실이 화면에서 사라진다.
                        v = s.HoldVoltage; pts.Add(new WaveformPoint(t, v));
                    }
                    if (s.HoldTimeUs > 0) { t += s.HoldTimeUs; pts.Add(new WaveformPoint(t, v)); }
                }
            }
            return pts;
        }

        /// <summary>
        /// 이 파형이 낼 수 있는 최대 반복 주파수 [kHz].
        /// 두 채널 중 <b>긴 쪽</b>이 한 주기를 정한다 — 짧은 쪽은 기다린다.
        /// </summary>
        public static double MaxFrequencyKHz(EpsonWaveformDocument doc)
        {
            if (doc == null) return 0;
            double totalUs = Math.Max(doc.ComA.TotalTimeUs, doc.ComB.TotalTimeUs);
            return totalUs <= 0 ? 0 : 1000.0 / totalUs;
        }

        /// <summary>그래프 Y축 범위 — 0 과 Vst 는 항상 보이게 하고 5V 단위로 올린다.</summary>
        public static (double Min, double Max) VoltageAxis(EpsonWaveformDocument doc)
        {
            if (doc == null) return (0, 40);

            double max = doc.Vst;
            foreach (var ch in new[] { doc.ComA, doc.ComB })
                foreach (var p in ch.Pulses)
                    foreach (var s in p.Segments)
                        max = Math.Max(max, s.HoldVoltage);

            return (0, Math.Max(5, Math.Ceiling(max / 5.0) * 5.0));
        }
    }
}
