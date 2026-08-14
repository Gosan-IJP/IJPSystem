using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Config
{
    /// <summary>
    /// <c>Recipes</c> 테이블에서 <b>레시피를 복사할 때 같이 따라와야 하는</b> 열 목록.
    ///
    /// <para>
    /// <b>왜 한 곳에 모으는가</b> — 열을 새로 만들면 세 곳을 고쳐야 한다: 마이그레이션, 저장 SQL,
    /// 그리고 <b>복사 SQL</b>. 앞 둘은 안 고치면 바로 터지지만 복사는 조용히 빠진다. 복사본이
    /// 그 항목만 비어 있는데 화면은 멀쩡히 열리고 저장도 되기 때문에, 한참 뒤 현장에서
    /// "복사했더니 칩 수·헤드명이 안 따라왔다" 로 발견된다(2026-08-13 실제 발생).
    /// </para>
    /// <para>
    /// 그래서 목록을 여기 두고 시험이 <b>마이그레이션 목록과 대조</b>한다 — 새 열을 만들고
    /// 여기 안 넣으면 시험이 먼저 알려 준다.
    /// </para>
    /// </summary>
    public static class RecipeColumns
    {
        /// <summary>
        /// 복사 대상이 <b>아닌</b> 열 — 새 레시피의 고유값이라 원본을 덮어쓰면 안 된다.
        /// </summary>
        public static readonly string[] NotCopied = { "Id", "Name", "SortOrder", "WaveformBasePath" };

        /// <summary>인쇄 조건.</summary>
        public static readonly string[] PrintSettings = { "PurgeTime", "Swath", "PrintDirection" };

        /// <summary>글라스 사양.</summary>
        public static readonly string[] GlassSpec =
        {
            "GlassWidthMm", "GlassHeightMm", "GlassThicknessMm", "GlassOriginXMm", "GlassOriginYMm",
        };

        /// <summary>
        /// 헤드 사양. 장비 공통이 아니라 <b>레시피에 딸린다</b> — 장비 하나로 여러 헤드를 갈아 쓴다.
        /// </summary>
        public static readonly string[] HeadSpec =
        {
            "HeadName", "HeadLength", "HeadWidthMm",
            "NozzlePitchUm", "NozzleRowPitchUm",
            "HeadChipCount", "NozzleRows", "HeadNozzlesPerRow",
            "HeadWaveform", "NozzleCount",
        };

        /// <summary>복사할 열 전부.</summary>
        public static IReadOnlyList<string> Copyable =>
            PrintSettings.Concat(GlassSpec).Concat(HeadSpec).ToArray();

        /// <summary>
        /// 복사용 <c>UPDATE … SET</c> 절을 만든다. 원본은 <c>@oldName</c>, 대상은 <c>@newId</c>.
        /// 열 이름은 이 클래스 안에서만 나오므로 SQL 주입 여지가 없다.
        /// </summary>
        public static string BuildCopySetClause()
            => string.Join(",\n", Copyable.Select(
                c => $"    {c} = (SELECT {c} FROM Recipes WHERE Name=@oldName)"));
    }
}
