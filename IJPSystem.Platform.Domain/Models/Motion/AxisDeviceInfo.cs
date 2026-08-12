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

        // 상한(+EL)/하한(-EL) 하드리밋 센서 배선이 반대로 물린 축을 교정한다.
        //   ※ 표시(LED)만 바뀐다. 실제 정지는 드라이브가 EL 입력으로 직접 걸므로 이 값과 무관하다.
        //   ※ 방향 반전 때문에 바꿔야 하는 경우는 여기 넣지 말 것 — 그건 자동이다(SwapLimitDisplay 참고).
        public bool SwapLimitSensors { get; set; }

        /// <summary>
        /// 리밋 상태를 <b>화면 좌표</b>로 보고할 때 상/하한을 바꿔야 하는가.
        ///
        /// <para>
        /// <see cref="InvertDirection"/> 은 좌표계를 통째로 뒤집는다. 그러면 하드웨어 −EL 이 붙어 있는
        /// 기구 끝이 화면에서는 <b>+ 끝</b>이 된다. 리밋은 "그 방향으로 더 못 간다"는 뜻이므로,
        /// 좌표를 뒤집으면 리밋 이름도 <b>반드시</b> 같이 뒤집혀야 한다. 선택 사항이 아니다.
        /// </para>
        /// <para>
        /// 예전에는 이걸 <see cref="SwapLimitSensors"/> 로 손수 맞추게 해 뒀는데, 한쪽만 설정하면
        /// 화면에 "(−) 리밋"이라고 뜬 곳에서 (+) 조그가 막히는 상태가 된다 — 리밋에서 빠져나올
        /// 방향을 화면이 반대로 알려주는 셈이다(T축, 2026-08-12).
        /// 그래서 방향 반전은 자동으로 반영하고, <see cref="SwapLimitSensors"/> 는 <b>배선이 진짜로
        /// 뒤바뀐 축</b>에만 쓰는 별개의 교정으로 남긴다. 둘 다 필요하면 서로 상쇄된다.
        /// </para>
        /// </summary>
        public bool SwapLimitDisplay => InvertDirection ^ SwapLimitSensors;

        // 티칭 좌표로 '저장'할 수 있는 값의 범위. null 이면 제한 없음(현행 동작).
        //   ※ 이동은 막지 않는다. 조그·수동이동은 어디든 갈 수 있어야 정비·복구가 된다.
        //      공정 좌표로 굳어지는 순간(레시피 저장)만 막는 것이 이 값의 목적이다(예: T축 0~30°).
        //   ※ 단위·부호는 화면에 보이는 좌표 그대로다(InvertDirection 적용 후).
        public TeachLimitConfig? TeachLimit { get; set; }

        /// <summary>그 값이 티칭 저장 범위 안인가. 미설정이면 항상 true.</summary>
        public bool IsWithinTeachLimit(double pos)
        {
            var l = TeachLimit;
            if (l == null) return true;
            if (l.Min is double lo && pos < lo) return false;
            if (l.Max is double hi && pos > hi) return false;
            return true;
        }

        /// <summary>티칭 저장 범위 표기 — 로그·안내문에 쓴다. 미설정이면 빈 문자열.</summary>
        public string TeachLimitText =>
            TeachLimit == null ? "" : $"{TeachLimit.Min?.ToString("0.###") ?? "-∞"}~{TeachLimit.Max?.ToString("0.###") ?? "+∞"}{Unit}";

        // 원점복귀 속도 패턴. null이면 드라이브 기본값 사용(현행 동작).
        // 설정하면 Connect 시 드라이브에 다운로드(ecmHomeCfg_SetSpeedPatt) → 콜드부팅 후 첫 실행에도 정상.
        // LabVIEW Set Home Parameters.vi 와 동일하게 '속도 패턴만' 설정(모드/방향/오프셋은 미변경 → 안전).
        public HomeConfig? Home { get; set; }

        public MotionDetailConfig MotionConfig { get; set; } = new();
    }

    // 티칭 저장 범위(JSON). 한쪽만 넣어도 된다 — 넣지 않은 쪽은 제한 없음.
    public class TeachLimitConfig
    {
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    // 원점복귀 속도 패턴(JSON). LabVIEW 클러스터 순서 Vel/Acc/Dec/HomeSpecVel 에 대응.
    public class HomeConfig
    {
        public double Velocity { get; set; }       // 고속(탐색) 속도 — LabVIEW Vel
        public double Acceleration { get; set; }   // 가속 — Acc
        public double Deceleration { get; set; }   // 감속 — Dec
        public double SpecVelocity { get; set; }   // 저속(크립/근접) 속도 — HomeSpecVel

        /// <summary>
        /// 절대 엔코더 축인가. true 면 원점복귀가 <b>0 으로 절대이동</b>이 된다.
        ///
        /// <para>
        /// 절대 엔코더는 전원을 넣은 순간 자기 위치를 안다 — 찾아갈 원점이 없다. 그런데도 홈 서치를
        /// 시키면 원점센서를 찾아 달리다 리밋을 때리고, 설령 성공해도 그 뒤의 위치 0 재정의가
        /// <b>절대 기준을 홈 센서 자리로 덮어쓴다</b>. 매 초기화마다 좌표계가 조금씩 옮겨간다.
        /// (T축 = 앱솔루트 DD 모터, 2026-08-12)
        /// </para>
        /// <para>
        /// 이동 속도는 <see cref="HomeConfig.Velocity"/> 를 그대로 쓴다 — 초기화 이동은 느려야 한다.
        /// </para>
        /// </summary>
        public bool Absolute { get; set; }

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