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
