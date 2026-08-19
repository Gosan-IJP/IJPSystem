using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print.Waveform
{
    /// <summary>Com 채널. Epson 헤드는 구동 라인이 두 갈래다.</summary>
    public enum ComChannelId { ComA, ComB }

    /// <summary>
    /// 전압 조정 방식 — 세그먼트의 <b>어느 값을 고정하고 어느 값을 계산할지</b> 정한다.
    /// 둘 다 사용자가 넣으면 서로 모순되는 값이 남기 때문에 한쪽은 반드시 계산값이다.
    /// </summary>
    public enum VoltageAdjustMode
    {
        /// <summary>기울기 고정 → 천이 시간을 계산한다(SlewTime 은 읽기 전용).</summary>
        ConstantSlew,
        /// <summary>천이 시간 고정 → 기울기를 계산한다(Slew 는 읽기 전용).</summary>
        ConstantDuration,
    }

    /// <summary>ComB 를 ComA 의 복제로 둘지, 따로 편집할지.</summary>
    public enum ComAbMode { Synchronous, Independent }

    /// <summary>노즐 행 A/B 를 같은 색으로 볼지, 다른 잉크로 나눌지.</summary>
    public enum NozzleRowMode { SingleColour, DualColour }

    /// <summary>그레이 레벨 한 칸의 배정. 같은 칸에 ComA/ComB 는 배타적이다.</summary>
    public enum GreyLevelAssign { None, ComA, ComB }

    /// <summary>
    /// 구동 파형의 한 구간. <b>천이(ramp) + 유지(hold)</b> 한 쌍이다.
    ///
    /// <para><see cref="Slew"/> 와 <see cref="SlewTimeUs"/> 중 하나는 계산값이다
    /// (<see cref="VoltageAdjustMode"/> 가 정한다). 둘 다 입력으로 두면 ΔV 와 어긋나는
    /// 조합이 저장되고, 화면 그래프와 실제 토출이 달라진다.</para>
    /// </summary>
    public sealed class EpsonWaveformSegment
    {
        /// <summary>전압 변화율 [V/µs]. 항상 양수로 다룬다 — 방향은 ΔV 의 부호가 정한다.</summary>
        public double Slew { get; set; }

        /// <summary>천이 시간 [µs].</summary>
        public double SlewTimeUs { get; set; }

        /// <summary>천이 후 도달·유지할 전압 [V].</summary>
        public double HoldVoltage { get; set; }

        /// <summary>유지 시간 [µs].</summary>
        public double HoldTimeUs { get; set; }

        public double TotalTimeUs => SlewTimeUs + HoldTimeUs;

        public EpsonWaveformSegment Clone() => new()
        {
            Slew = Slew, SlewTimeUs = SlewTimeUs,
            HoldVoltage = HoldVoltage, HoldTimeUs = HoldTimeUs,
        };
    }

    /// <summary>한 펄스. 세그먼트를 차례로 이어 붙인 것.</summary>
    public sealed class EpsonWaveformPulse
    {
        public List<EpsonWaveformSegment> Segments { get; } = new();
        public int    SegmentCount => Segments.Count;
        public double TotalTimeUs  => Segments.Sum(s => s.TotalTimeUs);

        /// <summary>
        /// 세그먼트별 온도 보상 마스크(비트 하나 = 세그먼트 하나). 화면에서 편집하지 않지만
        /// 파일에 있던 값을 그대로 되돌려 써야 해서 들고 있는다 — 저장할 때 0 으로 밀면
        /// 온도 보상이 조용히 꺼진다.
        /// </summary>
        public int TempCompMask { get; set; }

        public EpsonWaveformPulse Clone()
        {
            var p = new EpsonWaveformPulse { TempCompMask = TempCompMask };
            foreach (var s in Segments) p.Segments.Add(s.Clone());
            return p;
        }

        /// <summary>
        /// New / Insert Pulse 가 넣는 기본 펄스. Vst 에서 시작해 Vst 로 돌아오는
        /// 최소 형태다 — 돌아오지 않으면 파형에 DC 성분이 남아 헤드에 전압이 계속 걸린다.
        /// </summary>
        public static EpsonWaveformPulse CreateDefault(double vst)
        {
            var p = new EpsonWaveformPulse();
            p.Segments.Add(new EpsonWaveformSegment { Slew = 0, HoldVoltage = vst,        HoldTimeUs = 2.00 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 8, HoldVoltage = vst - 19.0, HoldTimeUs = 1.00 });
            p.Segments.Add(new EpsonWaveformSegment { Slew = 8, HoldVoltage = vst,        HoldTimeUs = 2.00 });
            return p;
        }
    }

    /// <summary>한 Com 채널의 펄스 묶음. 최대 <see cref="MaxPulses"/> 개.</summary>
    public sealed class EpsonComChannel
    {
        public const int MaxPulses = 4;

        public EpsonComChannel(ComChannelId channel) => Channel = channel;

        public ComChannelId Channel { get; }
        public List<EpsonWaveformPulse> Pulses { get; } = new();

        /// <summary>이 채널이 한 번 도는 데 걸리는 시간 — 최대 주파수의 분모다.</summary>
        public double TotalTimeUs => Pulses.Sum(p => p.TotalTimeUs);

        public EpsonComChannel Clone()
        {
            var c = new EpsonComChannel(Channel);
            foreach (var p in Pulses) c.Pulses.Add(p.Clone());
            return c;
        }
    }

    /// <summary>
    /// 그레이 레벨 × 펄스 배정표. "GL n 으로 토출할 때 어느 펄스를 어느 Com 라인으로 쏠지".
    /// <para>이것이 곧 <b>액적 크기</b>를 정한다 — 배정이 없는 GL 은 토출 자체가 일어나지 않는다.</para>
    /// </summary>
    public sealed class GreyLevelMatrix
    {
        public const int Levels = 4;   // GL0~GL3
        private readonly GreyLevelAssign[,] _cells = new GreyLevelAssign[Levels, EpsonComChannel.MaxPulses];

        public GreyLevelAssign this[int greyLevel, int pulseIndex]
        {
            get => _cells[greyLevel, pulseIndex];
            set => _cells[greyLevel, pulseIndex] = value;
        }

        /// <summary>그 레벨에 배정된 펄스가 하나라도 있는가. 없으면 토출이 안 된다.</summary>
        public bool HasAnyPulse(int greyLevel)
        {
            for (int p = 0; p < EpsonComChannel.MaxPulses; p++)
                if (_cells[greyLevel, p] != GreyLevelAssign.None) return true;
            return false;
        }

        /// <summary>
        /// 세 상태 토글 — 누른 칸이 이미 그 값이면 해제한다.
        /// 같은 (GL, 펄스)에 ComA/ComB 를 동시에 둘 수 없으므로 덮어쓴다.
        /// </summary>
        public void Toggle(int greyLevel, int pulseIndex, GreyLevelAssign assign)
            => _cells[greyLevel, pulseIndex] =
                   _cells[greyLevel, pulseIndex] == assign ? GreyLevelAssign.None : assign;

        public GreyLevelMatrix Clone()
        {
            var m = new GreyLevelMatrix();
            for (int g = 0; g < Levels; g++)
                for (int p = 0; p < EpsonComChannel.MaxPulses; p++)
                    m[g, p] = _cells[g, p];
            return m;
        }
    }

    /// <summary>파형 문서 한 벌 — 화면이 편집하고 파일로 저장하는 단위.</summary>
    public sealed class EpsonWaveformDocument
    {
        public string Name { get; set; } = "";

        /// <summary>대기 전압 [V]. 모든 펄스의 시작이자 끝이다.</summary>
        public double Vst { get; set; } = 24.0;

        public VoltageAdjustMode VoltageAdjustMode { get; set; } = VoltageAdjustMode.ConstantSlew;
        public ComAbMode         ComAbMode         { get; set; } = ComAbMode.Independent;
        public NozzleRowMode     NozzleRowMode     { get; set; } = NozzleRowMode.SingleColour;

        public EpsonComChannel ComA { get; set; } = new(ComChannelId.ComA);
        public EpsonComChannel ComB { get; set; } = new(ComChannelId.ComB);
        public GreyLevelMatrix GreyLevels { get; set; } = new();

        public EpsonComChannel ChannelOf(ComChannelId id) => id == ComChannelId.ComA ? ComA : ComB;

        // ── 파일에서 따라오는 값 ──────────────────────────────────────────
        // 화면에서 편집하지 않지만 저장할 때 그대로 되돌려 써야 한다. 기본값으로 덮어쓰면
        // 헤드 종류나 온도 보상 설정이 조용히 바뀐다.

        public string HeadType { get; set; } = "EPSON_S800";
        public int    Version  { get; set; } = 1;

        public TemperatureCompensation TempComp { get; set; } = new();

        /// <summary>
        /// 파일에 있던 노즐 행 마스크가 A ≠ B 였는가. 화면 배정표는 행을 구분하지 않으므로
        /// 저장하면 두 행이 같아진다 — 그 사실을 알려 줘야 한다.
        /// </summary>
        public bool HadAsymmetricRowMasks { get; set; }

        /// <summary>New — 기본 파형 한 벌.</summary>
        public static EpsonWaveformDocument CreateDefault(double vst = 24.0, int pulseCount = 2)
        {
            var doc = new EpsonWaveformDocument { Vst = vst };
            for (int i = 0; i < Math.Clamp(pulseCount, 1, EpsonComChannel.MaxPulses); i++)
            {
                doc.ComA.Pulses.Add(EpsonWaveformPulse.CreateDefault(vst));
                doc.ComB.Pulses.Add(EpsonWaveformPulse.CreateDefault(vst));
            }

            // 배정이 하나도 없으면 어느 그레이 레벨로도 토출이 안 된다 — 첫 펄스만 걸어 둔다.
            doc.GreyLevels[1, 0] = GreyLevelAssign.ComA;
            return doc;
        }
    }

    /// <summary>파일의 [TemperatureCompensation] 블록.</summary>
    public sealed class TemperatureCompensation
    {
        public bool   Enabled    { get; set; }
        public double TCompLow   { get; set; } = 40.0;
        public double TCompHigh  { get; set; } = 45.0;
        public double VCompStart { get; set; } = 24.0;
        public double VCompEnd   { get; set; } = 29.5;
        public double VTCoef     { get; set; } = -0.0146;
    }

    /// <summary>그래프 한 점.</summary>
    public readonly record struct WaveformPoint(double TimeUs, double Volts);
}
