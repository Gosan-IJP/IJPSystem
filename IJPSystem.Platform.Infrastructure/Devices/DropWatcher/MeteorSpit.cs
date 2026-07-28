using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Ttp.Meteor;   // PrinterInterfaceCLS(정적), eRET, eCFGPARAMEx, SigTypes, eCfgStringPool, eHEADPOWERSTATE, MeteorConsts

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 실장 Meteor 헤드 토출(Spit DW). <see cref="VirtualSpit"/> 자리에 들어가는 실제 어댑터.
    /// x86 인프로세스로 PrinterInterfaceCLS(AnyCPU 래퍼) + 네이티브 x86 PrinterInterface.dll 을 직접 호출.
    /// 중단 절차(BUSY 재시도 → idle 폴링)는 <see cref="SpitBase"/> 를 그대로 재사용한다.
    ///
    /// <b>스핏 메커니즘</b> (PrinterInterfaceCLS.xml 근거):
    ///   · 노즐 선택 = "안 쓸 노즐"을 DisabledNozzles 로 등록 — SIG_SPIT 은 전 노즐을 쏘므로 보간(補間)적으로 선택.
    ///     PiSetStringEx(KSP_DisabledNozzles, plane, "...") → PiReloadConfigValues(BM_CFG_RELOAD_DISABLED_NOZZLES)
    ///   · 발사   = PiSetSignal(SIG_SPIT, 스핏횟수)   (전 노즐 fire, state=1~65535회)
    ///   · 주파수 = PiSetSignal(SIG_SET_TICKLE_FREQ, Hz)  (기본 8000Hz)
    ///   · 농도   = PiSetParamEx(addr, CPEX_SpitGreyLevel, level)
    ///
    /// ※ 모든 Pi* 는 정적 메서드다(덤프 확인). 인스턴스화하지 않는다.
    /// </summary>
    public sealed class MeteorSpit : SpitBase
    {
        // 연속 관찰용 — SIG_SPIT state 는 "스핏 횟수"(1~65535). 최대치로 두고, 중단은 PiAbort 로 한다.
        // (진짜 무한 연속이 필요하면 IsBusy 가 풀릴 때 재발사하도록 확장)
        private const int SpitBurstCount = 65535;

        private readonly string _configFile;
        private readonly int _nozzleCount;    // 헤드 전체 노즐 수 — disabled = 전체 − 사용 계산용
        private readonly int _firstNozzle;    // 노즐 번호 시작(0 또는 1) — 화면/레시피 규약과 일치시킬 것
        private readonly int _pcc;            // PCC 번호(1~n)
        private readonly int _hdc;            // HDC 번호(1~n)
        private readonly int _colourPlane;    // DisabledNozzles StrIdx = 0-indexed 색상 plane
        private readonly int _attachTimeoutMs;// PCC(하드웨어) 부착 대기 한도
        private readonly Action<string>? _log;
        private readonly object _io = new();

        private int _peAddress;
        private bool _opened;
        private bool _isSpitting;
        private double _frequencyHz;

        /// <param name="configFile">PrintEngine 설정(.cfg) 경로.</param>
        /// <param name="nozzleCount">헤드 전체 노즐 수(예: S800). disabled 목록 계산에 사용.</param>
        /// <param name="firstNozzle">노즐 번호 시작값. 화면/레시피가 1-based 면 1(<see cref="NozzleExpression"/> 와 동일 기준).</param>
        public MeteorSpit(string configFile, int nozzleCount,
                          Action<string>? log = null,
                          int pcc = 1, int hdc = 1, int colourPlane = 0, int firstNozzle = 1,
                          int attachTimeoutMs = 15000)
        {
            _configFile      = configFile ?? throw new ArgumentNullException(nameof(configFile));
            _nozzleCount     = nozzleCount > 0 ? nozzleCount : throw new ArgumentOutOfRangeException(nameof(nozzleCount));
            _log             = log;
            _pcc             = pcc;
            _hdc             = hdc;
            _colourPlane     = colourPlane;
            _firstNozzle     = firstNozzle;
            _attachTimeoutMs = attachTimeoutMs;
        }

        public override double FrequencyHz => _frequencyHz;
        public override bool   IsSpitting  => _isSpitting;

        public override bool IsBusy
        {
            get { lock (_io) { return _opened && PrinterInterfaceCLS.PiIsBusy(); } }
        }

        // ── 연결 ─────────────────────────────────────────────────────────
        /// <summary>엔진 시작 → 연결 → 헤드 주소 산출. 최초 Start 전 1회(Start 가 자동 호출).</summary>
        public void Open()
        {
            lock (_io)
            {
                if (_opened) return;

                Check(PrinterInterfaceCLS.PiStartPrintEngine(_configFile), "PiStartPrintEngine");
                Check(PrinterInterfaceCLS.PiOpenPrinter(),                 "PiOpenPrinter");
                if (!PrinterInterfaceCLS.PiIsProcessConnected())
                    throw new InvalidOperationException("Meteor PrintEngine 프로세스에 연결되지 않았습니다.");

                // ★ 프로세스 연결(PiIsProcessConnected) ≠ PCC(하드웨어) 부착.
                //   실장 1단계에서 프로세스는 붙었는데 PccsAttached=0(헤드 미부착)인 경우를 확인했다.
                //   실제 부착까지 확인해야 스핏이 의미 있으므로, 미부착이면 여기서 명확히 실패시킨다.
                WaitPccAttached(_attachTimeoutMs);

                // 파라미터 주소: 이 PCC/HDC 의 head 1. (ja=1 = 해당 파라미터 인덱스)
                _peAddress = PrinterInterfaceCLS.MakePEAddress(_pcc, _hdc, 1, 1);
                _opened = true;
                _log?.Invoke("Meteor 연결 완료(PCC 부착 확인)");
            }
        }

        /// <summary>
        /// PCC(하드웨어)가 실제로 부착될 때까지 대기. 엔진이 config대로 이더넷으로 PCC에 접속하는 데
        /// 시간이 걸리므로 폴링한다. 제한 시간 내 미부착이면 원인을 담아 예외.
        /// (엔진연결≠PCC부착 — 실장 1단계에서 확인. PccsAttached=0 이면 헤드가 붙지 않은 것)
        /// </summary>
        private void WaitPccAttached(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            TAppStatus st = default;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (PrinterInterfaceCLS.PiGetPrnStatus(out st) == eRET.RVAL_OK
                    && st.PccsRequired > 0 && st.PccsAttached >= st.PccsRequired)
                    return;
                Thread.Sleep(200);
            }
            throw new InvalidOperationException(
                $"PCC 미부착(PccsAttached={st.PccsAttached}/{st.PccsRequired}, State={st.PrinterState}). " +
                "PCC 전원·이더넷 링크(PCC2E 어댑터)를 확인하세요.");
        }

        // ── 토출 개시 (Spit DW = ON) ──────────────────────────────────────
        public override void Start(SpitSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            lock (_io)
            {
                if (!_opened) Open();

                // ① 노즐 선택 — 사용 노즐의 여집합을 DisabledNozzles 로 등록
                ApplyNozzleSelection(s.Nozzles);

                // ② 파라미터
                if (s.HeadVoltage is int v)
                    SetParamEx(eCFGPARAMEx.CPEX_HeadVoltage, v);   // 주의: 설정 기본값 대비 오프셋. 절대값은 CPEX_HeadVoltageAbs.
                SetParamEx(eCFGPARAMEx.CPEX_SpitGreyLevel,   s.SpitGreyLevel);
                SetParamEx(eCFGPARAMEx.CPEX_TickleGreyLevel, s.TickleGreyLevel);

                // ③ 헤드 전원 ON
                Check(PrinterInterfaceCLS.PiSetHeadPower((int)eHEADPOWERSTATE.HPS_HEAD), "PiSetHeadPower");

                // ④ 주파수(선택) — 스핏/틱클 공용 주파수
                if (s.FrequencyHz > 0)
                    Check(PrinterInterfaceCLS.PiSetSignal(Sig(SigTypes.SIG_SET_TICKLE_FREQ),
                                                          (int)Math.Round(s.FrequencyHz)), "PiSetSignal(FREQ)");

                // ⑤ 발사 — 전 노즐(=disabled 제외) 스핏
                Check(PrinterInterfaceCLS.PiSetSignal(Sig(SigTypes.SIG_SPIT), SpitBurstCount), "PiSetSignal(SPIT)");

                _frequencyHz    = s.FrequencyHz;
                CurrentSettings = s;
                _isSpitting     = true;
                _log?.Invoke($"Spit ON — 사용노즐 {s.Nozzles?.Count ?? 0}개, {s.FrequencyHz}Hz");
            }
        }

        // ── 중단 명령 1회 (SpitBase 가 BUSY 재시도·idle 폴링) ────────────────
        protected override SpitAbortResult TryAbort()
        {
            lock (_io)
            {
                if (!_opened) return SpitAbortResult.Ok;

                var r = PrinterInterfaceCLS.PiAbort();
                _isSpitting = false;
                return r switch
                {
                    eRET.RVAL_OK      => SpitAbortResult.Ok,
                    eRET.RVAL_ABORTED => SpitAbortResult.Ok,   // 이미 중단됨 — 성공 취급
                    eRET.RVAL_BUSY    => SpitAbortResult.Busy,
                    _                 => SpitAbortResult.Failed,
                };
            }
        }

        // ── 정리 ─────────────────────────────────────────────────────────
        public override void Dispose()
        {
            lock (_io)
            {
                if (!_opened) return;
                try { PrinterInterfaceCLS.PiClosePrinter(); } catch { /* 종료 경로 — 무시 */ }
                _opened = false;
            }
        }

        // ── 헬퍼 ─────────────────────────────────────────────────────────

        /// <summary>사용 노즐의 여집합을 DisabledNozzles 로 등록하고 설정을 리로드한다.</summary>
        private void ApplyNozzleSelection(IReadOnlyList<int>? used)
        {
            var usedSet = new HashSet<int>(used ?? Array.Empty<int>());
            var disabled = new List<int>();
            for (int n = _firstNozzle; n < _firstNozzle + _nozzleCount; n++)
                if (!usedSet.Contains(n)) disabled.Add(n);

            string str = FormatDisabled(disabled);
            Check(PrinterInterfaceCLS.PiSetStringEx(
                      (uint)eCfgStringPool.KSP_DisabledNozzles, (uint)_colourPlane, str, 0UL),
                  "PiSetStringEx(DisabledNozzles)");
            Check(PrinterInterfaceCLS.PiReloadConfigValues(
                      (uint)MeteorConsts.BM_CFG_RELOAD_DISABLED_NOZZLES),
                  "PiReloadConfigValues");
        }

        /// <summary>
        /// DisabledNozzles 목록 문자열 포맷.
        /// ※ TODO(실장 확인): 정확한 구분자를 제어PC .cfg 의 [Planes] DisabledNozzlesPlane0 항목으로 대조할 것.
        ///   현재는 쉼표 구분(예: "40,41,42"). 공백/범위 표기면 여기만 고치면 된다.
        /// </summary>
        private static string FormatDisabled(IReadOnlyList<int> disabled)
            => string.Join(",", disabled.Select(n => n.ToString(CultureInfo.InvariantCulture)));

        /// <summary>SignalId 조립 — Byte2=Pcc, Byte1=Hdc, Byte0=SigType (Byte3 XAddr=0).</summary>
        private int Sig(SigTypes type) => (_pcc << 16) | (_hdc << 8) | (int)type;

        private void SetParamEx(eCFGPARAMEx param, long value)
            => Check(PrinterInterfaceCLS.PiSetParamEx(_peAddress, param, value), $"PiSetParamEx({param})");

        private static void Check(eRET r, string call)
        {
            if (r != eRET.RVAL_OK)
                throw new InvalidOperationException($"Meteor {call} 실패: {r}");
        }
    }
}
