using System.Collections.Generic;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>사용 가능한 모든 시퀀스 목록을 제공한다.</summary>
    /// <remarks>
    /// NameKey/DescriptionKey 는 번역 키 (Seq_*_Name, Seq_*_Desc).
    /// HMI 의 SequenceVM 이 ctor 에서 Loc.T 로 번역해 Name/Description 에 채워 넣음.
    /// </remarks>
    public static class SequenceRegistry
    {
        public static IReadOnlyList<SequenceDefinition> GetAll() => new[]
        {
            new SequenceDefinition
            {
                Id             = "INIT",
                Icon           = "🏠",
                NameKey        = "Seq_Init_Name",
                DescriptionKey = "Seq_Init_Desc",
                BuildSteps     = InitializeSequence.Build,
            },
            new SequenceDefinition
            {
                Id             = "PURGE",
                Icon           = "💧",
                NameKey        = "Seq_Purge_Name",
                DescriptionKey = "Seq_Purge_Desc",
                // 압력 SV 는 호출자(PnidViewModel)가 람다 래퍼로 주입. 여기는 Registry 메타데이터용 디폴트.
                BuildSteps     = (m, mo) => PurgeSequence.Build(m, mo),
            },
            new SequenceDefinition
            {
                Id             = "AUTO_PRINT",
                Icon           = "🖨",
                NameKey        = "Seq_AutoPrint_Name",
                DescriptionKey = "Seq_AutoPrint_Desc",
                // Registry 기본값은 swath=1(현행 1패스). 대시보드 오토런은 활성 레시피 값으로 별도 생성.
                BuildSteps     = (m, mo) => AutoPrintSequence.Build(m, mo),
            },
            new SequenceDefinition
            {
                Id             = "PATTERN_PRINT",
                Icon           = "🟦",
                NameKey        = "Seq_PatternPrint_Name",
                DescriptionKey = "Seq_PatternPrint_Desc",
                BuildSteps     = PatternPrintSequence.Build,
            },
            new SequenceDefinition
            {
                Id             = "GLASS_ALIGN",
                Icon           = "✛",
                NameKey        = "Seq_GlassAlign_Name",
                DescriptionKey = "Seq_GlassAlign_Desc",
                // 정렬 서비스는 HMI 가 꽂는다(GlassAlignServices) — 안 꽂혔으면 첫 단계가 그 사실을 말한다.
                BuildSteps     = GlassAlignSequence.Build,
            },
            new SequenceDefinition
            {
                Id             = "DROP_WATCHER",
                Icon           = "🔬",
                NameKey        = "Seq_DropWatcher_Name",
                DescriptionKey = "Seq_DropWatcher_Desc",
                BuildSteps     = DropWatcherSequence.Build,
            },
            new SequenceDefinition
            {
                Id             = "HEAD_UP",
                Icon           = "⬆",
                NameKey        = "Seq_HeadUp_Name",
                DescriptionKey = "Seq_HeadUp_Desc",
                BuildSteps     = HeadUpSequence.Build,
            },
            new SequenceDefinition
            {
                Id             = "HEAD_DOWN",
                Icon           = "⬇",
                NameKey        = "Seq_HeadDown_Name",
                DescriptionKey = "Seq_HeadDown_Desc",
                BuildSteps     = HeadDownSequence.Build,
            },
        };
    }
}
