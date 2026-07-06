using System;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// ① 트리거: NI-DAQ 펄스 분주기. (LabVIEW "Pules Reducer.vi")
    /// 헤드 토출 주파수(F)를 받아 1/N 로 분주해 스트로브 트리거를 만든다.
    /// DAQmx CO-Pulse Generation + Digital Edge Start Trigger.
    /// </summary>
    public interface IPulseReducer : IDisposable
    {
        void Open();
        /// <summary>분주비 N (매 N번째 펄스마다 1회 트리거).</summary>
        int Divider { get; set; }
        void Start();
        void Stop();
    }

    /// <summary>
    /// ② 조명: iCore Strobe Controller. (LabVIEW "iCore_init / iCore_Set Delay time")
    /// 트리거를 받아 Delay time 적용 후 LED 발광 → 액적 정지.
    /// </summary>
    public interface IStrobeController : IDisposable
    {
        void Init();
        /// <summary>발광 지연 [us] — 액적 위상(궤적 위치) 조정의 핵심.</summary>
        void SetDelayMicroseconds(double us);
        void Enable(bool on);
    }

    // ---------- 스켈레톤 ----------

    /// <summary>NI-DAQ 펄스 분주기 골격 (NationalInstruments.DAQmx 필요).</summary>
    public sealed class NiDaqPulseReducer : IPulseReducer
    {
        private readonly string _ctr, _trigSrc;
        public NiDaqPulseReducer(string counter = "Dev1/ctr0", string triggerSource = "/Dev1/PFI0")
        { _ctr = counter; _trigSrc = triggerSource; }

        public int Divider { get; set; } = 10;

        public void Open()
        {
            // TODO: Task 생성, COChannels.CreatePulseChannelTicks/Time,
            //       Timing.ConfigureImplicit, Triggers.StartTrigger.ConfigureDigitalEdge(_trigSrc).
            //       분주는 카운터 입력 분주 또는 소프트웨어로 구현.
            _ = (_ctr, _trigSrc);
        }
        public void Start() { /* TODO: task.Start() */ }
        public void Stop()  { /* TODO: task.Stop()  */ }
        public void Dispose() => Stop();
    }

    /// <summary>iCore 시리얼 스트로브 골격.</summary>
    public sealed class ICoreStrobe : IStrobeController
    {
        private readonly string _com;
        private readonly int _baud;
        private System.IO.Ports.SerialPort? _port;

        public ICoreStrobe(string com = "COM4", int baud = 115200) { _com = com; _baud = baud; }

        public void Init()
        {
            _port = new System.IO.Ports.SerialPort(_com, _baud) { NewLine = "\r\n", ReadTimeout = 500, WriteTimeout = 500 };
            _port.Open(); // TODO: iCore_init 명령 시퀀스
        }
        public void SetDelayMicroseconds(double us) => _port?.WriteLine($"DELAY {us:F0}"); // TODO: 실제 프로토콜
        public void Enable(bool on) => _port?.WriteLine(on ? "STROBE ON" : "STROBE OFF");
        public void Dispose() { if (_port != null && _port.IsOpen) _port.Close(); _port?.Dispose(); }
    }
}
