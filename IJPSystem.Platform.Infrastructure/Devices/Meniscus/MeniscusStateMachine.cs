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

        /// <summary>
        /// [Set Value] 뒤 RUN·SV 를 다시 읽어 볼 횟수와 간격. 장비가 STOP → RUN 으로 넘어가는
        /// 시간을 기다린다 — 쓰자마자 한 번만 읽으면 넘어가는 중인 장비를 STOP 으로 판정한다.
        /// </summary>
        public int ConfirmTries      { get; set; } = 5;
        public int ConfirmIntervalMs { get; set; } = 100;

        /// <summary>SV 되읽기 허용 오차[kPa] = 0.5Pa. 장비 분해능은 0.1Pa 다.</summary>
        private const double SetpointToleranceKpa = 0.0005;

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

        /// <summary>
        /// DMD Set : 목표 압력을 쓰고 <b>이어서 RUN 을 건다</b> — 랩뷰 "DMD Set" 과 같은 한 동작
        /// (Func06: 주소 1 = SV → 주소 0 = 1).
        ///
        /// <para>예전에는 이 둘을 [Set Value](SV 만)와 [Toggle Meniscus](RUN 만)로 갈라 놓았다.
        /// 그래서 SV 없이 RUN 만 들어가거나 STOP 인 채로 SV 만 들어가, 장비가 어느 쪽도 물지 않았다
        /// (2026-09-08 11호기 · 2026-09-11 10호기 "RUN 명령은 나갔지만 장비는 STOP").
        /// [Toggle Meniscus] 는 DMD 가 아니라 밸브/신호 계통이다.</para>
        /// </summary>
        public void SetPressure(double pressure)
        {
            if (!State.Connected) { Fault("연결 안 됨"); return; }
            SetWorkState(DmdWorkState.Set);

            try { _client.WritePressureSetpoint(pressure); }
            catch (Exception ex) { Fault(Explain("압력 설정", _cfg.PressureSetAddress, ex)); return; }

            try { _client.WriteControl(true); }
            catch (Exception ex) { Fault(Explain("RUN", _cfg.ControlAddress, ex)); return; }

            ConfirmApplied(pressure);

            // 설정 후 다시 Read 상태로 복귀(폴링 유지)
            if (_readLoop != null && !_readLoop.IsCompleted) SetWorkState(DmdWorkState.Read);
        }

        /// <summary>
        /// 쓰기 성공 ≠ 반영. 쓰기 주소 0·1 은 write-only 라 되읽을 수 없으므로
        /// 상태(주소 50)와 SV 되읽기(주소 52)로 확인한다.
        ///
        /// <para>RUN 이 아니면 SV 를 보기 전에 그 이유부터 말한다 — STOP 인 장비는 SV 를 0 으로
        /// 돌려주므로, 그때 "스케일 또는 주소를 확인하라" 고 하면 맞춰 놓은 주소를 다시 뒤지게 된다.</para>
        /// </summary>
        private void ConfirmApplied(double pressure)
        {
            bool running = false, readOk = false;
            double echoed = double.NaN;

            for (int i = 0; i < Math.Max(1, ConfirmTries); i++)
            {
                Thread.Sleep(ConfirmIntervalMs);
                try
                {
                    running = _client.ReadRunning();
                    echoed  = _client.ReadSetpoint();
                    readOk  = true;
                }
                catch { continue; }   // 되읽기 실패는 쓰기 실패가 아니다 — 다시 본다
                if (running && Math.Abs(echoed - pressure) <= SetpointToleranceKpa) break;
            }

            // 한 번도 못 읽었으면 단정하지 않는다 — 값은 쓴 그대로 둔다
            if (!readOk) { State.Value = pressure; State.HasError = false; Notify(); return; }

            State.Value   = echoed;
            State.Running = running;

            string wrote = $"{pressure * 1000:F0}Pa";
            string got   = $"{echoed * 1000:F0}Pa";

            if (!running)
                Fault($"목표압력 {wrote} 과 RUN 을 보냈지만 장비는 STOP 입니다. 통신·주소는 정상입니다 — " +
                      "장비 전면 조작부(LOCAL/REMOTE)·인터록·알람을 확인하세요.");
            else if (Math.Abs(echoed - pressure) > SetpointToleranceKpa)
                Fault($"목표압력이 반영되지 않았습니다 — 쓴 값 {wrote}, 장비 값 {got}. " +
                      "장비는 RUN 상태입니다. 스케일 또는 주소를 확인하세요.");
            else
            {
                State.HasError = false;
                Notify();
            }
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

        /// <summary>
        /// 앱 종료 — 랩뷰 "DMD Stop"(Func06: 주소 0 = 0) 처럼 압력 제어를 멈추고 연결을 닫는다.
        /// </summary>
        public void Shutdown()
        {
            if (State.Connected)
            {
                try { _client.WriteControl(false); } catch { /* 종료 중 — 막지 않는다 */ }
            }
            Stop();
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
