using IJPSystem.Platform.Domain.Models.Motion;
using System.Collections.Generic;
using System.Text.Json.Serialization;

// AxisDeviceInfo
// : 축의 고정 설정 정보를 담는 객체 (JSON에서 로드)
//   - 이름, 조그 속도 등 런타임 중 변경되지 않는 값
//   - 참조 구조: AxisDeviceInfo → MotionDetailConfig → Profile

namespace IJPSystem.Platform.Domain.Models.Motion
{
    // 1. 최상위 루트: JSON의 "MotionAxisList" 배열을 담음
    public class MotionAxisRoot
    {
        [JsonPropertyName("MotionAxisList")]
        public List<AxisDeviceInfo> MotionAxisList { get; set; } = new();
    }

    // 2. 개별 축 정보
    public class AxisDeviceInfo
    {
        public string AxisNo { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = "mm";

        // 물리 하드웨어 축 번호(0-base). null이면 MotionAxisList 나열 순서를 그대로 사용.
        // 배선상 논리축(X/Y/Z)이 다른 하드웨어 축에 물린 경우(예: X↔Y 뒤바뀜) 여기서 교정한다.
        public int? HwAxis { get; set; }

        public MotionDetailConfig MotionConfig { get; set; } = new();
    }

    // 3. 축별 상세 구동 설정 (계층 구조의 핵심)
    public class MotionDetailConfig
    {
        public Profile Move { get; set; } = new();
        public Profile Jog { get; set; } = new();
        public Profile Printing { get; set; } = new();
    }

    // 4. 속도/가감속 세부 수치
    public class Profile
    {
        public double Velocity { get; set; }
        public double Acceleration { get; set; }
        public double Deceleration { get; set; }
    }

    // 5. Move 명령 시 사용할 프로파일 종류
    //    Move    : 일반 이동 (포인트 이동 등)
    //    Jog     : 수동 조그
    //    Printing: 인쇄(잉크 토출) 구간 이동 — AutoPrint step 5에서 사용
    public enum MotionProfileKind
    {
        Move,
        Jog,
        Printing,
    }
}