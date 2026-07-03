using System;
using System.IO.Ports;

namespace IJPSystem.Drivers.IO.Strobe
{
    /// <summary>
    /// iCore Strobe Controller (VISA 시리얼) 기반 LED 스트로브.
    /// LabVIEW "20_DAQ LED Trigger/2.Component/iCore Strobe Controler" 대응.
    /// 시리얼 ASCII 명령으로 지연/폭/발광 제어(프로토콜은 컨트롤러 매뉴얼 기준으로 교체).
    /// </summary>
    public sealed class ICoreStrobeController : ILedStrobe
    {
        private readonly string _com;
        private readonly int _baud;
        private SerialPort? _port;

        public ICoreStrobeController(string com = "COM4", int baud = 115200)
        { _com = com; _baud = baud; }

        public void Open()
        {
            Close();
            _port = new SerialPort(_com, _baud) { ReadTimeout = 500, WriteTimeout = 500, NewLine = "\r\n" };
            _port.Open();
        }

        private void Send(string cmd) { EnsureOpen(); _port!.WriteLine(cmd); }
        private void EnsureOpen() { if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Open 먼저 호출."); }

        // 아래 명령 문자열은 예시 — 실제 iCore 프로토콜로 교체.
        public void Enable(bool on) => Send(on ? "STROBE ON" : "STROBE OFF");
        public void SetDelayMicroseconds(double us) => Send($"DELAY {us:F0}");
        public void SetWidthMicroseconds(double us) => Send($"WIDTH {us:F0}");
        public void Fire() => Send("TRIG");

        public void Close()
        {
            if (_port != null && _port.IsOpen) _port.Close();
            _port?.Dispose(); _port = null;
        }
        public void Dispose() => Close();
    }
}
