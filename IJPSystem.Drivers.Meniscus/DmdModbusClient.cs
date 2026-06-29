using System;
using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.Serial;

namespace IJPSystem.Drivers.Meniscus
{
    /// <summary>통신 전송 방식. 원본 VISA 패턴 "?*::(INSTR|SOCKET)"에 대응.</summary>
    public enum ModbusTransport
    {
        /// <summary>Modbus TCP (VISA SOCKET 대응).</summary>
        Tcp,
        /// <summary>Modbus RTU / 시리얼 (VISA INSTR 대응).</summary>
        Rtu
    }

    /// <summary>
    /// LabVIEW "DMD_Read Pressure.vi" 및 관련 Modbus 통신 변환.
    /// 사용 함수 매핑:
    ///   Read Holding Registers (FC03)     → ReadHoldingRegisters
    ///   Write Unit ID                     → UnitId 프로퍼티
    ///   Read/Write Transmission Data Unit → NModbus 내부 ADU 처리
    /// TCP / RTU 두 방식을 모두 지원한다 (원본 INSTR|SOCKET 과 동일).
    /// </summary>
    public sealed class DmdModbusClient : IDisposable
    {
        private TcpClient? _tcp;
        private SerialPort? _serial;
        private IModbusMaster? _master;

        /// <summary>Modbus 슬레이브 ID (Unit ID).</summary>
        public byte UnitId { get; set; } = DmdRegisterMap.DefaultUnitId;

        public bool IsConnected => _master != null;

        /// <summary>Modbus TCP 연결.</summary>
        public void ConnectTcp(string ip, int port = 502, int timeoutMs = 1000)
        {
            Close();
            _tcp = new TcpClient();
            _tcp.Connect(ip, port);
            _tcp.ReceiveTimeout = timeoutMs;
            _tcp.SendTimeout = timeoutMs;

            var factory = new ModbusFactory();
            _master = factory.CreateMaster(_tcp);
        }

        /// <summary>Modbus RTU(시리얼) 연결.</summary>
        public void ConnectRtu(string comPort, int baudRate = 9600,
            Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One,
            int timeoutMs = 1000)
        {
            Close();
            _serial = new SerialPort(comPort, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs
            };
            _serial.Open();

            var factory = new ModbusFactory();
            _master = factory.CreateRtuMaster(_serial);
        }

        /// <summary>전송 방식 enum 으로 분기 연결하는 헬퍼.</summary>
        public void Connect(ModbusTransport transport, string resource, int portOrBaud)
        {
            if (transport == ModbusTransport.Tcp)
                ConnectTcp(resource, portOrBaud);
            else
                ConnectRtu(resource, portOrBaud);
        }

        /// <summary>홀딩 레지스터 읽기 (FC03). DMD_Read Pressure 의 핵심.</summary>
        public ushort[] ReadHoldingRegisters(ushort startAddress, ushort count)
        {
            EnsureConnected();
            return _master!.ReadHoldingRegisters(UnitId, startAddress, count);
        }

        /// <summary>단일 레지스터 쓰기 (FC06).</summary>
        public void WriteSingleRegister(ushort address, ushort value)
        {
            EnsureConnected();
            _master!.WriteSingleRegister(UnitId, address, value);
        }

        /// <summary>다중 레지스터 쓰기 (FC16). 32bit 값 등에 사용.</summary>
        public void WriteMultipleRegisters(ushort startAddress, ushort[] values)
        {
            EnsureConnected();
            _master!.WriteMultipleRegisters(UnitId, startAddress, values);
        }

        private void EnsureConnected()
        {
            if (_master == null)
                throw new InvalidOperationException("Modbus 연결이 되어 있지 않습니다. Connect 먼저 호출.");
        }

        public void Close()
        {
            _master?.Dispose();
            _master = null;
            _tcp?.Close();
            _tcp = null;
            if (_serial != null && _serial.IsOpen) _serial.Close();
            _serial?.Dispose();
            _serial = null;
        }

        public void Dispose() => Close();
    }
}
