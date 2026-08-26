using System;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Config;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 레시피 복사 대상 열 검증.
    ///
    /// <para><b>이 시험이 막는 것</b>: 열을 새로 만들고 복사 목록에 넣는 것을 빠뜨리는 일.
    /// 복사본이 그 항목만 비어 있는데 화면은 멀쩡히 열리고 저장도 돼서, 한참 뒤 현장에서
    /// "복사했더니 칩 수·헤드명이 안 따라왔다" 로 발견된다(2026-08-13 실제 발생).</para>
    /// </summary>
    public class RecipeColumnsTests
    {
        [Fact]
        public void 헤드_사양이_복사_대상에_들어_있다()
        {
            // 실제로 빠져 있던 것들 — 칩 수와 헤드명이 대표적이다.
            Assert.Contains("HeadChipCount",     RecipeColumns.Copyable);
            Assert.Contains("HeadName",          RecipeColumns.Copyable);
            Assert.Contains("HeadWidthMm",       RecipeColumns.Copyable);
            Assert.Contains("HeadNozzlesPerRow", RecipeColumns.Copyable);
            Assert.Contains("HeadWaveform",      RecipeColumns.Copyable);
            Assert.Contains("NozzleRows",        RecipeColumns.Copyable);
            Assert.Contains("NozzleCount",       RecipeColumns.Copyable);
        }

        [Fact]
        public void 글라스_사양과_인쇄_조건도_들어_있다()
        {
            Assert.Contains("GlassWidthMm",   RecipeColumns.Copyable);
            Assert.Contains("GlassOriginYMm", RecipeColumns.Copyable);
            Assert.Contains("Swath",          RecipeColumns.Copyable);
            Assert.Contains("PrintDirection", RecipeColumns.Copyable);
            Assert.Contains("PurgeTime",      RecipeColumns.Copyable);
            // 자동 정렬 사용 여부도 품종이 정한다 — 복사본이 이것만 빠지면 마크 없는 글라스에서 멈춘다.
            Assert.Contains("AutoAlign",      RecipeColumns.Copyable);
        }

        [Fact]
        public void 레시피_고유값은_복사하지_않는다()
        {
            // Id·Name 을 덮어쓰면 복사본이 원본을 밀어낸다.
            foreach (string keep in RecipeColumns.NotCopied)
                Assert.DoesNotContain(keep, RecipeColumns.Copyable);
        }

        [Fact]
        public void 복사_목록에_중복이_없다()
        {
            // 같은 열이 두 번 들어가면 UPDATE 가 문법 오류로 통째로 실패한다.
            var all = RecipeColumns.Copyable;
            Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void SET_절이_모든_열을_원본에서_읽어_온다()
        {
            string sql = RecipeColumns.BuildCopySetClause();

            foreach (string c in RecipeColumns.Copyable)
                Assert.Contains($"{c} = (SELECT {c} FROM Recipes WHERE Name=@oldName)", sql);

            // 열 개수만큼 대입이 있어야 한다(쉼표로 이어 붙이므로 개수-1 개의 쉼표).
            Assert.Equal(RecipeColumns.Copyable.Count - 1, sql.Count(ch => ch == ','));
        }

        [Fact]
        public void SET_절에_대상_열_이름_말고는_들어가지_않는다()
        {
            // 열 이름은 이 클래스 안에서만 나오므로 주입 여지가 없다는 것을 고정해 둔다.
            string sql = RecipeColumns.BuildCopySetClause();
            Assert.DoesNotContain(";", sql);
            Assert.DoesNotContain("--", sql);
        }
    }
}
