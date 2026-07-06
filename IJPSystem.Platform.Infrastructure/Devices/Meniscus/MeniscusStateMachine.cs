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
                State.Value = pressure;
                State.HasError = false;
                Notify();
                // 설정 후 다시 Read 상태로 복귀(폴링 유지)
                if (_readLoop != null && !_readLoop.IsCompleted) SetWorkState(DmdWorkState.Read);
            }
            catch (Exception ex) { Fault("Set 실패: " + ex.Message); }
        }

        /// <summary>압력 제어 on/off (제어 레지스터). 기존 UI 의 Toggle 대응.</summary>
        public void SetControl(bool enabled)
        {
            if (!State.Connected) { Fault("연결 안 됨"); return; }
            try
            {
                _client.WriteControl(enabled);
                State.HasError = false;
                Notify();
            }
            catch (Exception ex) { Fault("Control 실패: " + ex.Message); }
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
