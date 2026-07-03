using System;
using System.IO.Ports;
using NModbus;
using NModbus.Serial;

namespace IJPSystem.Drivers.Meniscus
{
    /// <summary>
    /// LabVIEW "DMD_Read Pressure.vi" 의 시리얼 Modbus RTU 통신 변환.
    /// 사용 함수 매핑(NI Modbus Library → NModbus):
    ///   Create Serial Master   → CreateRtuMaster(SerialPort)
    ///   Write Unit ID          → UnitId
    ///   Read Holding Registers → ReadHoldingRegisters (FC03)
    ///   Shutdown               → Close/Dispose
    /// ※ 백그라운드 read 루프와 write 가 같은 포트를 쓰므로 모든 트랜잭션을 _io 락으로 직렬화한다.
    /// </summary>
    public sealed class DmdModbusRtuClient : IDisposable
    {
        private readonly DmdConfig _cfg;
        private readonly object _io = new object();
        private SerialPort? _port;
        private IModbusSerialMaster? _master;

        public DmdModbusRtuClient(DmdConfig cfg) => _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

        public bool IsConnected => _master != null;

        /// <summary>시리얼 포트 열고 RTU 마스터 생성. (Create Serial Master)</summary>
        public void Connect()
        {
            Close();
            _port = new SerialPort(_cfg.ComPort, _cfg.BaudRate, _cfg.Parity, _cfg.DataBits, _cfg.StopBits)
            {
                ReadTimeout = _cfg.TimeoutMs,
                WriteTimeout = _cfg.TimeoutMs
            };
            _port.Open();

            var factory = new ModbusFactory();
            _master = factory.CreateRtuMaster(_port);
        }

        /// <summary>홀딩 레지스터 읽기 (FC03).</summary>
        public ushort[] ReadHolding(ushort address, ushort count)
        {
            lock (_io)
            {
                EnsureConnected();
                return _master!.ReadHoldingRegisters(_cfg.UnitId, address, count);
            }
        }

        /// <summary>단일 레지스터 쓰기 (FC06).</summary>
        public void WriteSingle(ushort address, ushort value)
        {
            lock (_io)
            {
                EnsureConnected();
                _master!.WriteSingleRegister(_cfg.UnitId, address, value);
            }
        }

        /// <summary>메니스커스 압력 읽기 → 스케일 적용(kPa 등).</summary>
        public double ReadPressure()
        {
            ushort[] regs = ReadHolding(_cfg.PressureReadAddress, 1);
            // 부호(음압) 대응 위해 short 로 해석
            return (short)regs[0] * _cfg.PressureScale + _cfg.PressureOffset;
        }

        /// <summary>메니스커스 목표 압력 설정.</summary>
        public void WritePressureSetpoint(double pressure)
        {
            ushort raw = (ushort)Math.Round((pressure - _cfg.PressureOffset) / _cfg.PressureScale);
            WriteSingle(_cfg.PressureSetAddress, raw);
        }

        /// <summary>압력 제어 on/off (제어 레지스터 쓰기).</summary>
        public void WriteControl(bool enabled)
            => WriteSingle(_cfg.ControlAddress, (ushort)(enabled ? 1 : 0));

        private void EnsureConnected()
        {
            if (_master == null) throw new InvalidOperationException("연결 안 됨. Connect() 먼저 호출.");
        }

        public void Close()
        {
            lock (_io)
            {
                _master?.Dispose();
                _master = null;
                if (_port != null && _port.IsOpen) _port.Close();
                _port?.Dispose();
                _port = null;
            }
        }

        public void Dispose() => Close();
    }
}
