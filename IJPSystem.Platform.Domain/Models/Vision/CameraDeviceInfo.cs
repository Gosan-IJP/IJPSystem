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
        public int PixelWidth { get; set; } = 1920;
        public int PixelHeight { get; set; } = 1080;
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
