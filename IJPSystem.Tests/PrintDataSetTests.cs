using IJPSystem.Platform.Infrastructure.Print;
using System;
using System.IO;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 인쇄 데이터 세트(.bmp · POS.dat · Print_Para.dat).
    ///
    /// <para>바이트 순서를 특히 파고든다 — .dat 은 빅엔디안, .bmp 는 리틀엔디안이라 한 폴더에
    /// 두 규칙이 섞인다. 쓰고 되읽는 검사만 하면 양쪽이 같이 틀려도 통과해 버리므로,
    /// 원시 바이트를 직접 확인하는 검사를 따로 둔다.</para>
    /// </summary>
    public class PrintDataSetTests : IDisposable
    {
        private readonly string _dir;

        public PrintDataSetTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ijp_pds_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string P(string name) => Path.Combine(_dir, name);

        /// <summary>스텝 × 노즐 패턴 하나. 값은 0..dropLevels-1.</summary>
        private static PrintPattern MakePattern(int steps, int nozzles, Func<int, int, byte> level)
        {
            var lv = new byte[steps, nozzles];
            for (int s = 0; s < steps; s++)
                for (int c = 0; c < nozzles; c++)
                    lv[s, c] = level(s, c);

            var cols = new NozzlePosition[nozzles];
            for (int c = 0; c < nozzles; c++)
                cols[c] = new NozzlePosition(c + 1, head: 0, row: 0, indexInRow: c, xUm: c * 84.7);

            return new PrintPattern { Levels = lv, Columns = cols, ScanStepUm = 42.3 };
        }

        // ── POS.dat ──────────────────────────────────────────────────────

        [Fact]
        public void 노즐위치는_개수와_값을_그대로_되읽는다()
        {
            var xs = new[] { 0.0, 84.7, 169.4, 254.1 };
            PrintDataSet.WriteNozzlePos(P("POS.dat"), xs);

            Assert.Equal(xs, PrintDataSet.ReadNozzlePos(P("POS.dat")));
        }

        [Fact]
        public void 노즐위치_파일은_빅엔디안이다()
        {
            // 1.0 의 IEEE754 비트는 0x3FF0000000000000 — 빅엔디안이면 0x3F 가 먼저 나온다.
            PrintDataSet.WriteNozzlePos(P("POS.dat"), new[] { 1.0 });
            byte[] raw = File.ReadAllBytes(P("POS.dat"));

            Assert.Equal(12, raw.Length);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, raw[0..4]);   // 개수 1
            Assert.Equal(0x3F, raw[4]);
            Assert.Equal(0xF0, raw[5]);
            Assert.Equal(0x00, raw[11]);
        }

        [Fact]
        public void 노즐이_없어도_빈_파일이_아니라_개수0_을_남긴다()
        {
            PrintDataSet.WriteNozzlePos(P("POS.dat"), Array.Empty<double>());

            Assert.Equal(4, new FileInfo(P("POS.dat")).Length);
            Assert.Empty(PrintDataSet.ReadNozzlePos(P("POS.dat")));
        }

        [Fact]
        public void 잘린_노즐위치_파일은_읽다가_막는다()
        {
            PrintDataSet.WriteNozzlePos(P("POS.dat"), new[] { 1.0, 2.0, 3.0 });
            byte[] raw = File.ReadAllBytes(P("POS.dat"));
            File.WriteAllBytes(P("POS.dat"), raw[0..(raw.Length - 3)]);   // 전송 중 끊긴 파일

            Assert.Throws<InvalidDataException>(() => PrintDataSet.ReadNozzlePos(P("POS.dat")));
        }

        // ── Print_Para.dat ───────────────────────────────────────────────

        [Fact]
        public void 인쇄파라미터는_모든_필드를_그대로_되읽는다()
        {
            var p = new PrintDataSet.PrintPara
            {
                DpiX = 600, DpiY = 1200,
                WidthPx = 2400, HeightPx = 5000,
                WidthMm = 203.2, HeightMm = 105.75,
                HeadCount = 2, NozzlePerHead = 800,
                BitsPerPixel = 8, HeadPack = 0, Overlap = 1,
                SubPixelX = 2, SubPixelY = 2,
            };
            PrintDataSet.WritePrintPara(P("Print_Para.dat"), p);

            var r = PrintDataSet.ReadPrintPara(P("Print_Para.dat"));
            Assert.Equal(600, r.DpiX);
            Assert.Equal(1200, r.DpiY);
            Assert.Equal(2400, r.WidthPx);
            Assert.Equal(5000, r.HeightPx);
            Assert.Equal(203.2, r.WidthMm);
            Assert.Equal(105.75, r.HeightMm);
            Assert.Equal(2, r.HeadCount);
            Assert.Equal(800, r.NozzlePerHead);
            Assert.Equal(8, r.BitsPerPixel);
            Assert.Equal(0, r.HeadPack);
            Assert.Equal(1, r.Overlap);
            Assert.Equal(2, r.SubPixelX);
            Assert.Equal(2, r.SubPixelY);
        }

        [Fact]
        public void 인쇄파라미터_크기는_68바이트로_고정이다()
        {
            // 크기가 바뀌면 읽는 쪽(6_WIZ_Print)이 통째로 어긋난다. 필드를 늘렸다면
            // 이 검사가 먼저 깨져야지, 현장에서 이상한 치수로 인쇄되면 안 된다.
            PrintDataSet.WritePrintPara(P("Print_Para.dat"), new PrintDataSet.PrintPara());

            Assert.Equal(68, new FileInfo(P("Print_Para.dat")).Length);
            Assert.Equal(68, PrintDataSet.PrintParaSize);
        }

        [Fact]
        public void 인쇄파라미터_정수도_빅엔디안이다()
        {
            PrintDataSet.WritePrintPara(P("Print_Para.dat"), new PrintDataSet.PrintPara { WidthPx = 1 });
            byte[] raw = File.ReadAllBytes(P("Print_Para.dat"));

            // DpiX(8) + DpiY(8) 다음이 WidthPx — 빅엔디안이면 0x00 0x00 0x00 0x01.
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, raw[16..20]);
        }

        [Fact]
        public void 크기가_다른_인쇄파라미터는_읽지_않는다()
        {
            File.WriteAllBytes(P("Print_Para.dat"), new byte[60]);

            Assert.Throws<InvalidDataException>(() => PrintDataSet.ReadPrintPara(P("Print_Para.dat")));
        }

        // ── 패턴 비트맵 ──────────────────────────────────────────────────

        [Fact]
        public void 비트맵_헤더가_규격대로다()
        {
            var pattern = MakePattern(3, 5, (s, c) => 0);
            PrintDataSet.WritePatternBmp(P("p.bmp"), pattern, dropLevels: 2, dpiX: 600, dpiY: 600);

            byte[] raw = File.ReadAllBytes(P("p.bmp"));
            Assert.Equal((byte)'B', raw[0]);
            Assert.Equal((byte)'M', raw[1]);
            Assert.Equal(raw.Length, BitConverter.ToInt32(raw, 2));      // 파일 크기
            Assert.Equal(14 + 40 + 1024, BitConverter.ToInt32(raw, 10)); // 화소 시작 위치
            Assert.Equal(40, BitConverter.ToInt32(raw, 14));             // DIB 헤더 크기
            Assert.Equal(5, BitConverter.ToInt32(raw, 18));              // 가로 = 노즐
            Assert.Equal(3, BitConverter.ToInt32(raw, 22));              // 세로 = 스텝
            Assert.Equal(8, BitConverter.ToInt16(raw, 28));              // 8비트
            Assert.Equal(0, BitConverter.ToInt32(raw, 30));              // 무압축
            Assert.Equal(23622, BitConverter.ToInt32(raw, 38));          // 600dpi ≈ 23622 px/m
            Assert.Equal(256, BitConverter.ToInt32(raw, 46));            // 팔레트 256
        }

        [Fact]
        public void 행은_4바이트로_채워지고_스텝0이_맨_위다()
        {
            // 노즐 5개 → 행 길이 8바이트(패딩 3). 스텝 0 만 전부 토출.
            var pattern = MakePattern(3, 5, (s, c) => (byte)(s == 0 ? 1 : 0));
            PrintDataSet.WritePatternBmp(P("p.bmp"), pattern, dropLevels: 2);

            byte[] raw = File.ReadAllBytes(P("p.bmp"));
            int off = BitConverter.ToInt32(raw, 10);
            Assert.Equal(off + 8 * 3, raw.Length);

            // BMP 는 아래 행부터 저장한다 → 마지막 행이 스텝 0(검정).
            byte[] lastRow = raw[(off + 8 * 2)..(off + 8 * 2 + 5)];
            Assert.All(lastRow, b => Assert.Equal(0, b));

            byte[] firstRow = raw[off..(off + 5)];   // 스텝 2 — 비인쇄(흰색)
            Assert.All(firstRow, b => Assert.Equal(255, b));
        }

        [Fact]
        public void 토출은_검정_비토출은_흰색이다()
        {
            var pattern = MakePattern(1, 2, (s, c) => (byte)c);   // 0, 1
            PrintDataSet.WritePatternBmp(P("p.bmp"), pattern, dropLevels: 2);

            byte[] raw = File.ReadAllBytes(P("p.bmp"));
            int off = BitConverter.ToInt32(raw, 10);
            Assert.Equal(255, raw[off]);       // 안 찍는다 → 흰색
            Assert.Equal(0, raw[off + 1]);     // 찍는다 → 검정
        }

        [Fact]
        public void 방울_단계가_많으면_중간_회색이_나온다()
        {
            var pattern = MakePattern(1, 3, (s, c) => (byte)c);   // 0, 1, 2
            PrintDataSet.WritePatternBmp(P("p.bmp"), pattern, dropLevels: 3);

            byte[] raw = File.ReadAllBytes(P("p.bmp"));
            int off = BitConverter.ToInt32(raw, 10);
            Assert.Equal(255, raw[off]);
            Assert.Equal(128, raw[off + 1]);   // 절반
            Assert.Equal(0, raw[off + 2]);
        }

        [Fact]
        public void 단계가_범위를_넘어도_비트맵은_깨지지_않는다()
        {
            // 하프톤이 어긋나 단계가 넘쳐도 파일은 나와야 한다 — 여기서 예외가 나면
            // 원인이 저장 쪽으로 보여 엉뚱한 데를 뒤지게 된다.
            var pattern = MakePattern(1, 2, (s, c) => (byte)(c == 0 ? 0 : 9));
            PrintDataSet.WritePatternBmp(P("p.bmp"), pattern, dropLevels: 2);

            byte[] raw = File.ReadAllBytes(P("p.bmp"));
            int off = BitConverter.ToInt32(raw, 10);
            Assert.Equal(0, raw[off + 1]);     // 최대 단계로 잘린다
        }

        [Fact]
        public void 빈_패턴은_비트맵을_만들지_않는다()
        {
            var empty = new PrintPattern();

            Assert.Throws<ArgumentException>(() => PrintDataSet.WritePatternBmp(P("p.bmp"), empty, 2));
        }

        // ── 세 파일 한 번에 ──────────────────────────────────────────────

        [Fact]
        public void 저장하면_세_파일이_한_폴더에_생긴다()
        {
            var pattern = MakePattern(4, 3, (s, c) => (byte)((s + c) % 2));
            var para = new PrintDataSet.PrintPara { DpiX = 600, DpiY = 600, WidthPx = 3, HeightPx = 4 };

            var (bmp, pos, parp) = PrintDataSet.Save(_dir, "Job1", pattern, para, dropLevels: 2);

            Assert.True(File.Exists(bmp));
            Assert.True(File.Exists(pos));
            Assert.True(File.Exists(parp));
            Assert.Equal("Job1.bmp", Path.GetFileName(bmp));
            Assert.Equal(PrintDataSet.NozzlePosFileName, Path.GetFileName(pos));
            Assert.Equal(PrintDataSet.PrintParaFileName, Path.GetFileName(parp));
        }

        [Fact]
        public void 저장된_노즐위치는_패턴의_컬럼과_같은_순서다()
        {
            // 순서가 어긋나면 그림이 가로로 뒤섞인다 — 파일만 봐서는 알 수 없는 종류의 사고다.
            var pattern = MakePattern(2, 4, (s, c) => 0);
            PrintDataSet.Save(_dir, "Job1", pattern, new PrintDataSet.PrintPara(), dropLevels: 2);

            double[] xs = PrintDataSet.ReadNozzlePos(Path.Combine(_dir, PrintDataSet.NozzlePosFileName));
            Assert.Equal(4, xs.Length);
            for (int c = 0; c < 4; c++)
                Assert.Equal(pattern.Columns[c].XUm, xs[c], 6);
        }

        [Fact]
        public void 폴더가_없으면_만들어서_저장한다()
        {
            string sub = Path.Combine(_dir, "IMG_TEMP", "260822_101500");
            var pattern = MakePattern(2, 2, (s, c) => 0);

            var (bmp, _, _) = PrintDataSet.Save(sub, "260822_101500", pattern,
                                                new PrintDataSet.PrintPara(), dropLevels: 2);

            Assert.True(File.Exists(bmp));
        }
    }
}
