using IJPSystem.Platform.Infrastructure.Print;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// RIP — 이미지를 노즐 발사 지도로 바꾸는 단계. 여기서 농도가 틀어지면 인쇄물이
    /// 통째로 진하거나 연해지고, 매핑이 틀어지면 그림이 밀린다.
    /// </summary>
    public class HalftoneTests
    {
        /// <summary>
        /// 오차 확산의 존재 이유 — 반올림이면 사라질 농도가 점 배치로 남아야 한다.
        /// 중간 회색(128)을 흑백 2단계로 낮추면 절반쯤은 찍혀야 한다.
        /// </summary>
        [Fact]
        public void 중간회색을_2단계로_낮추면_평균_농도가_유지된다()
        {
            var gray = Fill(32, 32, 128);

            var lv = Halftone.ErrorDiffuse(gray, levels: 2);

            double on = Count(lv, v => v == 1) / 1024.0;
            Assert.InRange(on, 0.45, 0.55);
        }

        /// <summary>반올림이라면 128 → 전부 0 이거나 전부 1 이 된다. 그게 아님을 못 박는다.</summary>
        [Fact]
        public void 중간회색은_전부_같은_값이_되지_않는다()
        {
            var lv = Halftone.ErrorDiffuse(Fill(16, 16, 128), levels: 2);

            Assert.Contains(lv.Cast<byte>(), v => v == 0);
            Assert.Contains(lv.Cast<byte>(), v => v == 1);
        }

        [Fact]
        public void 완전한_흰색과_검정은_그대로_간다()
        {
            Assert.All(Halftone.ErrorDiffuse(Fill(8, 8, 0), 4).Cast<byte>(),   v => Assert.Equal(0, v));
            Assert.All(Halftone.ErrorDiffuse(Fill(8, 8, 255), 4).Cast<byte>(), v => Assert.Equal(3, v));
        }

        /// <summary>단계 수를 넘는 값이 나오면 헤드가 못 내는 방울을 요구하게 된다.</summary>
        [Fact]
        public void 결과는_항상_단계_범위_안이다()
        {
            var rnd = new Random(1234);
            var gray = new byte[24, 24];
            for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++) gray[y, x] = (byte)rnd.Next(256);

            var lv = Halftone.ErrorDiffuse(gray, levels: 4);

            Assert.All(lv.Cast<byte>(), v => Assert.InRange(v, (byte)0, (byte)3));
        }

        /// <summary>4단계면 중간 농도가 중간 단계로 — 2단계보다 점이 덜 튄다.</summary>
        [Fact]
        public void 다단계에서는_중간농도가_중간단계로_간다()
        {
            var lv = Halftone.ErrorDiffuse(Fill(16, 16, 85), levels: 4);   // 85/255 = 1/3 → 단계 1

            Assert.All(lv.Cast<byte>(), v => Assert.Equal(1, v));
        }

        [Fact]
        public void 같은_입력은_같은_결과를_준다()
        {
            var gray = Fill(16, 16, 100);

            Assert.Equal(Halftone.ErrorDiffuse(gray, 3).Cast<byte>(),
                         Halftone.ErrorDiffuse(gray, 3).Cast<byte>());
        }

        [Fact]
        public void 단계가_2보다_작으면_거부한다()
            => Assert.Throws<ArgumentOutOfRangeException>(() => Halftone.ErrorDiffuse(Fill(4, 4, 0), 1));

        private static byte[,] Fill(int h, int w, byte v)
        {
            var a = new byte[h, w];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y, x] = v;
            return a;
        }

        private static int Count(byte[,] a, Func<byte, bool> p) => a.Cast<byte>().Count(p);
    }

    public class PrintPatternBuilderTests
    {
        private const double RowOffsetUm = 84.7;
        private static NozzleLayout Layout(int nozzlesPerRow = 4, int heads = 1, double headPitch = 0)
            => new(3, nozzlesPerRow, RowOffsetUm * 3, RowOffsetUm, heads, headPitch);

        private static byte[,] Solid(int h, int w, byte v = 255)
        {
            var a = new byte[h, w];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y, x] = v;
            return a;
        }

        private static RipSettings Rip(double scanStepUm = RowOffsetUm) =>
            new() { DropLevels = 2, ScanStepUm = scanStepUm };

        /// <summary>컬럼 수 = 사용 노즐 수, 컬럼 순서 = X 순.</summary>
        [Fact]
        public void 컬럼은_사용노즐만_X순으로_만들어진다()
        {
            var layout = Layout();

            var pat = PrintPatternBuilder.Build(Solid(10, 10), RowOffsetUm, RowOffsetUm,
                                                layout, new[] { 5, 1, 3 }, Rip(), out var ignored);

            Assert.Empty(ignored);
            Assert.Equal(new[] { 1, 3, 5 }, pat.Columns.Select(c => c.Number));
            Assert.Equal(3, pat.Nozzles);
        }

        /// <summary>
        /// 안 쓰는 노즐이 있어도 <b>그림은 제자리</b>여야 한다.
        /// 노즐 5번은 균일 격자로 나눴다면 3번째 칸이지만, 실제로는 X=338.8µm 자리다.
        /// </summary>
        [Fact]
        public void 빠진_노즐이_있어도_남은_노즐은_자기_X의_픽셀을_읽는다()
        {
            var layout = Layout();
            // 가로로 밝기가 계단인 이미지 — 픽셀 1개 = 84.7µm 이므로 x번째 픽셀 = 노즐 x+1
            var gray = new byte[1, 8];
            for (int x = 0; x < 8; x++) gray[0, x] = (byte)(x * 30);

            var columns = layout.SortByX(new[] { 1, 5 }, out _);
            var sampled = PrintPatternBuilder.SampleToNozzleGrid(
                gray, RowOffsetUm, RowOffsetUm, columns,
                originXUm: 0, scanStepUm: RowOffsetUm, blendSeams: false);

            // 노즐1 → X=0 → 픽셀0(=0), 노즐5 → X=338.8 → 픽셀4(=120)
            Assert.Equal(2, columns.Count);
            Assert.Equal(0,   sampled[0, 0]);
            Assert.Equal(120, sampled[0, 1]);
        }

        /// <summary>세로 스텝은 이미지 높이를 스캔 스텝으로 나눈 만큼.</summary>
        [Fact]
        public void 스텝수는_이미지_높이에서_나온다()
        {
            var layout = Layout();

            var pat = PrintPatternBuilder.Build(Solid(10, 10), RowOffsetUm, RowOffsetUm,
                                                layout, new[] { 1, 2 }, Rip(scanStepUm: RowOffsetUm),
                                                out _);

            Assert.Equal(10, pat.Steps);
        }

        /// <summary>스캔 스텝을 반으로 줄이면 같은 그림을 두 배 촘촘히 찍는다.</summary>
        [Fact]
        public void 스캔스텝이_절반이면_스텝수는_두배()
        {
            var layout = Layout();

            var pat = PrintPatternBuilder.Build(Solid(10, 10), RowOffsetUm, RowOffsetUm,
                                                layout, new[] { 1 }, Rip(scanStepUm: RowOffsetUm / 2),
                                                out _);

            Assert.Equal(20, pat.Steps);
        }

        /// <summary>이미지 밖에 있는 노즐은 쏘지 않는다 — 글라스 밖에 잉크를 뿌리면 안 된다.</summary>
        [Fact]
        public void 이미지_밖_노즐은_쏘지_않는다()
        {
            var layout = Layout(nozzlesPerRow: 4);          // 노즐 12개, 폭 ≈ 931µm
            var gray = Solid(4, 3);                          // 폭 3픽셀 = 254.1µm 까지만

            var pat = PrintPatternBuilder.Build(gray, RowOffsetUm, RowOffsetUm, layout,
                                                Enumerable.Range(1, 12), Rip(), out _);

            // X ≥ 254.1µm 인 노즐(4번 이상)은 전부 0
            for (int c = 0; c < pat.Nozzles; c++)
            {
                bool inside = pat.Columns[c].XUm < 3 * RowOffsetUm;
                bool fired  = Enumerable.Range(0, pat.Steps).Any(s => pat.Levels[s, c] > 0);
                if (!inside) Assert.False(fired, $"{pat.Columns[c].Number}번은 이미지 밖인데 쐈다");
            }
        }

        /// <summary>사용 노즐이 없으면 빈 지도 — 예외가 아니라 아무것도 안 쏘는 것이 맞다.</summary>
        [Fact]
        public void 사용노즐이_없으면_빈_지도()
        {
            var pat = PrintPatternBuilder.Build(Solid(8, 8), RowOffsetUm, RowOffsetUm,
                                                Layout(), Array.Empty<int>(), Rip(), out _);

            Assert.Equal(0, pat.Nozzles);
            Assert.Equal(0, pat.Steps);
        }
    }

    /// <summary>헤드 이음새 — 겹치는 구간에서 두 헤드 가중치의 합이 1 이어야 한다.</summary>
    public class SeamWeightTests
    {
        private static NozzlePosition N(int number, int head, double x) => new(number, head, 0, 0, x);

        [Fact]
        public void 헤드가_하나면_전부_1()
        {
            var cols = new List<NozzlePosition> { N(1, 0, 0), N(2, 0, 100), N(3, 0, 200) };

            var w = PrintPatternBuilder.SeamWeights(cols, blend: true);

            Assert.All(w, v => Assert.Equal(1.0, v, 6));
        }

        [Fact]
        public void 헤드가_안_겹치면_전부_1()
        {
            var cols = new List<NozzlePosition> { N(1, 0, 0), N(2, 0, 100), N(3, 1, 200), N(4, 1, 300) };

            var w = PrintPatternBuilder.SeamWeights(cols, blend: true);

            Assert.All(w, v => Assert.Equal(1.0, v, 6));
        }

        /// <summary>겹치는 구간의 같은 X 에서 두 헤드 가중치를 더하면 1 — 잉크가 두 배가 되지 않는다.</summary>
        [Fact]
        public void 겹치는_구간에서_두_헤드_가중치의_합은_1()
        {
            // 헤드0: 0~300, 헤드1: 100~400 → 100~300 이 겹침. 같은 X 에 양쪽 노즐을 둔다.
            var cols = new List<NozzlePosition>
            {
                N(1, 0, 0), N(2, 0, 100), N(3, 0, 200), N(4, 0, 300),
                N(5, 1, 100), N(6, 1, 200), N(7, 1, 300), N(8, 1, 400),
            };
            var sorted = cols.OrderBy(c => c.XUm).ThenBy(c => c.Number).ToList();

            var w = PrintPatternBuilder.SeamWeights(sorted, blend: true);

            foreach (var g in sorted.Select((c, i) => (c, i))
                                    .GroupBy(t => t.c.XUm)
                                    .Where(g => g.Select(t => t.c.Head).Distinct().Count() == 2))
                Assert.Equal(1.0, g.Sum(t => w[t.i]), 6);
        }

        [Fact]
        public void 끄면_섞지_않는다()
        {
            var cols = new List<NozzlePosition> { N(1, 0, 0), N(2, 0, 200), N(3, 1, 100), N(4, 1, 300) }
                       .OrderBy(c => c.XUm).ToList();

            Assert.All(PrintPatternBuilder.SeamWeights(cols, blend: false), v => Assert.Equal(1.0, v, 6));
        }
    }
}
