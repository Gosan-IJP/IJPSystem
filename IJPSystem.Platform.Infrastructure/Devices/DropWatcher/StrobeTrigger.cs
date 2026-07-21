using System;
using System.IO.Ports;
using NModbus;
using NModbus.Serial;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    // ① 트리거(펄스 분주 + LED/Cam 동기)는 TriggerChain.cs 의 ITriggerChain 이 담당한다.
    //   이전의 IPulseReducer 는 분주만 다뤄 LED/Cam 동기가 빠져 있어 대체됨.

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

        /// <summary>Init() 성공 후 통신 가능 상태인지. 2점 측정은 이게 true 라야 의미가 있다.</summary>
        bool IsConnected { get; }

        /// <summary>마지막으로 적용한 지연[us]. 측정 결과 기록/검증용.</summary>
        double LastDelayMicroseconds { get; }
    }

    /// <summary>
    /// iCore 스트로브 Modbus/RTU 설정. 주소·스케일은 <b>장비 매뉴얼로 확인 후 교체</b>할 placeholder.
    /// (Meniscus 의 <see cref="Meniscus.DmdConfig"/> 와 같은 형식 — 같은 RS-485 계열이라 규약을 맞춘다)
    /// </summary>
    public sealed class StrobeConfig
    {
        public string ComPort  { get; set; } = "COM4";
        public int    BaudRate { get; set; } = 9600;
        public Parity Parity   { get; set; } = Parity.None;
        public int    DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public int    TimeoutMs { get; set; } = 1000;

        /// <summary>Modbus 슬레이브(Unit) ID.</summary>
        public byte UnitId { get; set; } = 1;

        /// <summary>Delay Time 홀딩 레지스터 시작 주소. (iCore_Set Delay time.vi = FC16)</summary>
        public ushort DelayRegister { get; set; } = 0x0000;

        /// <summary>
        /// µs 1단위당 레지스터 증분. 컨트롤러 분해능이 0.1µs 면 10, 1µs 면 1.
        /// 이 값이 틀리면 지연이 통째로 배수만큼 어긋나 속도가 그 배수로 틀린다.
        /// </summary>
        public double RegisterScale { get; set; } = 1.0;

        /// <summary>
        /// true 면 지연을 32bit(상위/하위 2워드)로 쓴다. 워드 순서는 장비에 맞춰
        /// <see cref="HighWordFirst"/> 로 조정. false 면 16bit 단일 레지스터.
        /// </summary>
        public bool Use32BitDelay { get; set; } = false;

        /// <summary>32bit 쓰기의 워드 순서(true=상위 먼저, big-endian 워드).</summary>
        public bool HighWordFirst { get; set; } = true;

        /// <summary>발광 on/off 레지스터 주소. 음수면 Enable() 은 no-op.</summary>
        public int EnableRegister { get; set; } = -1;

        /// <summary>지연 변경 후 발광이 안정될 때까지의 대기[ms]. (LabVIEW SettleMs)</summary>
        public int SettleMs { get; set; } = 50;
    }

    // ---------- 구현 ----------

    /// <summary>
    /// iCore 스트로브 컨트롤러 — Modbus RTU 구현.
    /// LabVIEW <c>iCore_Set Delay time.vi</c> = Write Multiple Holding Registers(FC16) 에 대응한다.
    /// ※ 지연은 촬영 스레드에서 연속 호출되므로 모든 트랜잭션을 _io 락으로 직렬화한다
    ///   (<see cref="Meniscus.DmdModbusRtuClient"/> 와 동일 규약).
    /// </summary>
    public sealed class ICoreStrobe : IStrobeController
    {
        private readonly StrobeConfig _cfg;
        private readonly object _io = new object();
        private SerialPort? _port;
        private IModbusSerialMaster? _master;

        public ICoreStrobe(StrobeConfig? cfg = null) => _cfg = cfg ?? new StrobeConfig();

        public bool   IsConnected           => _master != null;
        public double LastDelayMicroseconds { get; private set; } = double.NaN;

        /// <summary>지연 변경 후 발광 안정 대기[ms]. 시퀀스가 이 값을 참조한다.</summary>
        public int SettleMs => _cfg.SettleMs;

        public void Init()
        {
            Close();
            _port = new SerialPort(_cfg.ComPort, _cfg.BaudRate, _cfg.Parity, _cfg.DataBits, _cfg.StopBits)
            {
                ReadTimeout  = _cfg.TimeoutMs,
                WriteTimeout = _cfg.TimeoutMs,
            };
            _port.Open();
            _master = new ModbusFactory().CreateRtuMaster(_port);
        }

        public void SetDelayMicroseconds(double us)
        {
            if (us < 0) throw new ArgumentOutOfRangeException(nameof(us), "지연은 음수일 수 없습니다.");

            lock (_io)
            {
                EnsureConnected();

                uint raw = (uint)Math.Round(us * _cfg.RegisterScale);
                if (_cfg.Use32BitDelay)
                {
                    ushort hi = (ushort)(raw >> 16), lo = (ushort)(raw & 0xFFFF);
                    _master!.WriteMultipleRegisters(_cfg.UnitId, _cfg.DelayRegister,
                        _cfg.HighWordFirst ? new[] { hi, lo } : new[] { lo, hi });
                }
                else
                {
                    // 16bit 레지스터를 넘기면 조용히 잘린 값이 써져 지연이 엉뚱해진다 → 명시적으로 실패시킨다.
                    if (raw > ushort.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(us),
                            $"지연 {us:F1}us(raw {raw}) 이 16bit 범위를 넘습니다. StrobeConfig.Use32BitDelay 를 켜세요.");
                    _master!.WriteMultipleRegisters(_cfg.UnitId, _cfg.DelayRegister, new[] { (ushort)raw });
                }
            }
            LastDelayMicroseconds = us;
        }

        public void Enable(bool on)
        {
            if (_cfg.EnableRegister < 0) return;   // 미배선 장비 — 발광은 상시 ON
            lock (_io)
            {
                EnsureConnected();
                _master!.WriteSingleRegister(_cfg.UnitId, (ushort)_cfg.EnableRegister, (ushort)(on ? 1 : 0));
            }
        }

        private void EnsureConnected()
        {
            if (_master == null) throw new InvalidOperationException("스트로브 미연결. Init() 먼저 호출.");
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

    /// <summary>
    /// 가상 스트로브 — 실장 컨트롤러 없이 2점 측정 시퀀스를 검증하기 위한 대역.
    /// 적용한 지연을 <paramref name="onDelayApplied"/> 로 흘려보내, 가상 카메라가 그 지연에 맞는
    /// 낙하 위치로 액적을 합성하게 한다(지연을 안 흘리면 두 프레임이 같아 ΔY=0 → 속도 0 이 나온다).
    /// </summary>
    public sealed class VirtualStrobe : IStrobeController
    {
        private readonly Action<double>? _onDelayApplied;

        public VirtualStrobe(Action<double>? onDelayApplied = null) => _onDelayApplied = onDelayApplied;

        public bool   IsConnected           { get; private set; }
        public double LastDelayMicroseconds { get; private set; } = double.NaN;
        public bool   IsEnabled             { get; private set; }

        public void Init() => IsConnected = true;

        public void SetDelayMicroseconds(double us)
        {
            LastDelayMicroseconds = us;
            _onDelayApplied?.Invoke(us);
        }

        public void Enable(bool on) => IsEnabled = on;
        public void Dispose() => IsConnected = false;
    }
}
