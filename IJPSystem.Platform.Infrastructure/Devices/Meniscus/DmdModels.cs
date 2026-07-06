using System.IO.Ports;

namespace IJPSystem.Platform.Infrastructure.Devices.Meniscus
{
    /// <summary>
    /// LabVIEW "DMD Work State.ctl" enum 대응. 메니스커스(DMD) 동작 상태.
    /// </summary>
    public enum DmdWorkState
    {
        Init,   // DMD Init  : Modbus 시리얼 마스터 생성·연결
        Read,   // DMD Read  : 압력 주기 읽기
        Set,    // DMD Set   : 목표 압력 설정
        Stop    // DMD Stop  : 정지/해제
    }

    /// <summary>
    /// LabVIEW "DMD State.ctl" 클러스터 대응. (Pressure / DMD Value / Con / Err)
    /// </summary>
    public sealed class DmdState
    {
        /// <summary>현재 메니스커스 압력값. (Pressure)</summary>
        public double Pressure { get; set; }
        /// <summary>설정/제어 값. (DMD Value)</summary>
        public double Value { get; set; }
        /// <summary>연결 상태. (Con)</summary>
        public bool Connected { get; set; }
        /// <summary>에러 상태. (Err)</summary>
        public bool HasError { get; set; }
        /// <summary>마지막 에러 메시지.</summary>
        public string ErrorMessage { get; set; } = "";
        /// <summary>현재 상태머신 상태.</summary>
        public DmdWorkState WorkState { get; set; } = DmdWorkState.Stop;
    }

    /// <summary>
    /// DMD 모듈 Modbus 레지스터 맵 + 시리얼 설정.
    /// ※ 주소/스케일/COM 설정은 placeholder. DMD_Read Pressure.vi 및 모듈 매뉴얼로 교체.
    /// </summary>
    public sealed class DmdConfig
    {
        // --- 시리얼(VISA INSTR / COM) 설정 : Create Serial Master.vi 입력 대응 ---
        public string ComPort { get; set; } = "COM3";
        public int BaudRate { get; set; } = 9600;
        public Parity Parity { get; set; } = Parity.None;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public int TimeoutMs { get; set; } = 1000;

        /// <summary>Modbus 슬레이브(Unit) ID. (Write Unit ID.vi 대응)</summary>
        public byte UnitId { get; set; } = 1;

        // --- 레지스터 맵 ---
        /// <summary>압력 읽기 홀딩 레지스터 주소. (Read Holding Registers)</summary>
        public ushort PressureReadAddress { get; set; } = 0x0000;
        /// <summary>압력 목표값(setpoint) 레지스터 주소.</summary>
        public ushort PressureSetAddress { get; set; } = 0x0100;
        /// <summary>압력 제어 on/off 레지스터 주소.</summary>
        public ushort ControlAddress { get; set; } = 0x0101;

        /// <summary>레지스터 정수 → 실제 압력 스케일(예: 0.1 kPa 단위).</summary>
        public double PressureScale { get; set; } = 0.1;
        /// <summary>압력 오프셋(부호 표현용).</summary>
        public double PressureOffset { get; set; } = 0.0;
    }
}
