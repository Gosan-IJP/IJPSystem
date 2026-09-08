using System;
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 발사 지도 → Meteor 이미지 버퍼 패킹.
    ///
    /// <para>이 변환이 틀리면 통신 오류가 아니라 <b>그림이 틀리게</b> 나온다 — 헤드에 잉크가
    /// 나간 뒤에야 알게 되는 종류의 실수라, 장비 없이 여기서 잡는다.</para>
    /// </summary>
    public class MeteorImageBufferTests
    {
        // ── 행 정렬 ──────────────────────────────────────────────────────
        // 행이 DWORD 경계에서 시작하지 않으면 엔진이 스트라이드를 알 수 없다.

        [Theory]
        [InlineData(32, 1, 1)]    // 1bpp 32화소 = 정확히 1 DWORD
        [InlineData(33, 1, 2)]    // 한 화소 넘치면 한 DWORD 더
        [InlineData(1,  1, 1)]
        [InlineData(16, 2, 1)]    // 2bpp 는 DWORD 당 16화소 (S800 이 이 설정)
        [InlineData(17, 2, 2)]
        [InlineData(8,  4, 1)]    // 4bpp 는 DWORD 당 8화소
        [InlineData(9,  4, 2)]
        public void 행은_DWORD_경계에서_시작한다(int width, int bpp, int expectedRowDwords)
            => Assert.Equal(expectedRowDwords, MeteorImageBuffer.RowDwordsFor(width, bpp));

        [Fact]
        public void 버퍼_크기는_행간격_곱하기_스텝수다()
        {
            var levels = new byte[7, 33];          // 7스텝 × 33노즐
            var packed = MeteorImageBuffer.Pack(levels, bpp: 1);

            Assert.Equal(2, packed.RowDwords);      // 33화소 → 2 DWORD
            Assert.Equal(14, packed.Data.Length);   // 2 × 7
        }

        // ── 비트 배치 ────────────────────────────────────────────────────

        /// <summary>
        /// SDK 매뉴얼 §10.6 의 예제 그대로.
        ///
        /// <para>"1bpp 1화소(x) × 2화소(y), 둘 다 켬" 의 이미지 데이터가
        /// <c>0x80000000, 0x80000000</c> 이라고 매뉴얼이 값까지 적어 두었다.
        /// 비트 순서를 반대로 짜면 여기서 걸린다 — 실장에서는 에러 없이 그림만 뒤집힌다.</para>
        /// </summary>
        [Fact]
        public void 매뉴얼_예제와_일치한다()
        {
            var levels = new byte[2, 1];
            levels[0, 0] = 1;
            levels[1, 0] = 1;
            var packed = MeteorImageBuffer.Pack(levels, bpp: 1);

            Assert.Equal(new uint[] { 0x80000000u, 0x80000000u }, packed.Data);
        }

        [Fact]
        public void 첫_화소가_최상위_비트에_들어간다()
        {
            var levels = new byte[1, 4];
            levels[0, 0] = 1;
            var packed = MeteorImageBuffer.Pack(levels, bpp: 1);

            Assert.Equal(0x80000000u, packed.Data[0]);
        }

        [Fact]
        public void 화소_순서대로_하위_비트로_내려간다()
        {
            var levels = new byte[1, 4];
            levels[0, 0] = 1;
            levels[0, 3] = 1;
            var packed = MeteorImageBuffer.Pack(levels, bpp: 1);

            //   비트 31 30 29 28 …
            //   화소  0  1  2  3
            //         1  0  0  1
            Assert.Equal(0b1001u << 28, packed.Data[0]);
        }

        [Fact]
        public void 방울단계는_비트뎁스만큼_자리를_차지한다()
        {
            var levels = new byte[1, 3];
            levels[0, 0] = 1;
            levels[0, 1] = 2;
            levels[0, 2] = 3;
            var packed = MeteorImageBuffer.Pack(levels, bpp: 2);

            //   화소  0  1  2
            //        01 10 11  → 상위부터
            Assert.Equal(0b011011u << 26, packed.Data[0]);
        }

        [Fact]
        public void 스텝마다_다음_행으로_넘어간다()
        {
            var levels = new byte[2, 33];
            levels[0, 0]  = 1;   // 0행 첫 DWORD 의 최상위 비트
            levels[1, 32] = 1;   // 1행 둘째 DWORD (33번째 화소)
            var packed = MeteorImageBuffer.Pack(levels, bpp: 1);

            Assert.Equal(0x80000000u, packed.Data[0]);
            Assert.Equal(0u,          packed.Data[1]);
            Assert.Equal(0u,          packed.Data[2]);   // 1행 시작
            Assert.Equal(0x80000000u, packed.Data[3]);
        }

        /// <summary>남는 비트는 0 이어야 한다 — 매뉴얼: "각 행은 오른쪽을 0 으로 채운다".</summary>
        [Fact]
        public void 행_끝의_남는_비트는_0_이다()
        {
            var levels = new byte[1, 33];
            for (int c = 0; c < 33; c++) levels[0, c] = 1;
            var packed = MeteorImageBuffer.Pack(levels, bpp: 1);

            Assert.Equal(0xFFFFFFFFu, packed.Data[0]);
            Assert.Equal(0x80000000u, packed.Data[1]);   // 33번째 화소 하나만, 나머지는 패딩
        }

        [Fact]
        public void 빈_패턴은_전부_0_이다()
        {
            var packed = MeteorImageBuffer.Pack(new byte[3, 16], bpp: 4);
            Assert.All(packed.Data, d => Assert.Equal(0u, d));
        }

        // ── 거부해야 하는 것 ─────────────────────────────────────────────
        // 조용히 잘라내면 큰 방울이 작은 방울로 찍힌다 — 인쇄물을 봐야만 알게 된다.

        [Fact]
        public void 비트뎁스를_넘는_방울단계는_거부한다()
        {
            var levels = new byte[1, 2];
            levels[0, 1] = 2;   // 1bpp 상한은 1
            var ex = Assert.Throws<ArgumentException>(() => MeteorImageBuffer.Pack(levels, bpp: 1));
            Assert.Contains("상한", ex.Message);
        }

        /// <summary>
        /// 매뉴얼 §10.6 은 1·2·4 만 허용한다. ★8 을 반드시 포함해 둔다 — 우리 쪽
        /// <c>Print_Para.dat</c> 주석이 "8 = 방울 크기 단계" 라고 적고 있어 8 이 흘러들기 쉽다.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(8)]
        [InlineData(16)]
        public void 지원하지_않는_비트뎁스는_거부한다(int bpp)
        {
            Assert.False(MeteorImageBuffer.IsSupportedBpp(bpp));
            Assert.Throws<ArgumentOutOfRangeException>(() => MeteorImageBuffer.RowDwordsFor(8, bpp));
        }

        [Fact]
        public void 빈_지도는_거부한다()
            => Assert.Throws<ArgumentException>(() => MeteorImageBuffer.Pack(new byte[0, 0], bpp: 1));

        // ── 왕복 ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void 풀어_읽으면_원래_지도가_나온다(int bpp)
        {
            int width = 37, height = 5;
            int maxLevel = (1 << bpp) - 1;
            var levels = new byte[height, width];
            var rnd = new Random(1234);
            for (int s = 0; s < height; s++)
                for (int c = 0; c < width; c++)
                    levels[s, c] = (byte)rnd.Next(0, maxLevel + 1);

            var packed   = MeteorImageBuffer.Pack(levels, bpp);
            int perDword = 32 / bpp;
            uint mask    = (uint)maxLevel;

            for (int s = 0; s < height; s++)
                for (int c = 0; c < width; c++)
                {
                    uint word = packed.Data[s * packed.RowDwords + c / perDword];
                    uint got  = (word >> (32 - bpp - c % perDword * bpp)) & mask;
                    Assert.Equal(levels[s, c], (byte)got);
                }
        }
    }
}
