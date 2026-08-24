using System;
using System.Collections.Generic;
using System.IO;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 인쇄용 데이터 세트 — LabVIEW 저장 버튼이 만들던 세 파일.
    ///
    /// <para>
    /// 원본(Rasterizer_Main.vi)의 저장은 그림 저장이 아니라 <b>RIP 을 굳히는 단계</b>였다.
    /// 저장하면 <c>ConvertIMG</c> 가 세 개를 남기고, 인쇄할 때 <c>6_WIZ_Print</c> 가 이 셋을
    /// 다시 읽어 PCC 로 내려보낸다.
    /// </para>
    /// <list type="table">
    ///   <item><term>*.bmp</term><description>토출 패턴 비트맵 — 사람이 눈으로 확인한다</description></item>
    ///   <item><term>POS.dat</term><description>노즐 X 위치 배열</description></item>
    ///   <item><term>Print_Para.dat</term><description>DPI·치수·헤드 수·비트뎁스</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>.dat 은 빅엔디안</b>이다. LabVIEW 의 Write To Binary 기본 바이트 순서가 그렇고,
    /// 읽는 쪽(6_WIZ_Print)이 그대로 기대한다. BMP 는 규격상 리틀엔디안이라 한 폴더 안에
    /// 두 바이트 순서가 섞인다 — 헷갈리기 쉬우니 여기서 한 번에 다룬다.
    /// </para>
    ///
    /// <para>
    /// <b>이 파일은 내보내기지 원본이 아니다.</b> 우리 쪽 진짜 기록은
    /// <see cref="PrintPatternFile"/>(<c>pattern.json</c> + <c>pattern.bin</c>)이다 —
    /// 스캔 스텝·패스·버려진 노즐처럼 .dat 에 자리가 없는 값이 거기 있다.
    /// 값이 어긋나면 <c>pattern.json</c> 쪽을 믿을 것.
    /// </para>
    /// </summary>
    public static class PrintDataSet
    {
        public const string NozzlePosFileName = "POS.dat";
        public const string PrintParaFileName = "Print_Para.dat";

        /// <summary>Print_Para.dat 의 내용. 필드 순서가 곧 파일 순서다 — 함부로 바꾸면 읽는 쪽이 깨진다.</summary>
        public sealed class PrintPara
        {
            public double DpiX { get; set; }
            public double DpiY { get; set; }

            /// <summary>패턴 가로 화소 수 = 컬럼(노즐) 수.</summary>
            public int WidthPx { get; set; }

            /// <summary>패턴 세로 화소 수 = 스텝 수.</summary>
            public int HeightPx { get; set; }

            /// <summary>인쇄물 실제 가로 [mm] — 노즐 X 범위에서 나온다.</summary>
            public double WidthMm { get; set; }

            /// <summary>인쇄물 실제 세로 [mm] — 스텝 수 × 스캔 스텝.</summary>
            public double HeightMm { get; set; }

            public int HeadCount { get; set; }
            public int NozzlePerHead { get; set; }

            /// <summary>화소 비트뎁스. 1 = 찍/안찍, 8 = 방울 크기 단계.</summary>
            public int BitsPerPixel { get; set; }

            /// <summary>헤드 팩 번호. 우리 장비는 팩 구분을 쓰지 않아 0 이다(자리만 지킨다).</summary>
            public int HeadPack { get; set; }

            /// <summary>
            /// 이음새 겹침. 원본은 "겹치는 노즐 수"였지만 우리는 노즐 X 좌표에서 겹침을 직접
            /// 구하므로 개수를 들고 있지 않다 — 켰으면 1, 껐으면 0 이다.
            /// </summary>
            public int Overlap { get; set; }

            /// <summary>간격 분할 수(Interval). 1 이면 노즐 간격 그대로.</summary>
            public int SubPixelX { get; set; } = 1;
            public int SubPixelY { get; set; } = 1;
        }

        /// <summary>Print_Para.dat 의 크기 [byte]. 필드를 늘리면 읽는 쪽도 같이 고쳐야 한다는 표시.</summary>
        public const int PrintParaSize = 8 * 4 + 4 * 9;   // F64 4개 + I32 9개 = 68

        // ── 세 파일 한 번에 ──────────────────────────────────────────────

        /// <summary>세 파일을 한 폴더에 쓴다. 반환: 만든 파일 경로(bmp, pos, para).</summary>
        public static (string Bmp, string Pos, string Para) Save(
            string folder, string baseName, PrintPattern pattern, PrintPara para, int dropLevels)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("폴더 경로가 비었습니다.", nameof(folder));
            if (string.IsNullOrWhiteSpace(baseName)) throw new ArgumentException("파일 이름이 비었습니다.", nameof(baseName));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (para == null) throw new ArgumentNullException(nameof(para));

            Directory.CreateDirectory(folder);

            string bmp  = Path.Combine(folder, baseName + ".bmp");
            string pos  = Path.Combine(folder, NozzlePosFileName);
            string parp = Path.Combine(folder, PrintParaFileName);

            var xs = new double[pattern.Columns.Count];
            for (int i = 0; i < xs.Length; i++) xs[i] = pattern.Columns[i].XUm;

            WritePatternBmp(bmp, pattern, dropLevels, para.DpiX, para.DpiY);
            WriteNozzlePos(pos, xs);
            WritePrintPara(parp, para);

            return (bmp, pos, parp);
        }

        // ── POS.dat ──────────────────────────────────────────────────────

        /// <summary>노즐 X 위치 [µm]. I32 개수 + F64 배열, 빅엔디안.</summary>
        public static void WriteNozzlePos(string path, IReadOnlyList<double> xUm)
        {
            if (xUm == null) throw new ArgumentNullException(nameof(xUm));

            using var fs = File.Create(path);
            WriteI32(fs, xUm.Count);
            foreach (double x in xUm) WriteF64(fs, x);
        }

        public static double[] ReadNozzlePos(string path)
        {
            using var fs = File.OpenRead(path);
            int n = ReadI32(fs);
            if (n < 0 || fs.Length != 4L + 8L * n)
                throw new InvalidDataException($"POS.dat 크기가 맞지 않습니다 — 개수 {n}, 파일 {fs.Length}바이트.");

            var xs = new double[n];
            for (int i = 0; i < n; i++) xs[i] = ReadF64(fs);
            return xs;
        }

        // ── Print_Para.dat ───────────────────────────────────────────────

        public static void WritePrintPara(string path, PrintPara p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            using var fs = File.Create(path);
            WriteF64(fs, p.DpiX);
            WriteF64(fs, p.DpiY);
            WriteI32(fs, p.WidthPx);
            WriteI32(fs, p.HeightPx);
            WriteF64(fs, p.WidthMm);
            WriteF64(fs, p.HeightMm);
            WriteI32(fs, p.HeadCount);
            WriteI32(fs, p.NozzlePerHead);
            WriteI32(fs, p.BitsPerPixel);
            WriteI32(fs, p.HeadPack);
            WriteI32(fs, p.Overlap);
            WriteI32(fs, p.SubPixelX);
            WriteI32(fs, p.SubPixelY);
        }

        public static PrintPara ReadPrintPara(string path)
        {
            using var fs = File.OpenRead(path);
            if (fs.Length != PrintParaSize)
                throw new InvalidDataException($"Print_Para.dat 는 {PrintParaSize}바이트여야 합니다 — 실제 {fs.Length}.");

            return new PrintPara
            {
                DpiX          = ReadF64(fs),
                DpiY          = ReadF64(fs),
                WidthPx       = ReadI32(fs),
                HeightPx      = ReadI32(fs),
                WidthMm       = ReadF64(fs),
                HeightMm      = ReadF64(fs),
                HeadCount     = ReadI32(fs),
                NozzlePerHead = ReadI32(fs),
                BitsPerPixel  = ReadI32(fs),
                HeadPack      = ReadI32(fs),
                Overlap       = ReadI32(fs),
                SubPixelX     = ReadI32(fs),
                SubPixelY     = ReadI32(fs),
            };
        }

        // ── 패턴 비트맵 ──────────────────────────────────────────────────

        /// <summary>
        /// 토출 패턴을 8비트 흑백 BMP 로 쓴다. 가로 = 노즐, 세로 = 스텝, <b>토출이 검정</b>이다.
        ///
        /// <para>System.Drawing 을 쓰지 않고 직접 쓴다 — 헤더가 30줄이면 되는데 그림 라이브러리를
        /// 인프라에 끌어들일 이유가 없고, 바이트를 그대로 검사할 수 있어야 형식을 믿을 수 있다.</para>
        /// </summary>
        public static void WritePatternBmp(string path, PrintPattern pattern, int dropLevels,
                                           double dpiX = 0, double dpiY = 0)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));

            int w = pattern.Nozzles, h = pattern.Steps;
            if (w <= 0 || h <= 0) throw new ArgumentException("패턴이 비었습니다.", nameof(pattern));

            int stride    = (w + 3) & ~3;              // 각 행은 4바이트 경계에 맞춘다
            int imageSize = stride * h;
            const int PaletteBytes = 256 * 4;
            const int HeaderBytes  = 14 + 40;
            int offset    = HeaderBytes + PaletteBytes;

            // 방울 단계 → 밝기. 단계가 클수록 잉크가 많으니 어둡다.
            int maxLevel = Math.Max(1, dropLevels - 1);
            var shade = new byte[maxLevel + 1];
            for (int L = 0; L <= maxLevel; L++)
                shade[L] = (byte)Math.Round(255.0 - 255.0 * L / maxLevel);

            using var fs = File.Create(path);

            // BITMAPFILEHEADER — BMP 는 규격이 리틀엔디안이다(.dat 과 반대).
            fs.WriteByte((byte)'B'); fs.WriteByte((byte)'M');
            WriteI32Le(fs, offset + imageSize);
            WriteI32Le(fs, 0);
            WriteI32Le(fs, offset);

            // BITMAPINFOHEADER
            WriteI32Le(fs, 40);
            WriteI32Le(fs, w);
            WriteI32Le(fs, h);                          // 양수 = 아래에서 위로 쌓인다
            WriteI16Le(fs, 1);
            WriteI16Le(fs, 8);
            WriteI32Le(fs, 0);                          // 무압축
            WriteI32Le(fs, imageSize);
            WriteI32Le(fs, PixelsPerMeter(dpiX));
            WriteI32Le(fs, PixelsPerMeter(dpiY));
            WriteI32Le(fs, 256);
            WriteI32Le(fs, 0);

            // 회색 팔레트 (B, G, R, 0)
            for (int i = 0; i < 256; i++)
            {
                fs.WriteByte((byte)i); fs.WriteByte((byte)i); fs.WriteByte((byte)i); fs.WriteByte(0);
            }

            // 화소 — BMP 는 아래 행부터 저장하므로 스텝 0 이 위로 오도록 거꾸로 쓴다.
            var row = new byte[stride];
            for (int s = h - 1; s >= 0; s--)
            {
                for (int c = 0; c < w; c++)
                {
                    int level = pattern.Levels[s, c];
                    row[c] = shade[level > maxLevel ? maxLevel : level];
                }
                for (int p = w; p < stride; p++) row[p] = 0xFF;   // 패딩은 흰색(비인쇄)
                fs.Write(row, 0, stride);
            }
        }

        /// <summary>
        /// 패턴 비트맵을 다시 방울 단계로 되짚는다. (<see cref="WritePatternBmp"/> 의 반대)
        ///
        /// <para>랩뷰가 남긴 폴더에는 <c>pattern.bin</c> 이 없고 비트맵만 있다. 밝기에서
        /// 단계를 되돌리는 것이라 <b>손실이 있다</b> — 저장할 때 쓴 단계 수를 알아야 맞게 나눈다.</para>
        /// </summary>
        /// <param name="path">8비트 흑백 BMP.</param>
        /// <param name="dropLevels">방울 단계 수(2 이상). 저장할 때와 같아야 한다.</param>
        /// <returns>[스텝, 노즐] 방울 단계.</returns>
        public static byte[,] ReadPatternBmp(string path, int dropLevels)
        {
            using var fs = File.OpenRead(path);

            if (NextByte(fs) != 'B' || NextByte(fs) != 'M')
                throw new InvalidDataException("BMP 가 아닙니다: " + Path.GetFileName(path));

            fs.Position = 10;
            int offset = ReadI32Le(fs);
            fs.Position = 14;
            int dibSize = ReadI32Le(fs);
            int w = ReadI32Le(fs);
            int h = ReadI32Le(fs);
            fs.Position = 28;
            int bits = ReadI16Le(fs);
            int compression = ReadI32Le(fs);

            if (dibSize < 40) throw new InvalidDataException("지원하지 않는 BMP 헤더입니다.");
            if (bits != 8)    throw new InvalidDataException($"8비트 흑백 BMP 만 읽습니다 — {bits}비트.");
            if (compression != 0) throw new InvalidDataException("압축된 BMP 는 읽지 않습니다.");
            if (w <= 0 || h == 0) throw new InvalidDataException($"BMP 크기가 이상합니다 — {w}×{h}.");

            // 높이가 음수면 위에서 아래로 저장된 것이다(우리 것은 양수 = 아래에서 위로).
            bool bottomUp = h > 0;
            int rows = Math.Abs(h);
            int stride = (w + 3) & ~3;

            var levels = new byte[rows, w];
            int maxLevel = Math.Max(1, dropLevels - 1);
            var row = new byte[stride];

            fs.Position = offset;
            for (int r = 0; r < rows; r++)
            {
                int read = 0;
                while (read < stride)
                {
                    int n = fs.Read(row, read, stride - read);
                    if (n <= 0) throw new EndOfStreamException("BMP 화소가 모자랍니다.");
                    read += n;
                }

                int s = bottomUp ? rows - 1 - r : r;
                for (int c = 0; c < w; c++)
                {
                    // 검정(0)이 최대 단계, 흰색(255)이 0 단계 — 쓸 때와 반대로 되돌린다.
                    int level = (int)Math.Round((255 - row[c]) / 255.0 * maxLevel);
                    levels[s, c] = (byte)Math.Clamp(level, 0, maxLevel);
                }
            }
            return levels;
        }

        private static int ReadI32Le(Stream s)
        {
            int v = 0;
            for (int i = 0; i < 4; i++) v |= NextByte(s) << (i * 8);
            return v;
        }

        private static int ReadI16Le(Stream s)
        {
            int lo = NextByte(s), hi = NextByte(s);
            return lo | (hi << 8);
        }

        /// <summary>DPI → 미터당 화소. 0 이면 0 — 뷰어가 알아서 기본값을 쓴다.</summary>
        private static int PixelsPerMeter(double dpi)
            => dpi > 0 ? (int)Math.Round(dpi / 0.0254) : 0;

        // ── 빅엔디안 (.dat) ──────────────────────────────────────────────

        private static void WriteI32(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
        }

        private static void WriteF64(Stream s, double v)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            for (int i = 7; i >= 0; i--) s.WriteByte((byte)(bits >> (i * 8)));
        }

        private static int ReadI32(Stream s)
        {
            int v = 0;
            for (int i = 0; i < 4; i++) v = (v << 8) | NextByte(s);
            return v;
        }

        private static double ReadF64(Stream s)
        {
            long bits = 0;
            for (int i = 0; i < 8; i++) bits = (bits << 8) | (uint)NextByte(s);
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static int NextByte(Stream s)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException("파일이 예상보다 짧습니다.");
            return b;
        }

        // ── 리틀엔디안 (.bmp) ────────────────────────────────────────────

        private static void WriteI32Le(Stream s, int v)
        {
            s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24));
        }

        private static void WriteI16Le(Stream s, int v)
        {
            s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
        }
    }
}
