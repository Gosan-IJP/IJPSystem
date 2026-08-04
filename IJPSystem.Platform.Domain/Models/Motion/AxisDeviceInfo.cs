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

        // 엔코더 분해능(논리단위 1 이동에 필요한 펄스 수 = SetUnitDist). null/0 이면 드라이브 기본값 사용.
        public double? EncoderPulsePerUnit { get; set; }

        // 이동 방향 반전. 기구/결선상 물리 이동 방향이 HMI 의 +/- 와 반대인 축을 소프트웨어에서 미러링한다.
        // true 면 지령(MoveAbs/MoveRel/Jog)과 읽어온 현재위치에 <b>모두</b> -1 을 곱한다.
        //   ※ 한쪽만 뒤집으면 절대이동이 목표에서 멀어지며 발산한다 — 반드시 쌍으로 처리할 것.
        //   ※ 원점복귀는 마스터/드라이브가 수행하므로 이 값의 영향을 받지 않는다(홈 방향 그대로).
        //   ※ 부호가 뒤집히므로 해당 축의 기존 티칭 좌표는 재티칭 필요.
        // 드라이브 파라미터(회전방향/Polarity)로 잡을 수 있으면 그쪽이 더 깔끔하다(피드백까지 드라이브에서 함께 반전).
        public bool InvertDirection { get; set; }

        // 원점복귀 속도 패턴. null이면 드라이브 기본값 사용(현행 동작).
        // 설정하면 Connect 시 드라이브에 다운로드(ecmHomeCfg_SetSpeedPatt) → 콜드부팅 후 첫 실행에도 정상.
        // LabVIEW Set Home Parameters.vi 와 동일하게 '속도 패턴만' 설정(모드/방향/오프셋은 미변경 → 안전).
        public HomeConfig? Home { get; set; }

        public MotionDetailConfig MotionConfig { get; set; } = new();
    }

    // 원점복귀 속도 패턴(JSON). LabVIEW 클러스터 순서 Vel/Acc/Dec/HomeSpecVel 에 대응.
    public class HomeConfig
    {
        public double Velocity { get; set; }       // 고속(탐색) 속도 — LabVIEW Vel
        public double Acceleration { get; set; }   // 가속 — Acc
        public double Deceleration { get; set; }   // 감속 — Dec
        public double SpecVelocity { get; set; }   // 저속(크립/근접) 속도 — HomeSpecVel

        // 원점복귀 탐색 방향. +1 = (+)방향, -1 = (-)방향. null 이면 미지정 = 종전대로 (-)방향.
        // 코미조아 유틸리티(Home Return 탭)의 Dir 버튼과 같은 값이며, ecmHomeMot_MoveStart 인자로 들어간다.
        //   ※ 축이 원점센서 반대편으로 달려 리밋을 때리므로 축마다 실물 확인 후 넣을 것.
        //   ※ InvertDirection(지령 미러링)의 영향을 받지 않는다 — 원점복귀는 드라이브가 수행하므로
        //      여기 값이 드라이브 기준 물리 방향 그대로 전달된다.
        public int? Direction { get; set; }
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