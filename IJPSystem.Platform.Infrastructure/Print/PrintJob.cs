using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>패턴을 어디서 얻었는가. 값이 다르면 믿을 수 있는 정도가 다르다.</summary>
    public enum PatternSource
    {
        /// <summary><c>pattern.bin</c> — 변환이 남긴 원본. 방울 단계·스캔 스텝·노즐 구성이 그대로다.</summary>
        PatternFile,

        /// <summary><c>*.bmp</c> — 밝기에서 방울 단계를 되짚었다. 랩뷰가 남긴 폴더를 읽을 때 쓴다.</summary>
        Bitmap,
    }

    /// <summary>
    /// 디스크에서 읽어 들인 인쇄 데이터 한 벌. (랩뷰 <c>6_WIZ_Print</c> 의 "Load Print data")
    ///
    /// <para>
    /// <b>Meteor 가 파일을 읽는 게 아니다.</b> PCC 는 PC 의 파일시스템을 모른다. PC 가 읽어서
    /// 메모리로 올려 보낸다 — 파일 → PC → PCC 순서다. 그래서 읽기(이 클래스)와 전송
    /// (<see cref="IPrintDataDownloader"/>)이 갈라져 있다.
    /// </para>
    /// </summary>
    public sealed class PrintJob
    {
        public string Folder { get; init; } = "";
        public string? BmpPath { get; init; }
        public string NozzlePosPath { get; init; } = "";
        public string PrintParaPath { get; init; } = "";

        public PrintDataSet.PrintPara Para { get; init; } = new();

        /// <summary>노즐 X 위치 [µm]. 컬럼 순서와 1:1 이다.</summary>
        public IReadOnlyList<double> NozzleXUm { get; init; } = Array.Empty<double>();

        /// <summary>발사 지도. 이것이 실제로 PCC 로 올라간다.</summary>
        public PrintPattern Pattern { get; init; } = new();

        public PatternSource Source { get; init; }

        public int Steps => Pattern.Steps;
        public int Nozzles => Pattern.Nozzles;

        /// <summary>방울 수 — 0 이면 빈 그림을 인쇄하려는 것이다.</summary>
        public long DropCount
        {
            get
            {
                long n = 0;
                for (int s = 0; s < Pattern.Steps; s++)
                    for (int c = 0; c < Pattern.Nozzles; c++)
                        if (Pattern.Levels[s, c] > 0) n++;
                return n;
            }
        }

        public override string ToString() =>
            $"{Path.GetFileName(Folder)} — {Steps}스텝 × {Nozzles}노즐 " +
            $"({Para.WidthMm:F1}×{Para.HeightMm:F1}mm, {Para.DpiX:F0}dpi)";
    }

    /// <summary>
    /// 저장된 인쇄 데이터 하나의 요약. <b>패턴은 읽지 않는다</b> —
    /// 목록을 만들자고 수십 MB 를 읽으면 화면이 멈춘다. <c>Print_Para.dat</c> 68바이트면 충분하다.
    /// </summary>
    public sealed record PrintDataEntry(
        string Folder, string Name, DateTime SavedAt,
        int Nozzles, int Steps, double WidthMm, double HeightMm)
    {
        /// <summary>목록에 뜨는 한 줄.</summary>
        public string Label =>
            $"{Name}   {SavedAt:MM-dd HH:mm}   {Steps}×{Nozzles}   {WidthMm:F0}×{HeightMm:F0}mm";
    }

    /// <summary>
    /// 저장해 둔 인쇄 데이터를 다시 읽는다 — <see cref="PrintDataSet"/> 의 반대편.
    ///
    /// <para>
    /// 저장과 인쇄는 갈라져 있다. 오늘 저장한 것을 내일 불러 여러 번 찍을 수 있어야 하고,
    /// 그래서 폴더 하나만으로 인쇄가 되살아나야 한다.
    /// </para>
    /// </summary>
    public static class PrintJobFile
    {
        /// <summary>
        /// 폴더에서 인쇄 데이터를 읽는다.
        ///
        /// <para>
        /// <c>pattern.bin</c> 이 있으면 그것을 쓴다 — 방울 단계와 스캔 스텝이 손실 없이 들어 있다.
        /// 없으면 <c>*.bmp</c> 에서 밝기로 되짚는다(랩뷰가 남긴 폴더). 이때 스캔 스텝은 알 수 없어
        /// <c>Print_Para.dat</c> 의 세로 치수에서 나눠 구한다.
        /// </para>
        /// </summary>
        /// <param name="folder">저장 폴더.</param>
        /// <param name="bmpFileName">쓸 비트맵 이름. 없으면 폴더의 첫 .bmp.</param>
        public static PrintJob Load(string folder, string? bmpFileName = null)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("폴더 경로가 비었습니다.", nameof(folder));
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("인쇄 데이터 폴더가 없습니다: " + folder);

            string posPath  = Path.Combine(folder, PrintDataSet.NozzlePosFileName);
            string paraPath = Path.Combine(folder, PrintDataSet.PrintParaFileName);

            if (!File.Exists(posPath))
                throw new FileNotFoundException($"{PrintDataSet.NozzlePosFileName} 가 없습니다 — 저장이 끝나지 않은 폴더입니다.", posPath);
            if (!File.Exists(paraPath))
                throw new FileNotFoundException($"{PrintDataSet.PrintParaFileName} 가 없습니다 — 저장이 끝나지 않은 폴더입니다.", paraPath);

            double[] xs = PrintDataSet.ReadNozzlePos(posPath);
            var para    = PrintDataSet.ReadPrintPara(paraPath);

            string? bmp = bmpFileName != null
                ? Path.Combine(folder, bmpFileName)
                : Directory.EnumerateFiles(folder, "*.bmp").OrderBy(f => f).FirstOrDefault();

            PrintPattern pattern;
            PatternSource source;

            string patternBin = Path.Combine(folder, PrintPatternFile.DataFileName);
            if (File.Exists(patternBin))
            {
                pattern = PrintPatternFile.Load(folder).Pattern;
                source  = PatternSource.PatternFile;
            }
            else if (bmp != null && File.Exists(bmp))
            {
                // 세로 치수 ÷ 스텝 수 = 스텝 하나의 이동량. 비트맵에는 이 값이 없다.
                var levels = PrintDataSet.ReadPatternBmp(bmp, Math.Max(2, LevelsOf(para.BitsPerPixel)));
                int steps  = levels.GetLength(0);
                double scanStepUm = steps > 0 && para.HeightMm > 0 ? para.HeightMm * 1000.0 / steps : 0;

                pattern = new PrintPattern
                {
                    Levels     = levels,
                    Columns    = ColumnsFrom(xs, levels.GetLength(1)),
                    ScanStepUm = scanStepUm,
                };
                source = PatternSource.Bitmap;
            }
            else
            {
                throw new FileNotFoundException(
                    "패턴이 없습니다 — pattern.bin 도 .bmp 도 폴더에 없습니다.", patternBin);
            }

            return new PrintJob
            {
                Folder        = folder,
                BmpPath       = bmp,
                NozzlePosPath = posPath,
                PrintParaPath = paraPath,
                Para          = para,
                NozzleXUm     = xs,
                Pattern       = pattern,
                Source        = source,
            };
        }

        /// <summary>
        /// 최근 저장된 인쇄 데이터 목록 — 새것부터.
        ///
        /// <para>
        /// 세 파일이 다 있는 폴더만 후보다 — 저장하다 만 폴더를 골라 "왜 안 되지"가 되면 안 된다.
        /// 순서는 <b>파일 시각</b>으로 정한다. 폴더 이름도 시각이지만 이름은 사람이 바꿀 수 있고,
        /// 내용이 언제 쓰였는지는 안 바뀐다.
        /// </para>
        /// </summary>
        /// <param name="root">패턴 폴더들이 쌓이는 자리(예: <c>…\GS_Inkjet\IMG_TEMP</c>).</param>
        /// <param name="max">최대 개수.</param>
        public static IReadOnlyList<PrintDataEntry> FindRecent(string root, int max = 5)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || max <= 0)
                return Array.Empty<PrintDataEntry>();

            var found = new List<PrintDataEntry>();

            // 루트 자체가 한 벌일 수도 있다(폴더를 직접 지정해 저장한 경우).
            foreach (string dir in Enumerable.Repeat(root, 1).Concat(Directory.EnumerateDirectories(root)))
            {
                string para = Path.Combine(dir, PrintDataSet.PrintParaFileName);
                if (!File.Exists(para)) continue;
                if (!File.Exists(Path.Combine(dir, PrintDataSet.NozzlePosFileName))) continue;

                // 패턴이 없으면 읽어도 실패한다 — 후보에서 뺀다.
                bool hasPattern = File.Exists(Path.Combine(dir, PrintPatternFile.DataFileName))
                               || Directory.EnumerateFiles(dir, "*.bmp").Any();
                if (!hasPattern) continue;

                // 요약을 못 읽는 폴더는 목록에서 뺀다 — 눌러 봐야 실패한다.
                PrintDataSet.PrintPara p;
                try { p = PrintDataSet.ReadPrintPara(para); }
                catch { continue; }

                string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = dir;

                found.Add(new PrintDataEntry(dir, name, File.GetLastWriteTime(para),
                                             p.WidthPx, p.HeightPx, p.WidthMm, p.HeightMm));
            }

            return found.OrderByDescending(e => e.SavedAt).Take(max).ToList();
        }

        /// <summary>
        /// 가장 최근에 저장된 인쇄 데이터 폴더.
        ///
        /// <para>인쇄 직전에 사람이 폴더를 뒤지게 하면 엉뚱한 날짜를 고르기 쉽다.
        /// 방금 저장한 것을 그대로 불러오는 쪽이 실수가 적다.</para>
        /// </summary>
        /// <returns>폴더 경로. 쓸 만한 것이 없으면 null.</returns>
        public static string? FindLatest(string root) => FindRecent(root, 1).FirstOrDefault()?.Folder;

        /// <summary>비트뎁스 → 방울 단계 수. 1비트면 2단계, 8비트면 저장한 단계를 모르므로 256 으로 본다.</summary>
        private static int LevelsOf(int bitsPerPixel) => bitsPerPixel <= 1 ? 2 : 256;

        /// <summary>POS.dat 의 X 로 컬럼을 만든다. 개수가 어긋나면 짧은 쪽에 맞춘다.</summary>
        private static NozzlePosition[] ColumnsFrom(IReadOnlyList<double> xs, int count)
        {
            var cols = new NozzlePosition[count];
            for (int c = 0; c < count; c++)
            {
                double x = c < xs.Count ? xs[c] : 0;
                cols[c] = new NozzlePosition(c + 1, head: 0, row: 0, indexInRow: c, xUm: x);
            }
            return cols;
        }

        /// <summary>
        /// 읽은 것이 서로 앞뒤가 맞는가. 전송하기 <b>전에</b> 걸러야 하는 것들이다 —
        /// PCC 에 올라간 뒤에 틀린 걸 알면 잉크가 이미 나가 있다.
        /// </summary>
        public static IReadOnlyList<string> Validate(PrintJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var bad = new List<string>();

            if (job.Steps <= 0 || job.Nozzles <= 0)
                bad.Add("패턴이 비었습니다.");

            if (job.NozzleXUm.Count != job.Nozzles)
                bad.Add($"노즐 위치 {job.NozzleXUm.Count}개가 패턴 컬럼 {job.Nozzles}개와 다릅니다 " +
                        "— 그림이 가로로 밀립니다.");

            if (job.Para.WidthPx != job.Nozzles || job.Para.HeightPx != job.Steps)
                bad.Add($"Print_Para 크기 {job.Para.WidthPx}×{job.Para.HeightPx} 가 패턴 " +
                        $"{job.Nozzles}×{job.Steps} 와 다릅니다 — 다른 저장물이 섞였습니다.");

            if (job.Para.DpiX <= 0 || job.Para.DpiY <= 0)
                bad.Add("DPI 가 0 입니다.");

            if (job.Pattern.ScanStepUm <= 0)
                bad.Add("스캔 스텝이 0 입니다 — 엔코더 한 칸의 이동량을 알 수 없습니다.");

            // 노즐 X 는 늘어나야 한다. 뒤섞이면 그림이 가로로 흩어진다.
            for (int i = 1; i < job.NozzleXUm.Count; i++)
                if (job.NozzleXUm[i] < job.NozzleXUm[i - 1])
                {
                    bad.Add($"노즐 위치가 {i}번째에서 거꾸로 갑니다 — POS.dat 순서를 확인하세요.");
                    break;
                }

            if (job.DropCount == 0)
                bad.Add("방울이 하나도 없습니다 — 빈 그림입니다.");

            return bad;
        }
    }
}
