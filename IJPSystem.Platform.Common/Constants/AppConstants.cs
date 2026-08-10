namespace IJPSystem.Platform.Common.Constants
{
    /// <summary>애플리케이션 전역 상수</summary>
    public static class AppConstants
    {
        // ── 날짜/시간 포맷 ────────────────────────────────────────────────────
        public const string FmtTime          = "HH:mm:ss";
        public const string FmtTimeMs        = "HH:mm:ss.fff";
        public const string FmtDateTime      = "yyyy-MM-dd HH:mm:ss";
        public const string FmtDateTimeFile  = "yyyyMMdd_HHmmss";

        // ── 파일 경로 ─────────────────────────────────────────────────────────
        public const string ConfigFolder          = "Config";
        public const string LogFolder             = @"C:\Logs";
        public const string MotorConfigFile       = "MotorConfig.json";
        public const string IoConfigFile          = "IO.json";
        public const string VisionConfigFile      = "VisionConfig.json";
        public const string AlarmSystemDb         = "AlarmSystem.db";
        public const string AlarmHistoryDb        = "AlarmHistory.db";
        public const string SystemLogDb           = "SystemLog.db";

        // ── 로그/실행 제한 ────────────────────────────────────────────────────
        public const int MaxExecutionLogCount = 50;   // 시퀀스 실행 로그 최대 보관 수
        public const int MaxMainLogCount      = 500;  // 메인 로그 최대 보관 수

        // ── 타이머 주기 (ms) ──────────────────────────────────────────────────
        public const int TimerIntervalFastMs  = 50;   // 빠른 갱신 (모션 시뮬레이션, 애니메이션)
        public const int TimerIntervalSlowMs  = 500;  // 느린 갱신 (시스템 시간, I/O 상태)

        // ── 프린트 헤드 ───────────────────────────────────────────────────────
        // 노즐 번호 규약: 화면·레시피·파서 모두 1번부터 센다(0번 없음).
        // 패턴 배열은 0부터이므로 변환은 SpitPatternBuilder.FirstNozzleIndex 가 담당한다.
        // ※ 노즐 수는 <b>HeadSpec</b>(Infrastructure.Config)에서 읽는다 — 장비 설정에 값이 있으면
        //   그것을, 없으면 아래 기본값을 쓴다. 여기 상수를 직접 보지 말 것.
        //   이 값이 실제보다 작으면 뒤쪽 노즐 선택이 무시되고, 크면 없는 노즐에 패턴이 잡힌다.
        //   (Common 은 Infrastructure 를 참조하지 않으므로 기본값만 여기 둔다)
        public const int FirstNozzleNumber = 1;

        /// <summary>헤드 노즐 수 <b>기본값</b>. 실제 값은 HeadSpec.Count 로 읽을 것. S800 = 2열 × 400.</summary>
        public const int HeadNozzleCount   = 800;

        // ── 모션 ──────────────────────────────────────────────────────────────
        public const int MotionPollIntervalMs    = 100;  // 축 상태 폴링 주기
        public const int MotionInPositionTimeout = 200;  // InPosition 대기 최대 횟수 (× 100ms = 20s)
        public const double MaxJogVelocity       = 5000; // 조그 최대 속도 (pulse/s)
    }
}
