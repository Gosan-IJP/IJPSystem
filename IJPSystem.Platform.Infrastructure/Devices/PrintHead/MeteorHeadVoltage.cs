using System;
using System.Threading;
using IJPSystem.Platform.Domain.Models.Printing;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Ttp.Meteor;   // PrinterInterfaceCLS(정적), eRET, eCFGPARAMEx

namespace IJPSystem.Platform.Infrastructure.Devices.PrintHead
{
    /// <summary>
    /// 실물 Meteor 헤드 전압 보정.
    ///
    /// <para><b>왜 CPEX_HeadVoltage 가 아닌가</b> — 매뉴얼(MeteorCLS.xml)이 각 파라미터가 먹는
    /// 헤드 종류를 못 박아 두었는데, <c>CPEX_HeadVoltage</c>(1mV)·<c>HeadVoltageAdj</c>·
    /// <c>HeadVoltageAbs</c> 목록에 <b>엡손 계열이 하나도 없다</b>(Kyocera·Xaar·Spectra 등).
    /// <c>SIG_FORCE_HEAD_VOLTAGE</c> 도 "SG600 head" 전용이라고 적혀 있다.
    /// 이 장비 헤드(HT_EPSON_S3200)가 실제로 올라 있는 것은
    /// <c>CPEX_WF_VScaleMode</c> / <c>CPEX_WF_VScaleCoeff</c> 두 개뿐이다 —
    /// 파형 전압을 통째로 배율로 키우고 줄이는 길이다. 화면 단위가 % 인 것과도 맞는다.</para>
    ///
    /// <para><b>프린터 세션을 열지 않는다.</b> Meteor 는 한 프로세스가 하나만 소유할 수 있고,
    /// 이 앱에서는 <see cref="MeteorStatusMonitor"/> 가 이미 붙어 있다. 여기서 또 열면 서로
    /// 뺏는다 — 붙어 있는지만 상태로 확인하고 파라미터만 얹는다.</para>
    /// </summary>
    public sealed class MeteorHeadVoltage : IHeadVoltage
    {
        private readonly IMeteorStatusSource? _status;
        private readonly Action<string>? _log;
        private readonly object _io = new();

        /// <summary>
        /// 파형 전압 스케일 모드. S3200 은 1 = <b>펄스 폭을 유지</b>하며 전압만 키운다
        /// (매뉴얼: "One means ... keep pulse width constant (Dimatix heads, HT_EPSON_S3200)").
        /// 0 을 넣으면 스케일 자체가 꺼져 계수를 넣어도 아무 일도 일어나지 않는다.
        /// </summary>
        public int ScaleMode { get; set; } = 1;

        /// <summary>한 걸음의 크기(%). 0 이하면 한 번에 넣는다. (랩뷰 [Rate of volt] 자리)</summary>
        public double StepPercent { get; set; } = 5.0;

        /// <summary>걸음 사이 간격(ms).</summary>
        public int StepDelayMs { get; set; } = 20;

        public double AppliedPercent { get; private set; }

        /// <param name="status">헤드가 붙어 있는지 알려 주는 곳. null 이면 확인할 길이 없어 막는다.</param>
        public MeteorHeadVoltage(IMeteorStatusSource? status, Action<string>? log = null)
        {
            _status = status;
            _log    = log;
        }

        public string? NotReadyReason
        {
            get
            {
                if (_status == null) return "헤드(Meteor)가 설정되어 있지 않습니다 — AppConfig 의 DriverMode.Head 확인.";

                var st = _status.Poll();
                if (!st.Reachable) return string.IsNullOrEmpty(st.Detail) ? "헤드에 연결되지 않았습니다." : st.Detail;
                if (!st.Connected) return $"PCC 미부착({st.PccsAttached}/{st.PccsRequired}) — 전압을 걸 헤드가 없습니다.";
                return null;
            }
        }

        public bool IsAvailable => NotReadyReason == null;

        public void Apply(double percent)
        {
            string? why = NotReadyReason;
            if (why != null) throw new InvalidOperationException(why);

            lock (_io)
            {
                // ① 스케일을 켠다. 꺼진 채로 계수만 넣으면 조용히 아무 일도 안 일어난다 —
                //    화면에는 걸린 것처럼 보이는데 액적 속도는 그대로인, 가장 알아채기 어려운 실패다.
                SetParam(eCFGPARAMEx.CPEX_WF_VScaleMode, ScaleMode);

                // ② 계수를 단계로 올린다.
                foreach (double p in HeadVoltageScale.Ramp(AppliedPercent, percent, StepPercent))
                {
                    SetCoefficient(HeadVoltageScale.ToCoefficient(p));
                    AppliedPercent = p;                       // 중간에 끊겨도 어디까지 갔는지 남는다
                    if (StepDelayMs > 0 && p != percent) Thread.Sleep(StepDelayMs);
                }
            }

            _log?.Invoke($"헤드 전압 보정 {AppliedPercent:F2}% (배율 {HeadVoltageScale.ToCoefficient(AppliedPercent):F3})");
        }

        /// <summary>
        /// 대상 주소 — PCC·HDC·헤드·젯팅어셈블리 모두 0(=ALL).
        ///
        /// <para>헤드 하나에만 걸면 나머지 헤드가 다른 속도로 토출한다. 그건 인쇄물에서
        /// 열마다 착탄이 어긋나는 것으로만 드러나서, 원인을 찾는 데 오래 걸린다.</para>
        /// </summary>
        private static int Address => PrinterInterfaceCLS.MakePEAddress(0, 0, 0, 0);

        private static void SetParam(eCFGPARAMEx param, long value)
            => Check(PrinterInterfaceCLS.PiSetParamEx(Address, param, value), $"PiSetParamEx({param})");

        /// <summary>계수는 소수다 — 정수 오버로드로 넣으면 1.25 가 1 로 잘린다(매뉴얼의 기본 스케일링 사용).</summary>
        private static void SetCoefficient(double coeff)
            => Check(PrinterInterfaceCLS.PiSetParamEx(Address, eCFGPARAMEx.CPEX_WF_VScaleCoeff, (decimal)coeff),
                     "PiSetParamEx(CPEX_WF_VScaleCoeff)");

        private static void Check(eRET r, string call)
        {
            if (r != eRET.RVAL_OK)
                throw new InvalidOperationException($"Meteor {call} 실패: {r}");
        }
    }
}
