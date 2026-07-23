using IJPSystem.Platform.Application.Printing;
using System;
using System.Drawing;
using System.IO;
using netDxf;
using netDxf.Entities;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// DXF → 비트맵 변환 검증 — 하드웨어 없이 확인 가능.
    /// 핵심: 실측 스케일(1픽셀=1드롭@DPI)이 지켜지는가, 채움이 되는가, 좌표계가 맞는가.
    /// </summary>
    public class DxfToBitmapTests : IDisposable
    {
        private readonly string _dir;

        public DxfToBitmapTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "IJP_DxfTest_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>10mm×10mm 닫힌 사각형 DXF 를 만든다.</summary>
        private string MakeSquareDxf(double sizeMm = 10)
        {
            var doc = new DxfDocument();
            var pl = new Polyline2D(new[]
            {
                new Polyline2DVertex(0, 0),
                new Polyline2DVertex(sizeMm, 0),
                new Polyline2DVertex(sizeMm, sizeMm),
                new Polyline2DVertex(0, sizeMm),
            }, isClosed: true);
            doc.Entities.Add(pl);

            string path = Path.Combine(_dir, "square.dxf");
            doc.Save(path);
            return path;
        }

        // ── 스케일: 1픽셀 = 1드롭 @ DPI ───────────────────────────────────────
        [Fact]
        public void SquareSize_MatchesDpiScale()
        {
            // 10mm 사각형 @ 600DPI, 여백 0 → 10mm × (600/25.4) ≈ 236px
            string dxf = MakeSquareDxf(10);
            string outp = Path.Combine(_dir, "out.png");

            var r = DxfToBitmap.Convert(dxf, outp, new DxfRasterOptions { Dpi = 600, MarginMm = 0, UnitToMm = 1.0 });

            Assert.True(r.Success, r.Message);
            int expected = (int)Math.Ceiling(10.0 * 600 / 25.4);   // ≈237
            Assert.InRange(r.WidthPx, expected - 2, expected + 2);
            Assert.InRange(r.HeightPx, expected - 2, expected + 2);
        }

        [Fact]
        public void HalvedDpi_HalvesPixelSize()
        {
            string dxf = MakeSquareDxf(10);

            var hi = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "hi.png"),
                new DxfRasterOptions { Dpi = 600, MarginMm = 0 });
            var lo = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "lo.png"),
                new DxfRasterOptions { Dpi = 300, MarginMm = 0 });

            Assert.True(hi.Success && lo.Success);
            // 절반 DPI → 절반 픽셀(반올림 오차 ±2)
            Assert.InRange(hi.WidthPx - lo.WidthPx * 2, -3, 3);
        }

        [Fact]
        public void MarginAddsPixels()
        {
            string dxf = MakeSquareDxf(10);
            var noMargin = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "n.png"),
                new DxfRasterOptions { Dpi = 600, MarginMm = 0 });
            var margin = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "m.png"),
                new DxfRasterOptions { Dpi = 600, MarginMm = 2 });

            Assert.True(margin.WidthPx > noMargin.WidthPx);
        }

        // ── 채움 ──────────────────────────────────────────────────────────────
        [Fact]
        public void FilledSquare_HasInkInInterior()
        {
            string dxf = MakeSquareDxf(10);
            string outp = Path.Combine(_dir, "filled.png");
            DxfToBitmap.Convert(dxf, outp, new DxfRasterOptions { Dpi = 300, MarginMm = 1, Fill = true });

            using var bmp = new Bitmap(outp);
            // 중앙 픽셀이 잉크(검정)여야 한다.
            Color center = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            Assert.True(center.R < 128 && center.G < 128 && center.B < 128,
                $"채움 모드에서 중앙이 잉크여야 하는데 {center} 였다");
        }

        [Fact]
        public void OutlineOnly_HasBlankInterior()
        {
            string dxf = MakeSquareDxf(10);
            string outp = Path.Combine(_dir, "outline.png");
            DxfToBitmap.Convert(dxf, outp, new DxfRasterOptions { Dpi = 300, MarginMm = 1, Fill = false });

            using var bmp = new Bitmap(outp);
            Color center = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            Assert.True(center.R > 128, $"외곽선 모드에서 중앙은 배경이어야 하는데 {center} 였다");
        }

        // ── 실패 경로 ─────────────────────────────────────────────────────────
        [Fact]
        public void MissingFile_FailsGracefully()
        {
            var r = DxfToBitmap.Convert(Path.Combine(_dir, "nope.dxf"), Path.Combine(_dir, "o.png"));
            Assert.False(r.Success);
            Assert.Contains("찾을 수 없", r.Message);
        }

        [Fact]
        public void EmptyDrawing_FailsWithReason()
        {
            var doc = new DxfDocument();
            string dxf = Path.Combine(_dir, "empty.dxf");
            doc.Save(dxf);

            var r = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "o.png"));
            Assert.False(r.Success);
        }

        [Fact]
        public void OversizedOutput_IsRejected()
        {
            // 1000mm @ 4800DPI = 189000px > 상한 → 거부(거대 이미지 방지)
            string dxf = MakeSquareDxf(1000);
            var r = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "big.png"),
                new DxfRasterOptions { Dpi = 4800, MaxDimensionPx = 20000 });

            Assert.False(r.Success);
            Assert.Contains("상한", r.Message);
        }

        [Fact]
        public void MixedEntities_AllCounted()
        {
            var doc = new DxfDocument();
            doc.Entities.Add(new Circle(new Vector3(10, 10, 0), 5));
            doc.Entities.Add(new Line(new Vector3(0, 0, 0), new Vector3(20, 20, 0)));
            doc.Entities.Add(new Arc(new Vector3(5, 5, 0), 3, 0, 90));
            string dxf = Path.Combine(_dir, "mixed.dxf");
            doc.Save(dxf);

            var r = DxfToBitmap.Convert(dxf, Path.Combine(_dir, "mixed.png"),
                new DxfRasterOptions { Dpi = 300, MarginMm = 1 });

            Assert.True(r.Success, r.Message);
            Assert.Equal(3, r.EntityCount);
            Assert.True(File.Exists(r.OutputPath));
        }
    }
}
