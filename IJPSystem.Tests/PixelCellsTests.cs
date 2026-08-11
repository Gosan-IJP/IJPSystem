using System.Collections.Generic;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 편집 화면의 픽셀 켜기/끄기 계산.
    ///
    /// <para>
    /// 패턴은 "이 픽셀을 쏜다/안 쏜다"가 전부다. 화면에서 그은 선이 어느 픽셀을 켜는지가
    /// 배율이나 마우스 이벤트 간격에 따라 달라지면, 화면과 인쇄물이 어긋난다.
    /// 여기서는 그 대응이 확정적이라는 것을 고정한다.
    /// </para>
    /// </summary>
    public class PixelCellsTests
    {
        // 5x5mm @600dpi = 118px 을 화면 760 로 늘려 본 경우 — 한 칸 6.44
        private const double CellW = 760.0 / 118, CellH = 760.0 / 118;
        private const int W = 118, H = 118;

        [Fact]
        public void 점은_자기_칸에_들어간다()
        {
            Assert.Equal((0, 0), PixelCells.At(0, 0, CellW, CellH));
            Assert.Equal((0, 0), PixelCells.At(CellW - 0.001, 0, CellW, CellH));   // 경계 직전
            Assert.Equal((1, 0), PixelCells.At(CellW, 0, CellW, CellH));           // 경계는 다음 칸
            Assert.Equal((3, 7), PixelCells.At(CellW * 3.5, CellH * 7.5, CellW, CellH));
        }

        [Fact]
        public void 칸_원점은_경계에_정확히_앉는다()
        {
            // 이게 틀리면 그린 사각형이 반 픽셀 밀려, 저장할 때 이웃 픽셀까지 함께 켜진다.
            var (x, y) = PixelCells.Origin(10, 4, CellW, CellH);
            Assert.Equal(10 * CellW, x, 9);
            Assert.Equal(4 * CellH, y, 9);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 9)]
        [InlineData(4, 16)]
        public void 붓은_크기의_제곱만큼_칸을_덮는다(int size, int expected)
        {
            var cells = PixelCells.Brush(50, 50, size, W, H).ToList();
            Assert.Equal(expected, cells.Count);
            Assert.Equal(expected, cells.Distinct().Count());
        }

        [Fact]
        public void 붓은_이미지_밖으로_나가지_않는다()
        {
            // 모서리에서 잘리지 않으면 저장 때 배열 밖을 건드리거나 반대편에 점이 찍힌다.
            var cells = PixelCells.Brush(0, 0, 5, W, H).ToList();
            Assert.All(cells, c => Assert.InRange(c.X, 0, W - 1));
            Assert.All(cells, c => Assert.InRange(c.Y, 0, H - 1));
            Assert.Equal(9, cells.Count);      // 5x5 중 왼쪽 위 3x3 만 살아남는다
        }

        [Fact]
        public void 빠르게_그어도_선이_끊기지_않는다()
        {
            // 마우스 이동 이벤트는 띄엄띄엄 온다. 끝점만 찍으면 점선이 된다.
            var cells = PixelCells.Stroke(0, 0, CellW * 20, 0, CellW, CellH, 1, W, H);
            var xs = cells.Select(c => c.X).OrderBy(v => v).ToList();

            Assert.Equal(Enumerable.Range(0, 21), xs);      // 0~20 이 하나도 안 빠진다
            Assert.All(cells, c => Assert.Equal(0, c.Y));
        }

        [Fact]
        public void 대각선도_끊기지_않는다()
        {
            var cells = PixelCells.Stroke(0, 0, CellW * 30, CellH * 30, CellW, CellH, 1, W, H);
            var set = new HashSet<(int, int)>(cells);

            // 대각선 위의 칸은 전부 켜져 있어야 한다
            for (int i = 0; i <= 30; i++) Assert.Contains((i, i), set);
        }

        [Fact]
        public void 같은_칸을_두_번_돌려주지_않는다()
        {
            // 중복이 나오면 획 하나에 사각형이 수만 개 쌓여 화면이 멎는다.
            var cells = PixelCells.Stroke(0, 0, CellW * 40, CellH * 40, CellW, CellH, 5, W, H);
            Assert.Equal(cells.Count, cells.Distinct().Count());
        }

        [Fact]
        public void 이어_그은_획도_중복되지_않는다()
        {
            var seen = new HashSet<long>();
            var a = PixelCells.Stroke(0, 0, CellW * 10, 0, CellW, CellH, 1, W, H, seen);
            var b = PixelCells.Stroke(CellW * 10, 0, 0, 0, CellW, CellH, 1, W, H, seen);   // 같은 길 되돌아오기

            Assert.Equal(11, a.Count);
            Assert.Empty(b);          // 이미 켠 칸뿐이다
        }

        [Fact]
        public void 제자리_찍기도_한_칸을_켠다()
        {
            // 점 하나 찍기(누르고 바로 떼기)가 아무것도 안 하면 지우개로 한 픽셀을 못 지운다.
            var cells = PixelCells.Stroke(CellW * 5, CellH * 5, CellW * 5, CellH * 5, CellW, CellH, 1, W, H);
            Assert.Single(cells);
            Assert.Equal((5, 5), cells[0]);
        }

        [Fact]
        public void 큰_캔버스에서도_칸이_이미지_픽셀과_1대1이다()
        {
            // 200x200mm @600dpi = 4724px. 화면은 760 이라 한 칸이 0.16 밖에 안 되지만,
            // 저장할 때 6.2배로 늘리면 칸 하나가 정확히 픽셀 하나가 된다.
            const int big = 4724;
            double cw = 760.0 / big;

            var cells = PixelCells.Stroke(0, 0, cw * 100, 0, cw, cw, 1, big, big);
            Assert.Equal(101, cells.Count);

            var (x, _) = PixelCells.Origin(100, 0, cw, cw);
            Assert.Equal(100.0, x / cw, 9);            // 100번째 픽셀 경계에 정확히
        }
    }
}
