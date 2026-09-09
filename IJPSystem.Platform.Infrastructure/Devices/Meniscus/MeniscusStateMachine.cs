using System;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Infrastructure.Devices.Meniscus
{
    /// <summary>
    /// LabVIEW 메니스커스(DMD) 상태머신 변환: Init → Read → Set → Stop.
    /// (DMD Work State enum + DMD_Read Pressure 시리얼 Modbus RTU)
    /// 백그라운드 폴링으로 압력을 주기적으로 읽고, 목표 압력을 설정한다.
    /// </summary>
    public sealed class MeniscusStateMachine : IDisposable
    {
        private readonly DmdModbusRtuClient _client;
        private readonly DmdConfig _cfg;
        private CancellationTokenSource? _cts;
        private Task? _readLoop;

        public DmdState State { get; } = new DmdState();

        /// <summary>압력/상태 갱신 알림(UI 바인딩용).</summary>
        public event Action<DmdState>? StateChanged;

        /// <summary>압력 폴링 주기 [ms].</summary>
        public int PollIntervalMs { get; set; } = 200;

        public MeniscusStateMachine(DmdConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _client = new DmdModbusRtuClient(_cfg);
        }

        /// <summary>DMD Init : 시리얼 Modbus 연결 + 상태 초기화.</summary>
        public void Init()
        {
            SetWorkState(DmdWorkState.Init);
            try
            {
                _client.Connect();
                State.Connected = true;
                State.HasError = false;
                State.ErrorMessage = "";
                Notify();
            }
            catch (Exception ex) { Fault("Init 실패: " + ex.Message); }
        }

        /// <summary>DMD Read : 백그라운드 압력 폴링 시작.</summary>
        public void StartRead()
        {
            if (!State.Connected) { Fault("연결 안 됨"); return; }
            if (_readLoop != null && !_readLoop.IsCompleted) return;

            SetWorkState(DmdWorkState.Read);
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _readLoop = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        State.Pressure = _client.ReadPressure();
                        // RUN 상태도 같이 읽는다(주소 50). 화면 램프가 명령이 아니라 실물을
                        // 비추게 하려면 폴링이 이 값을 들고 와야 한다.
                        try { State.Running = _client.ReadRunning(); } catch { /* 압력 읽기는 살린다 */ }
                        State.HasError = false;
                        Notify();
                    }
                    catch (Exception ex) { Fault("Read 실패: " + ex.Message); }
                    try { await Task.Delay(PollIntervalMs, token); } catch { }
                }
            }, token);
        }

        /// <summary>DMD Set : 목표 압력 설정.</summary>
        public void SetPressure(double pressure)
        {
            if (!State.Connected) { Fault("연결 안 됨"); return; }
            SetWorkState(DmdWorkState.Set);
            try
            {
                _client.WritePressureSetpoint(pressure);

                // SV 를 되읽어 확인한다. 쓰기 주소(1)는 write-only 라 그쪽으로는 확인할 수 없고,
                // 장비가 물고 있는 값은 52번에만 나온다. 쓰기 성공 ≠ 반영이라 이 한 번이 필요하다.
                try
                {
                    double echoed = _client.ReadSetpoint();
                    State.Value = echoed;
                    if (Math.Abs(echoed - pressure) > 0.0005) WarnSetpointMismatch(pressure, echoed);
                }
                catch { State.Value = pressure; }   // 되읽기 실패는 쓰기 실패가 아니다 — 값은 그대로 둔다

                State.HasError = false;
                Notify();
                // 설정 후 다시 Read 상태로 복귀(폴링 유지)
                if (_readLoop != null && !_readLoop.IsCompleted) SetWorkState(DmdWorkState.Read);
            }
            catch (Exception ex) { Fault(Explain("압력 설정", _cfg.PressureSetAddress, ex)); }
        }

        /// <summary>
        /// SV 되읽기가 쓴 값과 다를 때, <b>RUN 상태를 먼저 보고</b> 이유를 고른다.
        ///
        /// <para>랩뷰의 "DMD Set" 은 SV(주소 1)와 RUN(주소 0)을 <b>한 번에</b> 쓴다. 우리 화면은
        /// [Set Value] 와 [Toggle Meniscus] 로 갈라져 있어서, STOP 인 채로 SV 만 쓰면 장비가
        /// 값을 물지 않고 0 을 돌려준다. 그때 "스케일 또는 주소를 확인하라" 고 하면 방금 맞춰 놓은
        /// 주소를 다시 뒤지게 된다 — 정작 필요한 것은 RUN 을 켜라는 한마디다(2026-09-08).</para>
        /// </summary>
        private void WarnSetpointMismatch(double wroteKpa, double echoedKpa)
        {
            bool running;
            try { running = _client.ReadRunning(); }
            catch { running = true; }   // 상태를 못 읽으면 STOP 이라 단정하지 않는다

            string wrote  = $"{wroteKpa * 1000:F0}Pa";
            string echoed = $"{echoedKpa * 1000:F0}Pa";

            Fault(running
                ? $"목표압력이 반영되지 않았습니다 — 쓴 값 {wrote}, 장비 값 {echoed}. " +
                  "장비는 RUN 상태입니다. 스케일 또는 주소를 확인하세요."
                : $"목표압력이 아직 반영되지 않았습니다 — 쓴 값 {wrote}, 장비 값 {echoed}. " +
                  "장비가 STOP 상태입니다 — [Toggle Meniscus] 로 RUN 을 켜야 값을 뭅니다. " +
                  "(주소·스케일 문제가 아닙니다)");
        }

        /// <summary>
        /// 쓰기 실패를 사람이 읽을 말로 바꾼다.
        ///
        /// <para>NModbus 의 <c>SlaveException.Message</c> 는 영문 규격 설명을 여섯 줄 쏟아낸다
        /// ("For a controller with 100 registers, the PDU addresses…"). 화면에 그게 그대로
        /// 뜨면 무엇을 해야 하는지 알 수 없다. 정작 필요한 정보는 <b>어느 주소에 무엇을 쓰려다
        /// 거절당했는가</b>와, Illegal Data Address 는 배선이 아니라 <b>주소가 틀린 것</b>이라는
        /// 사실 둘뿐이다(2026-09-08 11호기).</para>
        /// </summary>
        private static string Explain(string what, ushort address, Exception ex)
        {
            if (ex is NModbus.SlaveException se && se.SlaveExceptionCode == 2)
                return $"{what} 실패 — 장비에 주소 {address}(0x{address:X4}) 가 없습니다(Illegal Data Address). " +
                       "배선·통신은 정상입니다. MeniscusConfig.json 의 주소가 다른 호기에서 옮겨 적은 " +
                       "placeholder 인지 확인하세요 — Tools\\SerialScan 의 3_SweepAddresses 로 " +
                       "실제 쓸 수 있는 주소를 먼저 찾아야 합니다.";

            if (ex is NModbus.SlaveException se2)
                return $"{what} 실패 — 장비가 거절했습니다(Modbus 예외 {se2.SlaveExceptionCode}, 주소 {address}).";

            return $"{what} 실패({ex.GetType().Name}): {ex.Message}";
        }

        /// <summary>압력 제어 on/off (제어 레지스터). 기존 UI 의 Toggle 대응.</summary>
        public void SetControl(bool enabled)
        {
            if (!State.Connected) { Fault("연결 안 됨"); return; }
            try
            {
                _client.WriteControl(enabled);

                // 명령을 넣었다는 것과 장비가 실제로 도는 것은 다르다. 쓰기 주소(0)는 write-only 라
                // 되읽을 수 없으므로 상태 레지스터(50)를 본다 — 스트로브에서 겪은 것과 같은 함정이다.
                try
                {
                    bool actual = _client.ReadRunning();
                    if (actual != enabled)
                        Fault($"{(enabled ? "RUN" : "STOP")} 명령은 나갔지만 장비는 " +
                              $"{(actual ? "RUN" : "STOP")} 입니다. 장비 전면 조작부나 인터록을 확인하세요.");
                }
                catch { /* 상태를 못 읽는 것은 제어 실패가 아니다 */ }

                State.HasError = false;
                Notify();
            }
            catch (Exception ex) { Fault(Explain("출력 ON/OFF", _cfg.ControlAddress, ex)); }
        }

        /// <summary>DMD Stop : 폴링 정지 + 연결 해제.</summary>
        public void Stop()
        {
            SetWorkState(DmdWorkState.Stop);
            try { _cts?.Cancel(); _readLoop?.Wait(500); } catch { }
            _client.Close();
            State.Connected = false;
            Notify();
        }

        // ---- 내부 ----
        private void SetWorkState(DmdWorkState s) { State.WorkState = s; Notify(); }
        private void Fault(string msg) { State.HasError = true; State.ErrorMessage = msg; Notify(); }
        private void Notify() => StateChanged?.Invoke(State);

        public void Dispose() { Stop(); _cts?.Dispose(); _client.Dispose(); }
    }
}
