using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 스핏 패턴 노즐 매핑 검증 — 헤드 없이 확인 가능.
    /// 노즐 번호 기준(0/1 시작)이 어긋나면 패턴이 한 칸씩 밀려 <b>엉뚱한 노즐이 토출</b>되는데,
    /// 실장에서는 "왜 옆 노즐이 나오지?" 로 보여 원인 추적이 어렵다.
    /// </summary>
    public class SpitPatternTests
    {
        private const int Nozzles = 128;

        private static S800SingleSpitPatternBuilder Builder(int first = 1)
            => new(nozzleCount: Nozzles, rows: 1, firstNozzleIndex: first);

        [Fact]
        public void OneBased_Nozzle1_MapsToColumn0()
        {
            var b = Builder(first: 1);
            var pat = b.Build(new SpitSettings { Nozzles = new[] { 1 }, SpitGreyLevel = 255 });

            Assert.Equal(255, pat[0, 0]);
            Assert.Equal(0, pat[0, 1]);
        }

        [Fact]
        public void OneBased_LastNozzle_MapsToLastColumn()
        {
            var b = Builder(first: 1);
            var pat = b.Build(new SpitSettings { Nozzles = new[] { Nozzles }, SpitGreyLevel = 255 });

            Assert.Equal(255, pat[0, Nozzles - 1]);
            Assert.Empty(b.LastIgnoredNozzles);
        }

        [Fact]
        public void ZeroBased_ShiftsMappingByOne()
        {
            // 기준을 0으로 두면 같은 입력이 다른 컬럼으로 간다 — 이 차이가 곧 실장 오작동이다.
            var pat = Builder(first: 0).Build(new SpitSettings { Nozzles = new[] { 1 }, SpitGreyLevel = 255 });
            Assert.Equal(0, pat[0, 0]);
            Assert.Equal(255, pat[0, 1]);
        }

        [Fact]
        public void OutOfRangeNozzles_AreReportedNotSilentlyDropped()
        {
            var b = Builder();
            var pat = b.Build(new SpitSettings { Nozzles = new[] { 1, 0, 129, 500 }, SpitGreyLevel = 255 });

            Assert.Equal(255, pat[0, 0]);                       // 1번만 유효
            Assert.Equal(new[] { 0, 129, 500 }, b.LastIgnoredNozzles);
        }

        [Fact]
        public void TickleLevel_FillsUnselectedColumns()
        {
            var pat = Builder().Build(new SpitSettings
            {
                Nozzles = new[] { 5 }, SpitGreyLevel = 255, TickleGreyLevel = 40,
            });

            Assert.Equal(255, pat[0, 4]);   // 5번 = 컬럼 4
            Assert.Equal(40,  pat[0, 0]);   // 미선택은 틱클
            Assert.Equal(40,  pat[0, 127]);
        }

        [Fact]
        public void NoTickle_LeavesUnselectedColumnsZero()
        {
            var pat = Builder().Build(new SpitSettings
            {
                Nozzles = new[] { 5 }, SpitGreyLevel = 255, TickleGreyLevel = 0,
            });
            Assert.Equal(0, pat[0, 0]);
        }

        [Fact]
        public void EmptySelection_ProducesBlankPattern()
        {
            var b = Builder();
            var pat = b.Build(new SpitSettings { Nozzles = System.Array.Empty<int>() });

            for (int c = 0; c < Nozzles; c++) Assert.Equal(0, pat[0, c]);
            Assert.Empty(b.LastIgnoredNozzles);
        }

        [Fact]
        public void CountSpittingColumns_CountsOnlySpitLevel()
        {
            var b = Builder();
            var pat = b.Build(new SpitSettings
            {
                Nozzles = new[] { 1, 2, 3 }, SpitGreyLevel = 255, TickleGreyLevel = 40,
            });
            Assert.Equal(3, S800SingleSpitPatternBuilder.CountSpittingColumns(pat, 255, 40));
        }

        [Fact]
        public void GreyLevels_AreClampedToByteRange()
        {
            var pat = Builder().Build(new SpitSettings { Nozzles = new[] { 1 }, SpitGreyLevel = 9999 });
            Assert.Equal(255, pat[0, 0]);
        }
    }
}
