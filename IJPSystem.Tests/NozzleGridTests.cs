using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 노즐 격자 매핑 검증 — 하드웨어 없이 확인 가능.
    ///
    /// 이 기능이 존재하는 이유가 곧 핵심 테스트다: 중간 노즐이 불토출이면 리스트 순번과
    /// 실제 노즐 번호가 어긋난다. 매핑 전에는 4번 액적이 3번으로 보고됐다.
    /// </summary>
    public class NozzleGridTests
    {
        private const double PitchUm = 254.0;
        private const double UmPerPx = 2.0;
        private const double PitchPx = PitchUm / UmPerPx;   // 127px

        /// <summary>지정한 격자 위치들에 액적을 놓는다(0-based 격자 인덱스).</summary>
        private static List<DropletInfo> DropsAt(IEnumerable<int> gridIndices, double x0 = 100, double jitterPx = 0)
            => gridIndices.Select(i => new DropletInfo
            {
                CentroidXPixel  = x0 + i * PitchPx + jitterPx,
                CentroidYPixel  = 200,
                AreaPx          = 200,
                DiameterMicron  = 30,
                VolumePicoLiter = 14,
            }).ToList();

        private static NozzleGridResult Map(List<DropletInfo> drops, IReadOnlyList<int> expected)
            => NozzleGrid.Map(drops, expected, PitchUm, UmPerPx);

        // ── 정상: 전 노즐 토출 ────────────────────────────────────────────────
        [Fact]
        public void AllNozzlesFiring_MapsOneToOne()
        {
            var expected = Enumerable.Range(1, 15).ToList();
            var r = Map(DropsAt(Enumerable.Range(0, 15)), expected);

            Assert.Equal(15, r.Mapped.Count);
            Assert.Empty(r.MissingNozzles);
            Assert.True(r.AbsoluteMappingConfident);
            Assert.Equal(expected, r.Mapped.Select(m => m.NozzleNumber));
        }

        // ── 이 기능의 존재 이유 ───────────────────────────────────────────────
        [Fact]
        public void MissingMiddleNozzle_DoesNotShiftLaterNozzleNumbers()
        {
            // 노즐 1~15 중 3번(격자 인덱스 2)이 불토출.
            var expected = Enumerable.Range(1, 15).ToList();
            var present  = Enumerable.Range(0, 15).Where(i => i != 2);

            var r = Map(DropsAt(present), expected);

            Assert.Equal(new[] { 3 }, r.MissingNozzles);
            Assert.True(r.AbsoluteMappingConfident);

            // 매핑 전이었다면 리스트 3번째 원소가 "3번 노즐"로 보고됐을 것 — 실제로는 4번이다.
            var third = r.Mapped[2];
            Assert.Equal(4, third.NozzleNumber);

            // 4번 이후가 밀리지 않았는지 전수 확인
            Assert.Equal(new[] { 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                         r.Mapped.Select(m => m.NozzleNumber));
        }

        [Fact]
        public void MultipleMissingNozzles_AllReported()
        {
            var expected = Enumerable.Range(1, 15).ToList();
            var present  = Enumerable.Range(0, 15).Where(i => i != 4 && i != 9);   // 5번, 10번 불토출

            var r = Map(DropsAt(present), expected);

            Assert.Equal(new[] { 5, 10 }, r.MissingNozzles);
            Assert.Equal(13, r.Mapped.Count);
        }

        // ── 양 끝 불토출은 구분 불가 — 정직하게 신뢰도를 낮춘다 ───────────────
        [Fact]
        public void MissingEdgeNozzle_IsFlaggedAsNotConfident()
        {
            // 1번(격자 0)이 불토출 → 남은 액적만 보면 격자가 통째로 밀린 것과 구분되지 않는다.
            var expected = Enumerable.Range(1, 15).ToList();
            var r = Map(DropsAt(Enumerable.Range(1, 14)), expected);

            Assert.False(r.AbsoluteMappingConfident);
            Assert.NotNull(r.Ambiguity);
            Assert.NotEmpty(r.MissingNozzles);   // "어딘가 빠졌다"는 여전히 알 수 있다
        }

        // ── 격자 이탈(직진성) ─────────────────────────────────────────────────
        [Fact]
        public void GridDeviation_IsReportedForOffAxisDrop()
        {
            var expected = Enumerable.Range(1, 5).ToList();
            var drops = DropsAt(Enumerable.Range(0, 5));
            drops[2].CentroidXPixel += 20;         // 3번 노즐이 20px 틀어짐

            var r = Map(drops, expected);

            Assert.Equal(5, r.Mapped.Count);
            var off = r.Mapped.Single(m => m.NozzleNumber == 3);
            Assert.Equal(20, off.GridDeviationPixel, precision: 0);
            Assert.Equal(20, r.MaxDeviationPixel, precision: 0);
        }

        [Fact]
        public void SmallJitter_StillSnapsToCorrectGridSlot()
        {
            // 피치의 40% 이내 흔들림은 같은 격자점으로 붙어야 한다.
            var expected = Enumerable.Range(1, 5).ToList();
            var drops = DropsAt(Enumerable.Range(0, 5));
            drops[1].CentroidXPixel += PitchPx * 0.3;

            var r = Map(drops, expected);
            Assert.Equal(5, r.Mapped.Count);
            Assert.Empty(r.MissingNozzles);
        }

        // ── 경계/실패 경로 ────────────────────────────────────────────────────
        [Fact]
        public void NoDrops_AllExpectedAreMissing()
        {
            var expected = Enumerable.Range(1, 15).ToList();
            var r = Map(new List<DropletInfo>(), expected);

            Assert.Empty(r.Mapped);
            Assert.Equal(expected, r.MissingNozzles);
        }

        [Fact]
        public void ZeroPitch_FailsInsteadOfGuessing()
        {
            var r = NozzleGrid.Map(DropsAt(Enumerable.Range(0, 5)), new[] { 1, 2, 3, 4, 5 }, 0, UmPerPx);

            Assert.Empty(r.Mapped);
            Assert.NotNull(r.Ambiguity);
        }

        [Fact]
        public void MoreSlotsThanExpected_FlagsCalibrationProblem()
        {
            // 5개 기대인데 격자가 8칸에 걸쳐 있으면 피치/스케일 교정이 틀린 것이다.
            var r = Map(DropsAt(new[] { 0, 2, 4, 6, 7 }), new[] { 1, 2, 3, 4, 5 });

            Assert.False(r.AbsoluteMappingConfident);
            Assert.Contains("큽니다", r.Ambiguity);
        }

        [Fact]
        public void NonContiguousNozzleSelection_MapsToSelectedNumbers()
        {
            // 홀수 노즐만 토출시킨 경우 — 격자 인덱스는 0..4, 번호는 1,3,5,7,9
            var expected = new[] { 1, 3, 5, 7, 9 };
            var r = Map(DropsAt(Enumerable.Range(0, 5)), expected);

            Assert.Equal(expected, r.Mapped.Select(m => m.NozzleNumber));
            Assert.Empty(r.MissingNozzles);
        }
    }
}
