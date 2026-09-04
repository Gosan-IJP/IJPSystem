using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace IJPSystem.Platform.Domain.Models.Vision
{
    public class VisionCameraRoot
    {
        [JsonPropertyName("VisionCameraList")]
        public List<CameraDeviceInfo> VisionCameraList { get; set; } = new();
    }

    public class CameraDeviceInfo
    {
        public string CameraId { get; set; } = string.Empty;

        /// <summary>
        /// ※ 하드웨어 식별자 — IMAQdx(NI-MAX) 카메라 이름으로 사용된다(ImaqdxVisionDriver.TryOpen).
        /// 화면 표시 목적으로 바꾸지 말 것. 표시명은 <see cref="DisplayName"/> 사용.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>화면 표시명(예: "Glass View"). 비어 있으면 <see cref="Name"/> 을 사용.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 이 카메라만 다른 드라이버로 붙일 때 지정한다(Ebus / Imaqdx / Hikrobot / Virtual).
        /// 비우면 AppConfig 의 <c>DriverMode.Vision</c> 을 따른다.
        ///
        /// <para>9호기처럼 벤더가 섞인 구성 때문에 필요하다 — 드랍와처(JAI)는 eBUS 로 정상이지만
        /// 글라스뷰(하이크로봇)는 eBUS for JAI 라이선스 대상이 아니라 워터마크가 찍힌다.
        /// 카메라마다 맞는 드라이버를 붙일 수 있어야 한다.</para>
        /// </summary>
        public string Driver { get; set; } = string.Empty;

        /// <summary>Visual Monitor 소스 목록 노출 여부. 전용 화면만 쓰는 카메라는 false.</summary>
        public bool ShowInMonitor { get; set; } = true;

        public string IpAddress { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;

        /// <summary>
        /// GigE 카메라 MAC 주소(구분자 무관: <c>00:0c:df:01:1d:b4</c> / <c>000cdf011db4</c>).
        /// eBUS 드라이버가 카메라를 찾을 때 <b>가장 먼저</b> 쓰는 식별자다.
        /// IP 는 DHCP/링크로컬(169.254.x.x)이면 전원마다 바뀌므로 MAC 이 유일하게 안정적이다.
        /// 비워두면 SerialNumber → IpAddress → Name 순으로 찾는다.
        /// </summary>
        public string MacAddress { get; set; } = string.Empty;

        /// <summary>
        /// 노출/게인 GenICam 노드명 강제 지정. 비우면 드라이버가 후보를 순서대로 탐색한다
        /// (노출: ExposureTime → ExposureTimeAbs → ExposureTimeRaw / 게인: Gain → GainAbs → GainRaw).
        /// 카메라 기종마다 노드명이 달라, 실장에서 이름만 다를 때 재빌드 없이 맞추기 위한 탈출구.
        /// </summary>
        public string ExposureNode { get; set; } = string.Empty;
        public string GainNode { get; set; } = string.Empty;

        /// <summary>
        /// 하드웨어 트리거 입력 라인(GenICam <c>TriggerSource</c>) — 예: <c>Line0</c>, <c>Line1</c>.
        ///
        /// <para><b>비어 있으면 하드웨어 트리거를 쓰지 않는다</b>(자유 실행). 이 값이 있어야만
        /// 드랍와처가 트리거 체인을 켤 때 카메라를 트리거 모드로 바꾼다.</para>
        ///
        /// <para>Line 번호는 <b>배선으로 정해진다</b> — NI 카운터 출력(PFI)이 카메라의 몇 번
        /// 입력 핀에 꽂혔는지에 달렸다. 그래서 코드에 박지 않고 여기 둔다. 나머지 노드
        /// (TriggerSelector/TriggerMode/TriggerActivation)는 GenICam 표준이라 기본값으로 맞는다.</para>
        /// </summary>
        public string TriggerSource { get; set; } = string.Empty;

        /// <summary>
        /// 트리거가 무엇을 개시할지. 표준값 <c>FrameStart</c> — 트리거 1회당 프레임 1장.
        /// <c>AcquisitionStart</c> 로 두면 첫 트리거에 연속 촬영이 시작돼 동기가 깨진다.
        /// </summary>
        public string TriggerSelector { get; set; } = "FrameStart";

        /// <summary>트리거 엣지. NI 체인의 <c>TriggerOnRisingEdge</c> 와 맞춰야 한다.</summary>
        public string TriggerActivation { get; set; } = "RisingEdge";
        public int PixelWidth { get; set; } = 1920;
        public int PixelHeight { get; set; } = 1080;

        /// <summary>
        /// 광학계 사양상 1픽셀 크기[µm]. 0 = 미입력.
        ///
        /// <para>정렬 교정(µm/px)이 이 값에서 크게 벗어나면 교정을 거부한다 — 축이 실제로
        /// 안 움직였거나 마크를 엉뚱하게 잡아도 그럴듯한 배율이 나오고, 그 배율로 만든
        /// 이동량이 그대로 모터로 가기 때문이다. 10호기 글라스 카메라 = 1.125µm/px.</para>
        /// </summary>
        public double NominalMicronPerPx { get; set; }

        /// <summary>
        /// 화면 가로 1픽셀(u)이 기계에서 가리키는 방향 — "+X" "-X" "+Y" "-Y". 비면 미입력.
        ///
        /// <para><b><see cref="NominalMicronPerPx"/> 는 크기만 말한다.</b> 화면 오른쪽이 기계 +X 인지
        /// -Y 인지는 카메라를 어느 쪽으로 돌려 달았는지가 정하고, 그건 사양서가 아니라 이 장비에만
        /// 있는 값이다. 반대로 잡으면 정렬 보정이 오차를 줄이는 대신 두 배로 키운다.</para>
        ///
        /// <para><b>확인법</b>: 글라스 화면에서 X 를 + 로 조금 조그하고 마크가 화면에서 어느 쪽으로
        /// 가는지 본다. 마크가 왼쪽으로 갔다면 화면 오른쪽(u+)은 기계 -X 다. 한 번 보면 끝나는
        /// 값이라, 이 두 줄만 맞으면 사양 µm/px 로 자동 정렬을 돌릴 수 있다(실측 교정은 그 뒤에
        /// 배율을 정밀하게 맞추는 일이다).</para>
        ///
        /// <para>틀리게 적어도 장비가 상하지는 않는다 — 보정 뒤 오차가 늘면 정렬이 그 자리에서
        /// 멈추고 이 값을 짚어 준다.</para>
        /// </summary>
        public string PixelUAxis { get; set; } = string.Empty;

        /// <summary>화면 세로 1픽셀(v)이 기계에서 가리키는 방향. <see cref="PixelUAxis"/> 와 같은 규칙.</summary>
        public string PixelVAxis { get; set; } = string.Empty;

        /// <summary>
        /// T 축의 + 가 도는 방향 — 이 카메라 화면에서 봤을 때 "CW"(시계) / "CCW"(반시계). 비면 미입력.
        ///
        /// <para>정렬이 내는 각도는 화면 좌표(오른쪽 +X, 위쪽 +Y)에서 잰 값이라 반시계가 + 다.
        /// T 축의 + 가 어느 쪽인지는 모터 배선이 정하므로 그 둘이 반대일 수 있고, 그러면
        /// 보정이 기울기를 <b>두 배로</b> 만든다.</para>
        ///
        /// <para><b>확인법</b>: 글라스 화면의 [Calibrate T] 가 실제로 재 준다 — T 를 조금 돌려
        /// 두 마크로 잰 각이 어느 쪽으로 움직이는지 본다. 눈으로 조그해 보는 것보다 확실하다.</para>
        ///
        /// <para><b>10호기는 CCW(반시계)다</b> — 2026-09-01 실측(시험 회전 +0.050° 에 잰 각
        /// −0.015° → +0.035°, 눈금비 1.006, 회전반경 140mm). 도면을 보고 CW 로 적어 두었던 것이
        /// 실제와 반대였다. <b>도면보다 실측을 믿을 것</b> — 이 값이 반대면 회전 보정이 기울기를
        /// 줄이는 대신 두 배로 만든다.</para>
        /// </summary>
        public string TAxisPositiveDir { get; set; } = string.Empty;
        /// <summary>
        /// 촬상 전 정착 대기 [ms]. 스테이지가 선 뒤 기구 진동이 잦아들 때까지 기다린다.
        ///
        /// <para>드라이브가 "안 움직인다"고 말하는 순간에도 기구는 아직 서고 있다. 그 사이에
        /// 찍으면 흔들린 사진이 나오고, 그 사진으로 낸 보정은 오차를 <b>키운다</b>.
        /// 현장에서 흔들림이 남으면 이 값을 올린다(설치 폴더 Config/VisionConfig.json).</para>
        /// </summary>
        public int SettleBeforeCaptureMs { get; set; } = 500;

        /// <summary>
        /// 촬상 직전에 <b>버릴</b> 프레임 수. 잔상(이동 중에 찍힌 옛 그림)을 없앤다.
        ///
        /// <para>카메라는 자유 실행으로 계속 찍어 대기열에 쌓는다. MVS 기본 전략은 오래된
        /// 것부터 꺼내 주므로, 이동이 끝난 직후에 한 장을 받으면 <b>이동 중에 찍힌 과거</b>가
        /// 나올 수 있다(<c>LatestImageOnly</c> 를 거부하는 펌웨어에서 특히). 몇 장을 버리고
        /// 찍으면 받은 그림이 정지 후의 것임이 보장된다. 0 = 버리지 않음.</para>
        ///
        /// <para>드라이버가 이미 대기열을 비우고 최신 한 장만 준다(<c>GrabLatest</c>). 그래도
        /// 남으면 — 카메라가 이동 중 프레임을 "최신"으로 들고 있는 경우 — 이 값을 올린다.</para>
        /// </summary>
        public int FlushFramesBeforeCapture { get; set; } = 1;

        public double DefaultExposureMs { get; set; } = 10.0;
        public double DefaultGain { get; set; } = 1.0;
        public int LightChannel { get; set; } = 0;
        public double VirtualFailRate { get; set; } = 0.05;  // 가상 드라이버 불량 발생률

        /* Virtual 모드에서만 사용하는 시뮬레이션 설정입니다.
         실제 장비에서는 카메라가 촬영 → 비전 SW가 검사 → PASS/NG 결과를 반환합니다.
         하지만 지금 코드는 실제 카메라 없이 VirtualVisionDriver가 검사 결과를 소프트웨어로 가짜 생성하는데, 이때 "몇 % 확률로 NG를 만들지"를 결정하는 값입니다.

         VirtualFailRate = 0.0   → 항상 PASS(디버깅용)
         VirtualFailRate = 0.05  → 5% 확률 NG(기본값)
         VirtualFailRate = 1.0   → 항상 NG(알람 팝업 테스트용)
         VirtualVisionDriver.InspectAsync 내부에서 이렇게 동작합니다:

         if (Random.NextDouble() < VirtualFailRate)
             return InspectionResult.Fail(...);  // NG
         else
             return InspectionResult.Pass(...);  // PASS
        */
    }
}
