namespace IJPSystem.Drivers.Meniscus
{
    /// <summary>
    /// DMD(잉크 압력) 모듈의 Modbus 레지스터 맵.
    /// 원본 LabVIEW "DMD_Read Pressure.vi"가 Read Holding Registers(FC03)로
    /// 압력 레지스터를 읽는 것에 대응한다.
    ///
    /// ※ 아래 주소/스케일은 placeholder 다. 실제 값은 모듈 통신 매뉴얼 또는
    ///   LabVIEW VI 프론트패널의 상수로 반드시 교체할 것.
    /// </summary>
    public static class DmdRegisterMap
    {
        /// <summary>Modbus 슬레이브(Unit) ID. 원본의 "Write Unit ID.vi"에 대응.</summary>
        public const byte DefaultUnitId = 1;

        // ---- 읽기(홀딩 레지스터, FC03) ----

        /// <summary>현재 메니스커스 압력 측정값 레지스터 시작 주소.</summary>
        public const ushort PressureReadAddress = 0x0000;

        /// <summary>압력 값이 차지하는 레지스터 수 (1=16bit, 2=32bit float/long).</summary>
        public const ushort PressureReadCount = 1;

        /// <summary>모듈 상태 레지스터(가동/에러 등). 필요 시 사용.</summary>
        public const ushort StatusReadAddress = 0x0010;

        // ---- 쓰기(단일/다중 레지스터, FC06/FC16) ----

        /// <summary>메니스커스 압력 목표값(setpoint) 레지스터 주소.</summary>
        public const ushort PressureSetpointAddress = 0x0100;

        /// <summary>압력 제어 on/off 등 제어 비트 레지스터.</summary>
        public const ushort ControlAddress = 0x0101;

        // ---- 스케일링 ----
        // 레지스터 정수값 → 실제 압력[kPa] 변환 계수.
        // 예) 레지스터가 0.1 kPa 단위 정수면 Scale = 0.1.
        //     32bit float 를 쓰면 Scale 무시하고 float 변환 사용.

        /// <summary>레지스터 정수 → 압력[kPa] 스케일.</summary>
        public const double PressureScale = 0.1;

        /// <summary>오프셋[kPa]. 부호이면 음압 영역도 표현.</summary>
        public const double PressureOffset = 0.0;
    }

    /// <summary>압력 레지스터 데이터 타입.</summary>
    public enum PressureDataType
    {
        /// <summary>16bit 정수 × Scale + Offset.</summary>
        Int16Scaled,
        /// <summary>32bit 부호정수 × Scale + Offset (레지스터 2개).</summary>
        Int32Scaled,
        /// <summary>32bit IEEE float (레지스터 2개).</summary>
        Float32
    }
}
