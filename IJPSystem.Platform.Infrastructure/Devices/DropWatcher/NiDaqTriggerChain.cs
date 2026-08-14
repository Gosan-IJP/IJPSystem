using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// NI-DAQmx C API(nicaiu.dll) P/Invoke — 카운터 출력(펄스 생성)에 필요한 최소 표면.
    ///
    /// <b>관리형 API(NationalInstruments.DAQmx)를 쓰지 않는 이유</b>: 그 어셈블리는 .NET Framework
    /// 4.x 전용이라 net8.0 에서 참조할 수 없다(NI 는 .NET Core 지원 계획을 밝힌 바 없음).
    /// C API 는 헤더가 공개돼 있고 빌드 시점 의존성이 없어(DllImport 는 지연 바인딩) NI 미설치
    /// 개발 PC 에서도 컴파일된다 — Drivers.Vision 의 ImaqdxInterop 과 같은 전략.
    ///
    /// <b>P/Invoke 주의</b>:
    ///   · TaskHandle 은 DAQmx 8.9 부터 <c>void*</c> → 반드시 <see cref="IntPtr"/>. uint 로 쓰면 손상.
    ///   · 호출규약은 __stdcall (헤더의 <c>__CFUNC</c>).
    /// </summary>
    internal static class DaqmxInterop
    {
        private const string Dll = "nicaiu.dll";
        private const CallingConvention Conv = CallingConvention.StdCall;

        // ── 상수 (NIDAQmx.h) ──────────────────────────────────────────────────
        public const int Val_High        = 10192;   // 유휴 상태 High
        public const int Val_Low         = 10214;   // 유휴 상태 Low
        public const int Val_FiniteSamps = 10178;
        public const int Val_ContSamps   = 10123;
        public const int Val_Rising      = 10280;
        public const int Val_Falling     = 10171;

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int DAQmxCreateTask(string taskName, out IntPtr taskHandle);

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int DAQmxCreateCOPulseChanTicks(
            IntPtr taskHandle, string counter, string nameToAssignToChannel,
            string sourceTerminal, int idleState, int initialDelay, int lowTicks, int highTicks);

        [DllImport(Dll, CallingConvention = Conv)]
        public static extern int DAQmxCfgImplicitTiming(IntPtr taskHandle, int sampleMode, ulong sampsPerChan);

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int DAQmxCfgDigEdgeStartTrig(IntPtr taskHandle, string triggerSource, int triggerEdge);

        /// <param name="data">bool32 — 0/1.</param>
        [DllImport(Dll, CallingConvention = Conv)]
        public static extern int DAQmxSetStartTrigRetriggerable(IntPtr taskHandle, uint data);

        [DllImport(Dll, CallingConvention = Conv)]
        public static extern int DAQmxStartTask(IntPtr taskHandle);

        [DllImport(Dll, CallingConvention = Conv)]
        public static extern int DAQmxStopTask(IntPtr taskHandle);

        [DllImport(Dll, CallingConvention = Conv)]
        public static extern int DAQmxClearTask(IntPtr taskHandle);

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int DAQmxGetExtendedErrorInfo(StringBuilder errorString, uint bufferSize);

        // ── 오류 판정 ─────────────────────────────────────────────────────────
        // NIDAQmx.h: #define DAQmxFailed(error) ((error)<0)
        // 즉 음수만 실패이고 <b>양수는 경고</b>다. `!= 0` 으로 판정하면 정상 경고를 실패로 처리하게 된다.
        public static bool Failed(int status)  => status < 0;
        public static bool Warning(int status) => status > 0;

        /// <summary>마지막 오류의 상세 설명(채널명·원인 포함). 실패 시 빈 문자열.</summary>
        public static string LastError()
        {
            try
            {
                // 2048B 고정 버퍼 — 두 번 호출(길이 조회 후 할당) 관용구는 NI 문서에서 확인하지
                // 못했으므로 넉넉한 고정 버퍼로 간다. 잘리면 메시지 끝만 손실된다.
                var sb = new StringBuilder(2048);
                DAQmxGetExtendedErrorInfo(sb, (uint)sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>실패면 예외, 경고면 사유 반환(호출부가 로그로 남긴다), 정상이면 null.</summary>
        public static string? Check(string what, int status)
        {
            if (Failed(status))
                throw new InvalidOperationException($"DAQmx {what} 실패 ({status}): {LastError()}");
            return Warning(status) ? $"DAQmx {what} 경고 ({status}): {LastError()}" : null;
        }
    }

    /// <summary>
    /// NI-DAQmx 하드웨어 트리거 체인 구현.
    /// (LabVIEW "Pules Reducer.vi" — 4. Component/20_DAQ LED Trigger)
    ///
    /// 구성 (전장도면 2026-08-14 확정 — 10호기 실배선):
    ///   PCC2-E PL3-2 → 토출 펄스(PFI5) → [분주기 ctr1] CO Pulse Ticks, Continuous, 소스=PFI5
    ///                                       low+high=N 로 정확한 divide-by-N
    ///                       ↓ /Dev1/Ctr1InternalOutput
    ///                     [LED ctr0] CO Pulse Ticks, Finite 1펄스, 소스=100MHz, Retriggerable
    ///                       ↓ PFI12 <b>한 가닥</b>
    ///                       ├→ iCore 조명 DIGITAL INPUT+
    ///                       └→ DWC 카메라 OPTO IN+(PIN2)
    ///
    /// ★카메라 전용 카운터가 없다 — 조명 펄스를 그대로 나눠 쓴다. 그래서 스트로브 폭 하나가
    ///   방울 선명도와 카메라 트리거 인식을 동시에 결정한다
    ///   (<see cref="TriggerChainSettings.ValidateSharedPulseWidth"/> 참조).
    ///
    /// ※ 하드웨어 없이 작성했다 — 시그니처·상수는 NI 공개 헤더 기준이다.
    ///   첫 기동은 오실로스코프로 PFI12 파형을 확인할 것.
    /// </summary>
    public sealed class NiDaqTriggerChain : ITriggerChain
    {
        private readonly TriggerChainSettings _cfg;
        private readonly Action<string>? _log;

        private IntPtr _divider = IntPtr.Zero;
        private IntPtr _led     = IntPtr.Zero;
        private IntPtr _cam     = IntPtr.Zero;

        public NiDaqTriggerChain(TriggerChainSettings cfg, Action<string>? log = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _log = log;
        }

        public bool    IsRunning            { get; private set; }
        public double  EffectiveFrameRateHz { get; private set; }
        public string? MarginWarning        { get; private set; }

        public void Start(double spitFrequencyHz)
        {
            if (IsRunning) return;

            string? bad = _cfg.Validate();
            if (bad != null) throw new InvalidOperationException($"트리거 체인 설정 오류: {bad}");

            EffectiveFrameRateHz = _cfg.EffectiveFrameRate(spitFrequencyHz);
            if (EffectiveFrameRateHz <= 0)
                throw new InvalidOperationException("실촬영 주파수가 0 입니다. 토출 주파수/분주비를 확인하세요.");

            // 실장은 공유 펄스 폭 경고까지 함께 본다(가상은 광절연 입력이 없어 해당 없음).
            MarginWarning = _cfg.StartupWarning(spitFrequencyHz);

            try
            {
                BuildDivider();
                BuildRetriggerable(out _led, "LED", _cfg.LedCounter, _cfg.LedDelayUs, _cfg.LedWidthUs);

                // 10호기 실배선은 조명과 카메라가 PFI12 한 가닥을 병렬로 문다 — 카메라 전용 카운터가
                // 없다. 이때 Cam 태스크를 만들면 쓰지도 않는 카운터를 점유할 뿐이다.
                if (!_cfg.CamSharesLed)
                    BuildRetriggerable(out _cam, "Cam", _cfg.CamCounter, _cfg.CamDelayUs, _cfg.CamWidthUs);

                // ① 소비자(LED/Cam)를 먼저 arm — 트리거 대기 상태로 만든 뒤에
                Warn(DaqmxInterop.Check("StartTask(LED)", DaqmxInterop.DAQmxStartTask(_led)));
                if (_cam != IntPtr.Zero)
                    Warn(DaqmxInterop.Check("StartTask(Cam)", DaqmxInterop.DAQmxStartTask(_cam)));
                // ② 분주기를 켠다. 순서가 반대면 첫 트리거를 놓친다.
                Warn(DaqmxInterop.Check("StartTask(Divider)", DaqmxInterop.DAQmxStartTask(_divider)));

                IsRunning = true;
            }
            catch
            {
                ClearTasks();   // 부분 생성분이 카운터를 점유한 채 남지 않게
                throw;
            }
        }

        /// <summary>
        /// 분주기: 토출 펄스 자체를 타임베이스로 쓰는 CO Pulse Ticks.
        /// low+high = N 이므로 출력 주파수 = 입력/N (정확한 divide-by-N).
        /// initialDelay 는 <b>입력 펄스 단위</b> — 초기 불안정 방울을 건너뛴다.
        /// </summary>
        private void BuildDivider()
        {
            Warn(DaqmxInterop.Check("CreateTask(Divider)",
                DaqmxInterop.DAQmxCreateTask("DW_PulseDivider", out _divider)));

            int n = Math.Max(2, _cfg.DivideRatio);
            const int high = 1;
            int low = n - high;

            Warn(DaqmxInterop.Check("CreateCOPulseChanTicks(Divider)",
                DaqmxInterop.DAQmxCreateCOPulseChanTicks(
                    _divider, _cfg.DividerCounter, "PulseDV",
                    _cfg.SpitPulseTerminal,                 // 토출 펄스를 타임베이스로
                    DaqmxInterop.Val_Low,
                    Math.Max(0, _cfg.InitialSkipPulses),
                    low, high)));

            Warn(DaqmxInterop.Check("CfgImplicitTiming(Divider)",
                DaqmxInterop.DAQmxCfgImplicitTiming(_divider, DaqmxInterop.Val_ContSamps, 1000)));
        }

        /// <summary>
        /// LED / Cam: 분주 출력을 Start Trigger 로 받는 재트리거 카운터. 트리거당 1펄스.
        /// </summary>
        private void BuildRetriggerable(out IntPtr task, string name, string counter,
                                        double delayUs, double widthUs)
        {
            task = IntPtr.Zero;
            Warn(DaqmxInterop.Check($"CreateTask({name})",
                DaqmxInterop.DAQmxCreateTask("DW_" + name, out task)));

            int highTicks = Math.Max(1, _cfg.UsToTicks(widthUs));

            // 유휴 Low 에서 펄스는 "low 구간 → high 구간" 순으로 나가므로,
            // 트리거→상승엣지 지연 = initialDelay + lowTicks 다.
            // 따라서 지연은 전부 initialDelay 에 넣고 lowTicks 는 최소값(1틱=10ns)으로 둔다.
            // (low=high 로 두면 펄스 폭만큼 지연이 더 붙어 스트로브 위상이 어긋난다)
            const int lowTicks = 1;
            int delayTicks = Math.Max(0, _cfg.UsToTicks(delayUs));

            Warn(DaqmxInterop.Check($"CreateCOPulseChanTicks({name})",
                DaqmxInterop.DAQmxCreateCOPulseChanTicks(
                    task, counter, name,
                    _cfg.TimebaseTerminal,                  // 고속 타임베이스로 미세 폭 확보
                    DaqmxInterop.Val_Low,
                    delayTicks, lowTicks, highTicks)));

            // 트리거당 1펄스
            Warn(DaqmxInterop.Check($"CfgImplicitTiming({name})",
                DaqmxInterop.DAQmxCfgImplicitTiming(task, DaqmxInterop.Val_FiniteSamps, 1)));

            Warn(DaqmxInterop.Check($"CfgDigEdgeStartTrig({name})",
                DaqmxInterop.DAQmxCfgDigEdgeStartTrig(
                    task, _cfg.DividerOutputTerminal(),
                    _cfg.TriggerOnRisingEdge ? DaqmxInterop.Val_Rising : DaqmxInterop.Val_Falling)));

            // ★ 이걸 빠뜨리면 첫 펄스만 나가고 이후 트리거가 전부 무시된다.
            //   증상이 "첫 프레임만 찍히고 멈춤" 이라 원인 추적이 매우 까다롭다.
            Warn(DaqmxInterop.Check($"SetStartTrigRetriggerable({name})",
                DaqmxInterop.DAQmxSetStartTrigRetriggerable(task, 1)));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            // 역순 — 트리거 공급부터 차단한다.
            TryStop(_divider, "Divider");
            TryStop(_cam, "Cam");
            TryStop(_led, "LED");

            ClearTasks();
            EffectiveFrameRateHz = 0;
            MarginWarning = null;
        }

        private void TryStop(IntPtr task, string name)
        {
            if (task == IntPtr.Zero) return;
            try { DaqmxInterop.DAQmxStopTask(task); }
            catch (Exception ex) { _log?.Invoke($"트리거 체인 {name} 정지 실패: {ex.Message}"); }
        }

        // 태스크를 Clear 하지 않으면 카운터가 점유된 채 남아 다음 기동이 "리소스 사용 중"으로 실패한다.
        private void ClearTasks()
        {
            foreach (var (t, n) in new[] { (_divider, "Divider"), (_cam, "Cam"), (_led, "LED") })
            {
                if (t == IntPtr.Zero) continue;
                try { DaqmxInterop.DAQmxClearTask(t); }
                catch (Exception ex) { _log?.Invoke($"트리거 체인 {n} 해제 실패: {ex.Message}"); }
            }
            _divider = _cam = _led = IntPtr.Zero;
        }

        private void Warn(string? warning)
        {
            if (!string.IsNullOrEmpty(warning)) _log?.Invoke(warning!);
        }

        public void Dispose()
        {
            Stop();
            ClearTasks();
        }
    }
}
