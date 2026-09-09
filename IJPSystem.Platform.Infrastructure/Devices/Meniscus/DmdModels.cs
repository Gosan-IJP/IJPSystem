using System;
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

        /// <summary>
        /// 장비가 <b>실제로</b> RUN 인가(주소 50 리드백). 명령을 넣었는지가 아니다.
        ///
        /// <para>화면 램프가 명령값을 그대로 비추면, RUN 을 눌렀는데 장비는 STOP 인 상태가
        /// 초록불로 보인다. 압력이 걸린 줄 알고 다음 단계로 넘어가게 되므로 위험하다
        /// (2026-09-08 11호기에서 실제로 RUN 명령이 먹지 않았다).</para>
        /// </summary>
        public bool Running { get; set; }
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

        // --- 레지스터 맵 (랩뷰 DMD Control and State.vi 에서 확인, 2026-09-08) ---
        //
        // ★읽기와 쓰기가 <b>다른 주소</b>다. 그리고 쓰기 주소 0·1 은 <b>write-only</b> 라
        //   읽으면 Illegal Data Address 가 난다 — 읽기로 훑는 스캐너에는 아예 안 보인다.
        //   0~1000 을 다 훑고도 50·51·52 만 나왔던 이유가 이것이다(2026-09-07).
        //
        //   읽기(FC03/04)  50 = RUN/STOP 상태, 51 = PV 현재압력, 52 = SV 되읽기
        //   쓰기(FC06)      0 = RUN/STOP 명령,  1 = SV 목표압력

        /// <summary>현재 압력(PV) 읽기 주소. 읽기 전용.</summary>
        public ushort PressureReadAddress { get; set; } = 51;
        /// <summary>목표 압력(SV) 쓰기 주소. <b>쓰기 전용</b> — 되읽으려면 <see cref="SetpointReadAddress"/>.</summary>
        public ushort PressureSetAddress { get; set; } = 1;
        /// <summary>RUN/STOP 명령 주소(1=RUN, 0=STOP). <b>쓰기 전용</b>.</summary>
        public ushort ControlAddress { get; set; } = 0;

        /// <summary>RUN/STOP <b>상태</b> 읽기 주소(1=RUN, 0=STOP). 명령을 넣었다고 도는 것은 아니다.</summary>
        public ushort RunStateAddress { get; set; } = 50;
        /// <summary>SV 되읽기 주소. 쓴 목표값이 실제로 들어갔는지 확인하는 유일한 길이다.</summary>
        public ushort SetpointReadAddress { get; set; } = 52;

        /// <summary>
        /// 레지스터 정수 → 압력[kPa]. 클라이언트 바깥 규약이 kPa 이므로 여기서 kPa 로 맞춘다.
        ///
        /// <para>장비 레지스터는 0.1Pa 단위다(PV 는 실제값의 10배, SV 도 10배로 써 넣는다).
        /// 0.1Pa = 0.0001kPa 이라 스케일은 <b>0.0001</b>. 예전 값 0.1 은 결과를 1000배로 부풀려
        /// 13.8Pa 를 13,800Pa 로 보이게 했다(2026-09-08 11호기).</para>
        /// </summary>
        public double PressureScale { get; set; } = 0.0001;
        /// <summary>압력 오프셋(부호 표현용).</summary>
        public double PressureOffset { get; set; } = 0.0;

        // ── 단위 변환 ────────────────────────────────────────────────────
        // 통신에서 떼어 둔다. 스케일 하나 틀리면 13.8Pa 가 13,800Pa 로 보이는데,
        // 그것을 확인하자고 장비를 붙일 수는 없다(2026-09-08 실제로 그랬다).

        /// <summary>레지스터 값 → 압력[kPa]. 음압이 있으므로 부호 있는 정수로 읽는다.</summary>
        public double ToPressure(ushort raw) => (short)raw * PressureScale + PressureOffset;

        /// <summary>압력[kPa] → 레지스터 값.</summary>
        public ushort ToRaw(double pressureKpa)
            => (ushort)Math.Round((pressureKpa - PressureOffset) / PressureScale);
    }
}
