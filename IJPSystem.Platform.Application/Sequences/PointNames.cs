namespace IJPSystem.Platform.Application.Sequences
{
    // 시스템에서 사용되는 모든 티칭 포인트 이름의 단일 진실의 원천(Single Source of Truth).
    // - 시퀀스 코드는 이 상수만 참조 (`PointNames.PrintOrigin` 등)
    // - 신규 레시피 초기 포인트 행 생성 시에도 All을 그대로 사용
    // - 추가/이름변경/삭제 시 이 파일만 수정하면 시퀀스/UI/DB 매핑이 일관됨
    public static class PointNames
    {
        public const string Ready          = "READY";
        /// <summary>글라스 얼라인 위치 — 정렬 카메라(GVC)로 글라스 기준을 잡는 자리.</summary>
        public const string GlassAlign     = "GLASS ALIGN";
        public const string Load           = "LOAD";
        public const string Unload         = "UNLOAD";
        public const string Purge          = "PURGE";
        public const string Blotting       = "BLOTTING";
        public const string PrintOrigin    = "PRINT ORIGIN";
        public const string PrintEnd       = "PRINT END";
        public const string Maintenance    = "MT";
        public const string NJI            = "NJI";
        public const string DropWatcher    = "DROP WATCHER";
        public const string PrintHeadUp    = "PRINT HEAD UP";
        public const string PrintHeadDown  = "PRINT HEAD DOWN";

        // Pulse 장비에서는 Load / Unload / Blotting / NJI / Maintenance(MT) 를 티칭 포인트에서 제외(상수는 시퀀스 호환을 위해 유지).
        // (글라스 로드/언로드는 수동이라 전용 티칭 위치가 필요 없음)
        public static IReadOnlyList<string> All { get; } = new[]
        {
            Ready, GlassAlign, Purge,
            PrintOrigin, PrintEnd, DropWatcher,
            PrintHeadUp, PrintHeadDown,
        };
    }
}
