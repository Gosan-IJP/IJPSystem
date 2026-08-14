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

        /// <summary>
        /// 지연 레지스터 리드백(raw). 커미셔닝 검증용 — 쓰기 직후 읽어 일치하면 통신·주소가 맞다.
        /// (LabVIEW 원본도 Write 후 Read Holding Registers 로 확인한다) 미지원/실패 시 null.
        /// </summary>
        uint? TryReadDelayRaw();

        /// <summary>
        /// 지금 <b>실제로</b> 어떤 모드로 켜져 있는가(0=OFF / 1=Continuous / 2=Pulse). 읽기 실패 시 null.
        ///
        /// <para><see cref="Enable"/> 를 호출했다는 사실만으로는 켜졌다고 말할 수 없다 — 전원이
        /// 빠졌거나 sID 가 틀렸어도 호출 자체는 지나간다. 화면에 "조명 켜짐" 을 띄우려면
        /// 명령이 아니라 <b>리드백</b>을 봐야 한다.</para>
        /// </summary>
        ushort? ReadOperationMode();

        /// <summary>이 조명이 있어야 할 모드(드랍와처=2 Pulse, 글라스뷰=1 Continuous). 판정 기준값.</summary>
        ushort ExpectedRunMode { get; }
    }

    /// <summary>
    /// iCore 스트로브 Modbus/RTU 설정. 주소·스케일은 <b>장비 매뉴얼로 확인 후 교체</b>할 placeholder.
    /// (Meniscus 의 <see cref="Meniscus.DmdConfig"/> 와 같은 형식 — 같은 RS-485 계열이라 규약을 맞춘다)
    /// </summary>
    public sealed class StrobeConfig
    {
        public string ComPort  { get; set; } = "COM12";
        public int    BaudRate { get; set; } = 115200;
        public Parity Parity   { get; set; } = Parity.None;
        public int    DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public int    TimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 운전 모드 레지스터(0x300). 0=OFF, 1=Continuous(상시 점등), 2=Pulse(트리거 스트로브).
        /// 조명 ON/OFF 는 이 레지스터가 전부다 — 외부 트리거 없이 시리얼만으로 켜고 끌 수 있다.
        /// </summary>
        public ushort OperationRegister { get; set; } = 0x300;

        /// <summary>Trigger Delay 레지스터(0x314). LabVIEW <c>iCore_Set Delay time.vi</c> 대상.</summary>
        public ushort DelayRegister { get; set; } = 0x314;

        /// <summary>
        /// ★지연은 스케일 정수가 아니라 <b>IEEE-754 float32</b> 다(2026-08-05 실측 확정).
        /// Duration 이 [0,16672] → 0x41200000 → 10.0 으로, Period 는 10000.0 으로 정확히 떨어졌다.
        /// 정수로 쓰면 890 → 1.2e-42 가 되어 지연이 사실상 0 이 된다(이전 구현의 실패 원인).
        /// </summary>
        public bool DelayIsFloat32 { get; set; } = true;

        /// <summary>32bit 워드 순서. 실측 결과 <b>하위 워드 먼저</b>(false).</summary>
        public bool HighWordFirst { get; set; } = false;

        /// <summary>지연 변경 후 발광이 안정될 때까지의 대기[ms]. (LabVIEW SettleMs)</summary>
        public int SettleMs { get; set; } = 50;

        /// <summary>
        /// 카메라별 컨트롤러. 9호기는 <b>한 포트(COM12)에 2대</b>가 sID 로 구분돼 물려 있다.
        /// 포트는 한 프로세스에서 한 번만 열 수 있으므로 <see cref="IPulseBus"/> 로 공유한다.
        /// </summary>
        public List<StrobeDeviceConfig> Devices { get; set; } = new();

        /// <summary>카메라 ID 로 장비 설정을 찾는다. 없으면 목록의 첫 장비(단일 구성 호환).</summary>
        public StrobeDeviceConfig? DeviceFor(string? cameraId) =>
            Devices.FirstOrDefault(d => string.Equals(d.Camera, cameraId, StringComparison.OrdinalIgnoreCase))
            ?? Devices.FirstOrDefault();
    }

    /// <summary>한 대의 iPulse 컨트롤러 배정.</summary>
    public sealed class StrobeDeviceConfig
    {
        /// <summary>이 컨트롤러가 비추는 카메라의 CameraId (예: CAM_DW / CAM_GV).</summary>
        public string Camera { get; set; } = "";

        /// <summary>Modbus 슬레이브(sID) — 장비의 DIP-SW 로 정해진다.</summary>
        public byte UnitId { get; set; } = 1;

        /// <summary>
        /// 이 조명을 켤 때 쓸 운전 모드. 드랍와처는 토출 동기가 필요해 Pulse(2),
        /// 글라스뷰는 육안 조명이라 Continuous(1) 로 켠다.
        /// </summary>
        public ushort RunMode { get; set; } = 2;
    }

    // ---------- 구현 ----------

    /// <summary>
    /// COM 포트 하나를 여러 iPulse 컨트롤러가 공유하는 버스.
    ///
    /// 9호기는 COM12 한 포트에 2대(sID1/sID2)가 물려 있는데, <b>COM 포트는 한 프로세스에서
    /// 한 번만 열린다</b> — 컨트롤러마다 SerialPort 를 열면 두 번째가 실패한다.
    /// 그래서 포트별로 인스턴스를 공유하고 참조 수로 수명을 관리한다.
    /// 모든 트랜잭션은 락으로 직렬화한다(RS-485 반이중이라 겹치면 프레임이 깨진다).
    /// </summary>
    public sealed class IPulseBus : IDisposable
    {
        private static readonly Dictionary<string, IPulseBus> _shared = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _registry = new();

        private readonly object _io = new object();
        private readonly string _key;
        private SerialPort? _port;
        private IModbusSerialMaster? _master;
        private int _refCount;

        private IPulseBus(string key) => _key = key;

        /// <summary>포트별 공유 인스턴스를 얻는다(없으면 열고, 있으면 참조만 늘린다).</summary>
        public static IPulseBus Open(StrobeConfig cfg)
        {
            lock (_registry)
            {
                if (!_shared.TryGetValue(cfg.ComPort, out var bus))
                {
                    bus = new IPulseBus(cfg.ComPort);
                    _shared[cfg.ComPort] = bus;
                }
                bus.EnsureOpen(cfg);
                bus._refCount++;
                return bus;
            }
        }

        private void EnsureOpen(StrobeConfig cfg)
        {
            lock (_io)
            {
                if (_master != null) return;
                _port = new SerialPort(cfg.ComPort, cfg.BaudRate, cfg.Parity, cfg.DataBits, cfg.StopBits)
                {
                    ReadTimeout  = cfg.TimeoutMs,
                    WriteTimeout = cfg.TimeoutMs,
                };
                _port.Open();
                _master = new ModbusFactory().CreateRtuMaster(_port);
            }
        }

        public bool IsOpen => _master != null;

        public ushort[]? ReadHolding(byte unit, ushort addr, ushort count)
        {
            lock (_io)
            {
                if (_master == null) return null;
                try { return _master.ReadHoldingRegisters(unit, addr, count); }
                catch { return null; }
            }
        }

        /// <summary>
        /// 단일 레지스터 쓰기 + 리드백 검증.
        /// 실장 iCore 는 쓰기 응답 프레임이 비표준이라(2026-07-23 관측) NModbus CRC 검증에 걸린다 →
        /// 예외를 삼키고 <b>리드백으로 판정</b>한다. 반영되지 않았으면 false.
        /// </summary>
        public bool WriteSingleVerified(byte unit, ushort addr, ushort value)
        {
            lock (_io)
            {
                if (_master == null) return false;
                try { _master.WriteSingleRegister(unit, addr, value); }
                catch { try { _port?.DiscardInBuffer(); } catch { } }

                try { return _master.ReadHoldingRegisters(unit, addr, 1) is { Length: > 0 } r && r[0] == value; }
                catch { return false; }
            }
        }

        /// <summary>2워드 쓰기 + 리드백 검증(워드 순서는 호출자가 맞춰 넘긴다).</summary>
        public bool WritePairVerified(byte unit, ushort addr, ushort[] words)
        {
            lock (_io)
            {
                if (_master == null || words.Length != 2) return false;
                try { _master.WriteMultipleRegisters(unit, addr, words); }
                catch { try { _port?.DiscardInBuffer(); } catch { } }

                try
                {
                    var r = _master.ReadHoldingRegisters(unit, addr, 2);
                    return r.Length == 2 && r[0] == words[0] && r[1] == words[1];
                }
                catch { return false; }
            }
        }

        public void Dispose()
        {
            lock (_registry)
            {
                if (--_refCount > 0) return;    // 다른 컨트롤러가 아직 쓴다
                _shared.Remove(_key);
            }
            lock (_io)
            {
                _master?.Dispose();
                _master = null;
                if (_port != null && _port.IsOpen) _port.Close();
                _port?.Dispose();
                _port = null;
            }
        }
    }

    /// <summary>
    /// iCore iPulse LED 컨트롤러 한 대. (모델 1P1S-100A, Modbus RTU/FC3·FC6·FC16)
    ///
    /// 레지스터는 매뉴얼(rev03) + 실물 프로브(2026-08-05)로 확정:
    ///   Operation(0x300)   0=OFF / 1=Continuous / 2=Pulse   ← 조명 ON/OFF 는 이것뿐
    ///   Trigger Delay(0x314)  IEEE-754 float32, 하위 워드 먼저 [us]
    /// 밝기·정격전류(Rated Current)는 <b>건드리지 않는다</b> — 매뉴얼에 주소가 없고,
    /// 정격을 넘겨 쓰면 LED 가 파손된다. 그쪽은 iPulse Configurator 로 세팅하고 저장한다.
    /// </summary>
    public sealed class ICoreStrobe : IStrobeController
    {
        private readonly StrobeConfig _cfg;
        private readonly StrobeDeviceConfig _dev;
        private IPulseBus? _bus;

        /// <param name="cameraId">이 조명이 비추는 카메라(CAM_DW / CAM_GV). null 이면 목록의 첫 장비.</param>
        public ICoreStrobe(StrobeConfig? cfg = null, string? cameraId = null)
        {
            _cfg = cfg ?? new StrobeConfig();
            _dev = _cfg.DeviceFor(cameraId) ?? new StrobeDeviceConfig();
        }

        public bool   IsConnected           => _bus?.IsOpen == true;
        public double LastDelayMicroseconds { get; private set; } = double.NaN;

        /// <summary>이 컨트롤러의 sID — 로그·진단 표시용.</summary>
        public byte UnitId => _dev.UnitId;

        /// <summary>지연 변경 후 발광 안정 대기[ms]. 시퀀스가 이 값을 참조한다.</summary>
        public int SettleMs => _cfg.SettleMs;

        public void Init()
        {
            Close();
            _bus = IPulseBus.Open(_cfg);
        }


        /// <summary>
        /// 발광 지연[us] 설정 — Trigger Delay(0x314).
        /// ★값은 IEEE-754 float32(하위 워드 먼저)로 쓴다. 정수로 쓰면 890 → 1.2e-42 가 되어
        ///   지연이 사실상 0 이 된다(이전 구현이 그랬고, 그래서 액적 위상이 안 움직였다).
        /// </summary>
        public void SetDelayMicroseconds(double us)
        {
            if (us < 0) throw new ArgumentOutOfRangeException(nameof(us), "지연은 음수일 수 없습니다.");
            if (_bus == null) throw new InvalidOperationException("스트로브 미연결. Init() 먼저 호출.");

            ushort[] words = ToWords(us);
            if (!_bus.WritePairVerified(_dev.UnitId, _cfg.DelayRegister, words))
                throw new IOException(
                    $"지연 {us:F1}us 쓰기 실패(sID {_dev.UnitId}, 0x{_cfg.DelayRegister:X3}) — 리드백 불일치.");

            LastDelayMicroseconds = us;
        }

        /// <summary>us → 장비 워드 배열. float32(실측 확정) 또는 정수 폴백.</summary>
        private ushort[] ToWords(double us)
        {
            uint bits = _cfg.DelayIsFloat32
                ? unchecked((uint)BitConverter.SingleToInt32Bits((float)us))
                : (uint)Math.Round(us);

            ushort hi = (ushort)(bits >> 16), lo = (ushort)(bits & 0xFFFF);
            return _cfg.HighWordFirst ? new[] { hi, lo } : new[] { lo, hi };
        }

        /// <summary>
        /// 조명 ON/OFF — Operation(0x300). 켤 때는 이 장비의 <see cref="StrobeDeviceConfig.RunMode"/>
        /// (드랍와처=Pulse, 글라스뷰=Continuous), 끌 때는 0(OFF).
        /// ※ 컨트롤러의 LED Enable(채널) 이 꺼져 있으면 이 값을 바꿔도 불은 안 들어온다.
        ///   그건 Configurator 로만 설정 가능하다.
        /// </summary>
        public void Enable(bool on)
        {
            if (_bus == null) throw new InvalidOperationException("스트로브 미연결. Init() 먼저 호출.");

            ushort mode = on ? _dev.RunMode : (ushort)0;
            if (!_bus.WriteSingleVerified(_dev.UnitId, _cfg.OperationRegister, mode))
                throw new IOException(
                    $"조명 {(on ? "ON" : "OFF")} 실패(sID {_dev.UnitId}, 0x{_cfg.OperationRegister:X3}={mode}) — 리드백 불일치.");
        }

        /// <summary>현재 운전 모드(0=OFF, 1=Continuous, 2=Pulse). 읽기 실패 시 null.</summary>
        public ushort? ReadOperation()
        {
            var r = _bus?.ReadHolding(_dev.UnitId, _cfg.OperationRegister, 1);
            return r is { Length: > 0 } ? r[0] : null;
        }

        public ushort? ReadOperationMode() => ReadOperation();

        public ushort ExpectedRunMode => _dev.RunMode;

        /// <summary>
        /// 지연 레지스터 리드백(raw 비트). 커미셔닝 검증용 — 통신·주소가 맞는지 확인한다.
        /// float32 로 해석하려면 <see cref="ReadDelayMicroseconds"/> 를 쓸 것.
        /// </summary>
        public uint? TryReadDelayRaw()
        {
            var regs = _bus?.ReadHolding(_dev.UnitId, _cfg.DelayRegister, 2);
            if (regs == null || regs.Length < 2) return null;
            return _cfg.HighWordFirst
                ? ((uint)regs[0] << 16) | regs[1]
                : ((uint)regs[1] << 16) | regs[0];
        }

        /// <summary>지연 리드백을 us 로 해석. 실패 시 null.</summary>
        public double? ReadDelayMicroseconds()
        {
            uint? raw = TryReadDelayRaw();
            if (raw == null) return null;
            return _cfg.DelayIsFloat32
                ? BitConverter.Int32BitsToSingle(unchecked((int)raw.Value))
                : raw.Value;
        }

        public void Close()
        {
            _bus?.Dispose();
            _bus = null;
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

        /// <summary>가상은 마지막 적용값을 그대로 돌려준다(리드백 일치 시나리오 재현).</summary>
        public uint? TryReadDelayRaw() =>
            double.IsNaN(LastDelayMicroseconds) ? null : (uint)Math.Round(LastDelayMicroseconds);

        /// <summary>
        /// 가상은 Enable 한 대로 돌려준다 — 실물처럼 "명령과 리드백이 일치" 하는 정상 상태를 재현한다.
        /// 덕분에 화면의 조명 램프를 가상 모드에서 그대로 확인할 수 있다.
        /// </summary>
        public ushort? ReadOperationMode() => IsEnabled ? ExpectedRunMode : (ushort)0;

        /// <summary>가상 드랍와처는 실물과 같이 Pulse(2) 를 기준으로 둔다.</summary>
        public ushort ExpectedRunMode => 2;

        public void Dispose() => IsConnected = false;
    }
}
