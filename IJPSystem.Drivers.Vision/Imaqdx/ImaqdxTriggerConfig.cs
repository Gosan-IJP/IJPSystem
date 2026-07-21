namespace IJPSystem.Drivers.Vision.Imaqdx
{
    /// <summary>
    /// 카메라 하드웨어 트리거 설정(GenICam 속성 경로 + 값).
    ///
    /// <b>경로를 상수로 박지 않고 설정으로 뺀 이유</b>: IMAQdx 의 <c>CameraAttributes::</c> 트리는
    /// IMAQdx 가 정하는 게 아니라 <b>카메라의 GenICam XML 에서 런타임에 생성</b>된다. 카테고리 이름
    /// (AcquisitionControl / AcquisitionTrigger / TriggerControl …)은 카메라 제조사가 정하므로
    /// 기종은 물론 펌웨어 리비전마다 다를 수 있다. 아래 기본값은 GenICam SFNC 표준 명칭 기준의
    /// <b>추정값</b>이며, 실장 카메라에서 반드시 확인해야 한다.
    ///
    /// 확인 방법: NI MAX 에서 해당 카메라의 Camera Attributes 트리를 열어 실제 경로를 읽는다.
    /// (또는 IMAQdxEnumerateAttributes2 로 "CameraAttributes" 하위를 열거)
    /// 경로가 틀리면 설정 호출이 실패하고, 그 경로가 로그에 그대로 찍히도록 해뒀다.
    /// </summary>
    public sealed class ImaqdxTriggerConfig
    {
        /// <summary>false 면 하드웨어 트리거 설정을 건너뛴다(자유 촬영).</summary>
        public bool Enabled { get; set; } = true;

        // ※ 설정 순서가 중요하다 — TriggerSelector 는 GenICam 셀렉터라 뒤따르는
        //   Mode/Source/Activation 의 적용 대상을 결정한다. 먼저 설정하지 않으면
        //   엉뚱한 트리거가 구성되거나 실패한다.
        public string SelectorPath   { get; set; } = "CameraAttributes::AcquisitionControl::TriggerSelector";
        public string SelectorValue  { get; set; } = "FrameStart";

        public string ModePath       { get; set; } = "CameraAttributes::AcquisitionControl::TriggerMode";
        public string ModeValue      { get; set; } = "On";

        public string SourcePath     { get; set; } = "CameraAttributes::AcquisitionControl::TriggerSource";
        /// <summary>카메라의 트리거 입력 라인. DAQ Cam 펄스가 결선된 라인으로 맞출 것.</summary>
        public string SourceValue    { get; set; } = "Line0";

        public string ActivationPath { get; set; } = "CameraAttributes::AcquisitionControl::TriggerActivation";
        public string ActivationValue{ get; set; } = "RisingEdge";

        /// <summary>
        /// 획득 타임아웃[ms]. 이 시간 안에 트리거가 안 오면 GetImageData 가 타임아웃으로 돌아온다.
        /// 세션은 유효하게 유지되므로 정상적인 재시도 대상이지 오류가 아니다.
        /// 토출 주파수가 낮으면 넉넉히 잡을 것(기본 5000ms 는 IMAQdx 기본값과 동일).
        /// </summary>
        public uint TimeoutMs { get; set; } = 5000;

        /// <summary>링버퍼 장수. 3~8 이면 충분하다.</summary>
        public uint BufferCount { get; set; } = 5;
    }
}
