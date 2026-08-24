using System;
using IJPSystem.Platform.Infrastructure.Vision;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 글라스 정렬용 패턴 매칭.
    ///
    /// <para>여기서 확인하려는 것은 "찾았다/못 찾았다"가 아니라 <b>얼마나 정확히 어디인지</b>다.
    /// 결과가 그대로 스테이지 이동량이 되므로, 한 픽셀이 곧 정렬 오차다.</para>
    /// </summary>
    public class PatternMatcherTests
    {
        private const int W = 400, H = 300;

        /// <summary>배경에 잔무늬가 있는 장면. 평평한 배경이면 상관계수가 정의되지 않는다.</summary>
        private static GrayImage Scene(int markX, int markY, int seed = 1)
        {
            var px = new byte[W * H];
            var rnd = new Random(seed);
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(90 + rnd.Next(0, 20));

            // 비대칭 마크 — 대칭이면 어느 쪽으로 맞춰도 점수가 같아 위치가 흔들린다.
            Fill(px, markX,     markY,     20, 20, 230);
            Fill(px, markX + 6, markY + 6, 26, 8,  20);

            return new GrayImage(px, W, H);
        }

        private static void Fill(byte[] px, int x, int y, int w, int h, byte v)
        {
            for (int row = 0; row < h; row++)
            {
                int yy = y + row;
                if (yy < 0 || yy >= H) continue;
                for (int col = 0; col < w; col++)
                {
                    int xx = x + col;
                    if (xx < 0 || xx >= W) continue;
                    px[yy * W + xx] = v;
                }
            }
        }

        /// <summary>등록: 장면에서 마크 주변을 잘라 패턴으로 삼는다(화면의 ROI 드래그와 같다).</summary>
        private static GrayImage Template(GrayImage scene, int x, int y) => scene.Crop(x - 6, y - 6, 44, 34);

        [Fact]
        public void 같은_자리를_그대로_찾는다()
        {
            var scene = Scene(150, 120);
            var templ = Template(scene, 150, 120);

            var m = PatternMatcher.Find(scene, templ);

            Assert.True(m.Found);
            Assert.True(m.Score > 0.99, $"점수가 낮다: {m.Score:F3}");
            Assert.Equal(150 - 6 + (44 - 1) / 2.0, m.CenterX, 1);
            Assert.Equal(120 - 6 + (34 - 1) / 2.0, m.CenterY, 1);
        }

        [Theory]
        [InlineData(40, 25)]
        [InlineData(-35, 18)]
        [InlineData(70, -40)]
        public void 옮겨진_만큼_그대로_나온다(int dx, int dy)
        {
            // 정렬은 이 차이가 곧 이동량이다 — 부호가 뒤집히면 반대로 움직인다.
            var reference = Scene(150, 120);
            var templ     = Template(reference, 150, 120);
            var moved     = Scene(150 + dx, 120 + dy);

            var a = PatternMatcher.Find(reference, templ);
            var b = PatternMatcher.Find(moved, templ);

            Assert.True(b.Found);
            Assert.Equal(dx, b.CenterX - a.CenterX, 1);
            Assert.Equal(dy, b.CenterY - a.CenterY, 1);
        }

        [Fact]
        public void 밝기가_변해도_찾는다()
        {
            // 조명을 올리거나 노출을 바꿔도 같은 자리를 잡아야 한다 — NCC 를 고른 이유다.
            var scene = Scene(150, 120);
            var templ = Template(scene, 150, 120);

            var bright = new byte[scene.Pixels.Length];
            for (int i = 0; i < bright.Length; i++) bright[i] = (byte)Math.Min(255, scene.Pixels[i] * 1.3 + 20);

            var m = PatternMatcher.Find(new GrayImage(bright, W, H), templ);

            Assert.True(m.Found);
            Assert.True(m.Score > 0.9, $"점수가 낮다: {m.Score:F3}");
        }

        [Fact]
        public void 없는_패턴은_못_찾았다고_한다()
        {
            // 점수가 낮은데 '찾음'으로 넘기면 엉뚱한 자리로 스테이지가 이동한다.
            var scene = Scene(150, 120);

            var rnd = new Random(99);
            var noise = new byte[44 * 34];
            for (int i = 0; i < noise.Length; i++) noise[i] = (byte)rnd.Next(0, 255);

            var m = PatternMatcher.Find(scene, new GrayImage(noise, 44, 34),
                                        new PatternSearchOptions { MinScore = 0.7 });

            Assert.False(m.Found);
        }

        [Fact]
        public void 합격_점수는_설정한_값을_따른다()
        {
            var scene = Scene(150, 120);
            var templ = Template(scene, 150, 120);

            Assert.False(PatternMatcher.Find(scene, templ, new PatternSearchOptions { MinScore = 1.01 }).Found);
            Assert.True (PatternMatcher.Find(scene, templ, new PatternSearchOptions { MinScore = 0.5  }).Found);
        }

        [Fact]
        public void 서브픽셀까지_본다()
        {
            // 정수 픽셀로만 답하면 결과가 항상 딱 떨어진다. 보간이 도는지 확인한다.
            var scene = Scene(151, 120);
            var templ = Template(Scene(150, 120), 150, 120);

            var m = PatternMatcher.Find(scene, templ);

            Assert.True(m.Found);
            Assert.NotEqual(Math.Round(m.CenterX), m.CenterX);
        }

        [Fact]
        public void 검색_범위를_좁혀도_같은_답이다()
        {
            var scene = Scene(150, 120);
            var templ = Template(scene, 150, 120);

            var whole  = PatternMatcher.Find(scene, templ);
            var narrow = PatternMatcher.Find(scene, templ, new PatternSearchOptions
            {
                SearchRadiusPx = 30,
                ExpectedX      = whole.CenterX,
                ExpectedY      = whole.CenterY,
            });

            Assert.True(narrow.Found);
            Assert.Equal(whole.CenterX, narrow.CenterX, 1);
            Assert.Equal(whole.CenterY, narrow.CenterY, 1);
        }

        [Fact]
        public void 범위_밖으로_벗어나면_못_찾는다()
        {
            // 좁힌 범위 밖의 마크를 잡아 오면 '좁히기'가 아무 의미가 없다.
            var scene = Scene(320, 220);
            var templ = Template(Scene(150, 120), 150, 120);

            var m = PatternMatcher.Find(scene, templ, new PatternSearchOptions
            {
                SearchRadiusPx = 20, ExpectedX = 150, ExpectedY = 120, MinScore = 0.7,
            });

            Assert.False(m.Found);
        }

        [Fact]
        public void 패턴이_장면보다_크면_못_찾는다()
        {
            // 화면에서 고른 ROI 가 다음 프레임보다 클 수 있다. 예외로 화면이 죽으면 안 된다.
            var big = new GrayImage(new byte[500 * 400], 500, 400);

            Assert.False(PatternMatcher.Find(Scene(150, 120), big).Found);
        }

        [Fact]
        public void 축소_단계가_없어도_결과가_같다()
        {
            // 피라미드는 속도용이다. 켜고 끈 답이 다르면 둘 중 하나가 틀린 것이다.
            var scene = Scene(210, 160);
            var templ = Template(Scene(150, 120), 150, 120);

            var fast = PatternMatcher.Find(scene, templ, new PatternSearchOptions { PyramidLevels = 3 });
            var slow = PatternMatcher.Find(scene, templ, new PatternSearchOptions { PyramidLevels = 0 });

            Assert.True(fast.Found);
            Assert.True(slow.Found);
            Assert.Equal(slow.CenterX, fast.CenterX, 1);
            Assert.Equal(slow.CenterY, fast.CenterY, 1);
        }

        [Fact]
        public void 일도_돌아가도_버틴다()
        {
            // 1도까지는 견딘다는 전제로 각도 스윕을 넣지 않았다. 그 전제를 여기서 지킨다.
            var scene = Rotate(Scene(150, 120), 1.0);
            var templ = Template(Scene(150, 120), 150, 120);

            var m = PatternMatcher.Find(scene, templ, new PatternSearchOptions { MinScore = 0.7 });

            Assert.True(m.Found, "1도에서 놓쳤다 — 각도 스윕이 필요하다");
        }

        /// <summary>이미지 중심 기준 회전(최근접 이웃). 테스트용이라 화질은 따지지 않는다.</summary>
        private static GrayImage Rotate(GrayImage src, double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            double cx = src.Width / 2.0, cy = src.Height / 2.0;

            var outPix = new byte[src.Width * src.Height];
            for (int y = 0; y < src.Height; y++)
            {
                for (int x = 0; x < src.Width; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    int sx = (int)Math.Round(cx + dx * cos + dy * sin);
                    int sy = (int)Math.Round(cy - dx * sin + dy * cos);

                    outPix[y * src.Width + x] = sx >= 0 && sy >= 0 && sx < src.Width && sy < src.Height
                        ? src.Pixels[sy * src.Width + sx]
                        : (byte)100;
                }
            }
            return new GrayImage(outPix, src.Width, src.Height);
        }
    }
}
