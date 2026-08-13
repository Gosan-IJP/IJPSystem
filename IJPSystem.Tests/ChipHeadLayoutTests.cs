using System.Collections.Generic;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// S3200(400×2열×4칩) 물리 배치 검증.
    ///
    /// <para><b>여기 있는 시험은 번호 규약이 바뀌어도 그대로 성립한다</b> — 전부 물리 주소
    /// (칩·열·칩 안 번호)로만 확인하기 때문이다. 번호 규약은 아직 미확정이고, 확정되면
    /// <see cref="ChipHeadLayout.Numbering"/> 기본값만 바꾸면 된다.</para>
    ///
    /// <para>기준은 헤드 도면의 세 숫자다: <c>120.184mm (2839/600inch)</c>,
    /// <c>1.016mm (24/600inch)</c>, <c>15.24mm (360/600inch)</c>. 계산이 이 셋과 맞아떨어지면
    /// 피치·겹침·칩 배치가 모두 맞은 것이다.</para>
    /// </summary>
    public class ChipHeadLayoutTests
    {
        private static ChipHeadLayout S3200(
            ChipHeadLayout.Numbering order = ChipHeadLayout.Numbering.ChipRowBlock)
            => ChipHeadLayout.S3200(order);

        // ── 도면 대조 ─────────────────────────────────────────────────────────

        [Fact]
        public void 노즐_수는_3200이다()
        {
            var h = S3200();
            Assert.Equal(3200, h.TotalNozzles);
            Assert.Equal(800,  h.NozzlesPerChip);
        }

        [Fact]
        public void 인쇄폭이_도면의_120_184mm_와_같다()
        {
            // 도면: 120.184mm = 2839/600 inch. 이 값이 맞으면 피치·겹침·칩수가 전부 맞은 것이다.
            // (정확값은 2839×25400/600 = 120184.333µm 이고, 도면 표기는 그것을 mm 세 자리로 반올림한 것)
            var h = S3200();
            Assert.Equal(2840, h.SlotCount);              // 격자 칸 = 2839 + 1
            Assert.Equal(120.184, h.PrintWidthUm / 1000.0, precision: 3);
        }

        [Fact]
        public void 겹침_때문에_격자_칸이_노즐_수보다_적다()
        {
            // 3200개가 2840칸에 놓인다 — 360개는 앞 칩과 같은 칸에 겹쳐 있다.
            var h = S3200();
            Assert.True(h.SlotCount < h.TotalNozzles);
            Assert.Equal(360, h.TotalNozzles - h.SlotCount);
        }

        [Fact]
        public void 실효_피치는_600npi_이고_열_안_간격은_그_두_배다()
        {
            var h = S3200();
            Assert.Equal(42.3333, h.EffectivePitchUm, precision: 4);
            Assert.Equal(84.6667, h.InRowPitchUm,     precision: 4);
            Assert.Equal(600.0,   h.EffectiveDpi,     precision: 3);
        }

        [Fact]
        public void 칩은_60노즐씩_겹치고_340만큼만_나아간다()
        {
            var h = S3200();
            Assert.Equal(60,  h.OverlapNozzles);
            Assert.Equal(340, h.ChipAdvanceNozzles);
        }

        // ── 크로스스캔 배치 ───────────────────────────────────────────────────

        [Fact]
        public void 칩1_A1_이_원점이고_B1_이_반_피치_옆이다()
        {
            // A 와 B 가 반 피치 어긋나 맞물려야 600npi 가 된다. 같은 자리면 300npi 밖에 안 된다.
            var h = S3200();
            Assert.Equal(0, h.At(1, NozzleRow.A, 1).Slot);
            Assert.Equal(1, h.At(1, NozzleRow.B, 1).Slot);
            Assert.Equal(h.EffectivePitchUm, h.At(1, NozzleRow.B, 1).XUm, precision: 4);
        }

        [Fact]
        public void 마지막_노즐은_칩4_B400_이고_2839번_칸이다()
        {
            var h = S3200();
            var last = h.At(4, NozzleRow.B, 400);
            Assert.Equal(2839, last.Slot);
            Assert.Equal(120.184, last.XUm / 1000.0, precision: 3);
        }

        [Fact]
        public void 도면의_Over_Lap_이_실제로_같은_칸에_놓인다()
        {
            // 도면 표기: 칩1 A#341~A#400 이 칩2 A#1~A#60 과 겹친다.
            var h = S3200();
            Assert.Equal(h.At(2, NozzleRow.A, 1).Slot,  h.At(1, NozzleRow.A, 341).Slot);
            Assert.Equal(h.At(2, NozzleRow.A, 60).Slot, h.At(1, NozzleRow.A, 400).Slot);
            Assert.Equal(h.At(2, NozzleRow.B, 1).Slot,  h.At(1, NozzleRow.B, 341).Slot);
        }

        [Fact]
        public void 겹침_쌍은_열당_60개씩_칩_경계마다_나온다()
        {
            var h = S3200();
            var pairs = h.OverlapPairs();

            Assert.Equal(3 * 2 * 60, pairs.Count);              // 경계 3 × 열 2 × 60
            Assert.All(pairs, p => Assert.Equal(p.Earlier.Slot, p.Later.Slot));
            Assert.All(pairs, p => Assert.Equal(p.Earlier.Chip + 1, p.Later.Chip));
        }

        [Fact]
        public void 모든_칸이_빠짐없이_채워진다()
        {
            // 한 칸이라도 비면 그 줄에 인쇄가 안 된다 — 흰 줄로 나타난다.
            var h = S3200();
            var slots = h.AllByAddress().Select(p => p.Slot).ToHashSet();

            Assert.Equal(h.SlotCount, slots.Count);
            Assert.Equal(0, slots.Min());
            Assert.Equal(h.SlotCount - 1, slots.Max());
        }

        // ── 스캔 방향(발사 시점) ──────────────────────────────────────────────

        [Fact]
        public void 짝수_칩은_스캔방향으로_15_24mm_앞서_있다()
        {
            // 이 값을 무시하고 동시에 쏘면 칩마다 15mm 어긋난 그림이 나온다.
            var h = S3200();
            Assert.Equal(0,     h.At(1, NozzleRow.A, 1).YUm, precision: 3);
            Assert.Equal(15240, h.At(2, NozzleRow.B, 1).YUm, precision: 3);
            Assert.Equal(0,     h.At(3, NozzleRow.A, 1).YUm, precision: 3);
            Assert.Equal(15240, h.At(4, NozzleRow.B, 1).YUm, precision: 3);
        }

        [Fact]
        public void 칩_안_두_열은_1_016mm_떨어져_있고_짝수_칩은_앞뒤가_뒤집힌다()
        {
            // 도면: 칩1·3 은 A 가 앞(왼쪽), 칩2·4 는 B 가 앞. 뒤집힘을 빠뜨리면
            // 짝수 칩의 두 열이 1.016mm 씩 반대로 어긋난다.
            var h = S3200();

            Assert.Equal(0,    h.At(1, NozzleRow.A, 1).YUm, precision: 3);
            Assert.Equal(1016, h.At(1, NozzleRow.B, 1).YUm, precision: 3);

            Assert.Equal(15240,        h.At(2, NozzleRow.B, 1).YUm, precision: 3);
            Assert.Equal(15240 + 1016, h.At(2, NozzleRow.A, 1).YUm, precision: 3);
        }

        // ── 번호 규약 (미확정 — 갈아 끼울 수 있다는 것만 확인) ────────────────

        [Theory]
        [InlineData(ChipHeadLayout.Numbering.ChipRowBlock)]
        [InlineData(ChipHeadLayout.Numbering.ChipInterleaved)]
        [InlineData(ChipHeadLayout.Numbering.ByPosition)]
        public void 어떤_번호_규약이든_1대1_대응이_된다(ChipHeadLayout.Numbering order)
        {
            // 규약이 무엇이든 번호 하나에 노즐 하나여야 한다. 겹치거나 비면 패턴이 조용히 어긋난다.
            var h = S3200(order);
            var numbers = h.AllByAddress().Select(p => p.Number).ToList();

            Assert.Equal(h.TotalNozzles, numbers.Count);
            Assert.Equal(h.TotalNozzles, numbers.Distinct().Count());
            Assert.Equal(1, numbers.Min());
            Assert.Equal(h.TotalNozzles, numbers.Max());
        }

        [Theory]
        [InlineData(ChipHeadLayout.Numbering.ChipRowBlock)]
        [InlineData(ChipHeadLayout.Numbering.ChipInterleaved)]
        [InlineData(ChipHeadLayout.Numbering.ByPosition)]
        public void 번호에서_주소로_돌아와도_같은_노즐이다(ChipHeadLayout.Numbering order)
        {
            var h = S3200(order);
            foreach (var p in h.AllByAddress())
            {
                var back = h.PositionOf(p.Number);
                Assert.NotNull(back);
                Assert.Equal(p.Chip, back!.Value.Chip);
                Assert.Equal(p.Row,  back.Value.Row);
                Assert.Equal(p.Slot, back.Value.Slot);
            }
        }

        [Fact]
        public void ChipRowBlock_은_칩1_A_400개_다음에_칩1_B_가_온다()
        {
            var h = S3200(ChipHeadLayout.Numbering.ChipRowBlock);
            Assert.Equal(1,   h.At(1, NozzleRow.A, 1).Number);
            Assert.Equal(400, h.At(1, NozzleRow.A, 400).Number);
            Assert.Equal(401, h.At(1, NozzleRow.B, 1).Number);
            Assert.Equal(801, h.At(2, NozzleRow.A, 1).Number);
        }

        [Fact]
        public void ChipInterleaved_는_번호가_1_늘면_한_칸_옆이다()
        {
            var h = S3200(ChipHeadLayout.Numbering.ChipInterleaved);
            Assert.Equal(1, h.At(1, NozzleRow.A, 1).Number);
            Assert.Equal(2, h.At(1, NozzleRow.B, 1).Number);
            Assert.Equal(3, h.At(1, NozzleRow.A, 2).Number);
        }

        // ── 정렬 ──────────────────────────────────────────────────────────────

        [Fact]
        public void 위치순_정렬은_칸_순서를_따르고_범위밖은_돌려준다()
        {
            var h = S3200();
            var sorted = h.SortByX(new[] { 900, 1, 5000, 401, 1, -3 }, out var ignored);

            Assert.Equal(new[] { 5000, -3 }, ignored);          // 중복(1)은 조용히 넘긴다
            Assert.Equal(3, sorted.Count);
            for (int i = 1; i < sorted.Count; i++)
                Assert.True(sorted[i - 1].Slot <= sorted[i].Slot);
        }

        // ── 설정 검사 ─────────────────────────────────────────────────────────

        [Fact]
        public void 겹침이_열_전체면_거부한다()
        {
            // 400 겹치면 다음 칩이 앞으로 못 나아가 폭이 늘지 않는다 — 조용히 두면 원인 찾기 어렵다.
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new ChipHeadLayout(4, 400, 400, 42.3333, 1016, 15240));
        }

        [Fact]
        public void 겹침이_없으면_칩이_그대로_이어_붙는다()
        {
            // 겹침 0 이면 칸 수 = 노즐 수. 일반 헤드로도 쓸 수 있다는 확인.
            var h = new ChipHeadLayout(4, 400, 0, 42.3333, 1016, 15240);
            Assert.Equal(h.TotalNozzles, h.SlotCount);
            Assert.Empty(h.OverlapPairs());
        }

        // ── 장비 설정 연결 ────────────────────────────────────────────────────

        [Theory]
        [InlineData("",                ChipHeadLayout.Numbering.ChipRowBlock)]   // 미설정 = 기본
        [InlineData("ChipRowBlock",    ChipHeadLayout.Numbering.ChipRowBlock)]
        [InlineData("chipinterleaved", ChipHeadLayout.Numbering.ChipInterleaved)] // 대소문자 무관
        [InlineData("ByPosition",      ChipHeadLayout.Numbering.ByPosition)]
        public void 번호_규약_문자열을_읽는다(string value, ChipHeadLayout.Numbering expected)
            => Assert.Equal(expected, IJPSystem.Platform.Infrastructure.Config.HeadSpec.ParseNumbering(value));

        [Fact]
        public void 오타난_번호_규약은_기본값으로_넘기지_않고_막는다()
        {
            // 조용히 기본값으로 가면 패턴이 통째로 재배치되는데 화면에는 아무 표시가 없다.
            Assert.Throws<System.ArgumentException>(
                () => IJPSystem.Platform.Infrastructure.Config.HeadSpec.ParseNumbering("ChipRowBlok"));
        }
    }
}
