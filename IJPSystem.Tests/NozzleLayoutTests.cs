using IJPSystem.Platform.Infrastructure.Print;
using System.Linq;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 노즐 물리 위치 모델. 여기가 틀리면 <b>인쇄 전체가 틀린다</b> — 이미지 픽셀을 노즐에
    /// 배정하는 기준이라, 한 칸만 어긋나도 그림이 통째로 밀리거나 재배치된다.
    /// </summary>
    public class NozzleLayoutTests
    {
        // 실장 값(랩뷰 공유 2026-08-08): 열 간 어긋남 84.7µm.
        // 열이 3개이므로 한 열 안 간격은 그 3배다.
        private const double RowOffsetUm  = 84.7;
        private const int    Rows         = 3;
        private const double InRowPitchUm = RowOffsetUm * Rows;   // 254.1

        private static NozzleLayout Make(int nozzlesPerRow = 4, int headCount = 1,
                                         double headPitchUm = 0,
                                         NozzleLayout.NozzleOrder order = NozzleLayout.NozzleOrder.Interleaved)
            => new(Rows, nozzlesPerRow, InRowPitchUm, RowOffsetUm, headCount, headPitchUm, order);

        /// <summary>
        /// 드랍와처가 재는 254µm 과 인쇄 해상도를 정하는 84.7µm 은 <b>같은 배열의 다른 측면</b>이다.
        /// 스핏은 한 열만 쏘므로 화면에는 254 로 보이고, 세 열이 엇갈려 실효 84.7 이 된다.
        /// 이 관계가 깨지면 둘 중 하나가 틀린 것이다.
        /// </summary>
        [Fact]
        public void 열이_3개면_실효간격은_한열간격의_3분의1()
        {
            var layout = Make();

            Assert.Equal(254.1, layout.InRowPitchUm, 3);
            Assert.Equal(84.7, layout.EffectivePitchUm, 3);
            Assert.Equal(300.0, layout.EffectiveDpi, 0);   // 25400 / 84.7 ≈ 300dpi
        }

        /// <summary>번호가 1 늘면 X 로 딱 한 칸(84.7µm) 옆이어야 한다 — 엇갈린 열의 번호 규약.</summary>
        [Fact]
        public void 인터리브_번호는_X로_한칸씩_증가한다()
        {
            var layout = Make(nozzlesPerRow: 4);

            var xs = layout.All().Select(p => p.XUm).ToArray();

            Assert.Equal(12, xs.Length);                       // 3열 × 4개
            for (int i = 1; i < xs.Length; i++)
                Assert.Equal(RowOffsetUm, xs[i] - xs[i - 1], 6);
        }

        /// <summary>같은 열 안에서는 254.1µm 간격 — 스핏 한 줄로 드랍와처가 보게 될 간격.</summary>
        [Fact]
        public void 같은_열_안의_간격은_한열간격이다()
        {
            var layout = Make(nozzlesPerRow: 4);

            var row0 = layout.All().Where(p => p.Row == 0).Select(p => p.XUm).ToArray();

            Assert.Equal(4, row0.Length);
            for (int i = 1; i < row0.Length; i++)
                Assert.Equal(InRowPitchUm, row0[i] - row0[i - 1], 6);
        }

        /// <summary>번호를 한 열씩 세는 헤드라면 번호 순과 위치 순이 <b>달라진다</b>.</summary>
        [Fact]
        public void RowByRow_규약은_번호순과_위치순이_다르다()
        {
            var layout = Make(nozzlesPerRow: 4, order: NozzleLayout.NozzleOrder.RowByRow);

            var byNumber = layout.All().Select(p => p.Number).ToArray();
            var byX      = layout.SortByX(byNumber, out _).Select(p => p.Number).ToArray();

            Assert.NotEqual(byNumber, byX);
            // 1번(열0 첫번째)·2번(열0 두번째)은 254.1 떨어져 있고, 그 사이에 다른 열 노즐이 낀다.
            Assert.Equal(1, byX[0]);
            Assert.Equal(5, byX[1]);   // 열1 첫번째 — X = 84.7
        }

        [Fact]
        public void 두번째_헤드는_헤드간격만큼_뒤에_온다()
        {
            var layout = Make(nozzlesPerRow: 4, headCount: 2, headPitchUm: 2000.0);

            var first  = layout.PositionOf(1)!.Value;                        // 헤드0 첫 노즐
            var second = layout.PositionOf(1 + layout.NozzlesPerHead)!.Value; // 헤드1 첫 노즐

            Assert.Equal(0, first.Head);
            Assert.Equal(1, second.Head);
            Assert.Equal(2000.0, second.XUm - first.XUm, 6);
        }

        /// <summary>사용 노즐이 뒤섞여 들어와도 위치 순으로 정렬돼야 한다 — 컬럼 배정의 전제.</summary>
        [Fact]
        public void SortByX_는_뒤섞인_입력을_위치순으로_돌려준다()
        {
            var layout = Make(nozzlesPerRow: 4);

            var sorted = layout.SortByX(new[] { 7, 2, 11, 1 }, out var ignored);

            Assert.Empty(ignored);
            Assert.Equal(new[] { 1, 2, 7, 11 }, sorted.Select(p => p.Number));
            Assert.True(sorted.Zip(sorted.Skip(1)).All(z => z.First.XUm < z.Second.XUm));
        }

        /// <summary>범위 밖 번호는 조용히 사라지면 안 된다 — 알려야 원인을 찾는다.</summary>
        [Fact]
        public void 범위_밖_번호는_무시목록으로_돌려준다()
        {
            var layout = Make(nozzlesPerRow: 4);   // 1~12

            var sorted = layout.SortByX(new[] { 0, 1, 13, 12 }, out var ignored);

            Assert.Equal(new[] { 1, 12 }, sorted.Select(p => p.Number));
            Assert.Equal(new[] { 0, 13 }, ignored);
        }

        /// <summary>같은 노즐을 두 번 쏠 수는 없다 — 중복은 한 번만 남는다.</summary>
        [Fact]
        public void 중복_번호는_한번만_남는다()
        {
            var layout = Make(nozzlesPerRow: 4);

            var sorted = layout.SortByX(new[] { 3, 3, 3 }, out var ignored);

            Assert.Single(sorted);
            Assert.Empty(ignored);
        }

        /// <summary>S800 규모(800노즐)에서도 번호↔위치 관계가 유지되는지.</summary>
        [Fact]
        public void S800_규모에서_전체_노즐수와_폭이_맞는다()
        {
            var layout = new NozzleLayout(Rows, nozzlesPerRow: 800 / Rows,
                                          InRowPitchUm, RowOffsetUm);

            Assert.Equal(266, layout.NozzlesPerRow);
            Assert.Equal(798, layout.TotalNozzles);

            var last = layout.PositionOf(798)!.Value;
            // 마지막 노즐 X = (전체-1) × 실효간격 — 인터리브라 번호가 곧 칸수다.
            Assert.Equal(797 * RowOffsetUm, last.XUm, 3);
        }
    }
}
