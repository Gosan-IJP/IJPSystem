using System.Linq;
using IJPSystem.Platform.Infrastructure.Print;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 노즐 선택 화면의 칩·열 단위 빠른 선택 검증.
    ///
    /// <para>여기서 한 칸만 어긋나면 "칩3 을 껐는데 칩2 가 꺼지는" 식으로 조용히 틀린다 —
    /// 3,200개짜리 막대에서는 눈으로 못 잡는다.</para>
    /// </summary>
    public class NozzleGroupsTests
    {
        // S3200 = 4칩 × 2열 × 400 = 3,200
        private const int Chips = 4, Rows = 2, PerRow = 400, First = 1, Last = 3200;

        [Fact]
        public void 칩_버튼은_칩_수만큼_나오고_800개씩_맡는다()
        {
            var chips = NozzleGroups.ByChip(Chips, Rows, PerRow, First, Last);

            Assert.Equal(4, chips.Count);
            Assert.Equal(new[] { "칩1", "칩2", "칩3", "칩4" }, chips.Select(g => g.Label));
            Assert.All(chips, g => Assert.Equal(800, g.Nozzles.Count));
        }

        [Fact]
        public void 칩_버튼은_서로_겹치지_않고_전체를_덮는다()
        {
            // 겹치면 한 칩을 끌 때 옆 칩까지 꺼지고, 비면 그 노즐은 어느 버튼으로도 못 집는다.
            var all = NozzleGroups.ByChip(Chips, Rows, PerRow, First, Last)
                                  .SelectMany(g => g.Nozzles).ToList();

            Assert.Equal(3200, all.Count);
            Assert.Equal(3200, all.Distinct().Count());
            Assert.Equal(1,    all.Min());
            Assert.Equal(3200, all.Max());
        }

        [Fact]
        public void 칩1은_1_800_칩2는_801_1600_이다()
        {
            var chips = NozzleGroups.ByChip(Chips, Rows, PerRow, First, Last);

            Assert.Equal(1,    chips[0].Nozzles.First());
            Assert.Equal(800,  chips[0].Nozzles.Last());
            Assert.Equal(801,  chips[1].Nozzles.First());
            Assert.Equal(1600, chips[1].Nozzles.Last());
            Assert.Equal(2401, chips[3].Nozzles.First());
            Assert.Equal(3200, chips[3].Nozzles.Last());
        }

        [Fact]
        public void 열_버튼은_칩마다_흩어진_토막을_모두_모은다()
        {
            // ★ A열은 칩1·2·3·4 에 400개씩 흩어져 있다. 한 토막만 잡으면 그 칩만 켜진다.
            var rows = NozzleGroups.ByRow(Chips, Rows, PerRow, First, Last);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "A열", "B열" }, rows.Select(g => g.Label));
            Assert.All(rows, g => Assert.Equal(1600, g.Nozzles.Count));

            // A열 = 칩1의 1~400, 칩2의 801~1200, 칩3의 1601~2000, 칩4의 2401~2800
            var a = rows[0].Nozzles;
            Assert.Contains(1,    a);
            Assert.Contains(400,  a);
            Assert.Contains(801,  a);
            Assert.Contains(2800, a);
            Assert.DoesNotContain(401, a);    // 401~800 은 칩1의 B열
        }

        [Fact]
        public void 열_버튼들도_겹치지_않고_전체를_덮는다()
        {
            var all = NozzleGroups.ByRow(Chips, Rows, PerRow, First, Last)
                                  .SelectMany(g => g.Nozzles).ToList();

            Assert.Equal(3200, all.Count);
            Assert.Equal(3200, all.Distinct().Count());
        }

        [Fact]
        public void 칩_하나면_칩_버튼을_만들지_않는다()
        {
            // 칩이 없는 헤드(S800)에서 "칩1" 하나만 뜨면 [전체] 와 같은 뜻이라 화면만 어지럽다.
            Assert.Empty(NozzleGroups.ByChip(1, 2, 400, 1, 800));
        }

        [Fact]
        public void 칩이_없어도_열_버튼은_나온다()
        {
            // S800 = 2열 × 400.
            var rows = NozzleGroups.ByRow(1, 2, 400, 1, 800);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { 1, 400 },   new[] { rows[0].Nozzles.First(), rows[0].Nozzles.Last() });
            Assert.Equal(new[] { 401, 800 }, new[] { rows[1].Nozzles.First(), rows[1].Nozzles.Last() });
        }

        [Fact]
        public void 설정이_헤드보다_크게_잡혀도_없는_번호를_넣지_않는다()
        {
            // 칩4 × 열2 × 400 = 3200 인데 총 노즐 수가 3000 으로 적혀 있는 경우.
            // 그대로 두면 선택은 되고 토출 단계에서 조용히 빠진다.
            var chips = NozzleGroups.ByChip(Chips, Rows, PerRow, 1, 3000);

            Assert.All(chips, g => Assert.All(g.Nozzles, n => Assert.InRange(n, 1, 3000)));
            Assert.Equal(3000, chips.SelectMany(g => g.Nozzles).Count());
            Assert.DoesNotContain(chips[3].Nozzles, n => n > 3000);
        }

        [Fact]
        public void 시작_번호가_1이_아니어도_따라간다()
        {
            var chips = NozzleGroups.ByChip(2, 2, 100, firstNozzle: 0, lastNozzle: 399);

            Assert.Equal(0,   chips[0].Nozzles.First());
            Assert.Equal(199, chips[0].Nozzles.Last());
            Assert.Equal(200, chips[1].Nozzles.First());
        }

        [Theory]
        [InlineData(0,  "A")]
        [InlineData(1,  "B")]
        [InlineData(25, "Z")]
        [InlineData(26, "27")]   // 26개를 넘으면 숫자로 — 글자가 겹치면 어느 열인지 못 읽는다
        public void 열_이름은_도면_표기를_따른다(int index, string expected)
            => Assert.Equal(expected, NozzleGroups.RowName(index));
    }
}
