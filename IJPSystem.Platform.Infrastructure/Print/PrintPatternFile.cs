using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 토출 패턴을 폴더 하나로 저장/복원한다.
    ///
    /// <para>
    /// <b>왜 파일로 남기는가</b>: RIP 결과(어느 노즐이 언제 무엇을 쏘는가)는 인쇄 직전에 다시
    /// 만들 것이 아니라 <b>확인하고 재현할 수 있어야</b> 한다. 같은 DXF 를 두 번 변환해 다른
    /// 결과가 나오면 인쇄 사고의 원인을 찾을 수 없다. PCC 전송 경로가 붙기 전에도 이 파일만
    /// 있으면 패턴을 열어 볼 수 있다.
    /// </para>
    /// <para>
    /// 형식 — 폴더 안에 두 개:
    ///   <c>pattern.json</c> 메타(스텝·컬럼·노즐 번호·X 위치·스캔 스텝·단계 수·패스)
    ///   <c>pattern.bin</c>  1패스 본체. 스텝 순서로 이어 붙인 <c>스텝수 × 노즐수</c> 바이트(행 우선).
    ///   <c>pattern.p1.bin</c> … 2패스부터. 패스마다 헤드를 <c>PassOffsetXUm</c> 만큼 옮겨 찍는다.
    /// CSV 가 아닌 이유: 100mm 인쇄 × 800노즐이면 200만 칸이라 텍스트로는 수 MB 가 되고,
    /// 어차피 사람이 읽는 것은 메타뿐이다.
    /// </para>
    /// </summary>
    public static class PrintPatternFile
    {
        public const string MetaFileName = "pattern.json";
        public const string DataFileName = "pattern.bin";

        /// <summary>
        /// 패스 <paramref name="pass"/> 의 본체 파일명. 0번은 <c>pattern.bin</c> —
        /// 한 패스뿐인 흔한 경우의 파일 이름을 다중 패스 때문에 바꾸지 않는다.
        /// </summary>
        public static string PassFileName(int pass) => pass <= 0 ? DataFileName : $"pattern.p{pass}.bin";

        /// <summary>
        /// 스와스 <paramref name="swath"/>, 패스 <paramref name="pass"/> 의 본체 파일명.
        ///
        /// <para>스와스 0 은 <see cref="PassFileName"/> 과 <b>같은 이름</b>을 쓴다 — 한 폭짜리
        /// 저장물이 대부분이고, 그 흔한 경우의 파일 이름을 다중 스와스 때문에 바꾸지 않는다.
        /// 덕분에 예전에 저장한 폴더도 그대로 읽힌다.</para>
        /// </summary>
        public static string ImageFileName(int swath, int pass)
            => swath <= 0 ? PassFileName(pass)
             : pass  <= 0 ? $"pattern.s{swath}.bin"
                          : $"pattern.s{swath}.p{pass}.bin";

        /// <summary>한 컬럼(=노즐 하나)의 위치. 열 순서는 <c>pattern.bin</c> 의 열 순서와 1:1.</summary>
        public sealed class ColumnInfo
        {
            public int    Nozzle { get; set; }
            public int    Head   { get; set; }
            public int    Row    { get; set; }
            public double XUm    { get; set; }
        }

        public sealed class PatternMeta
        {
            /// <summary>형식 판 번호. 늘어나면 읽는 쪽이 분기한다.</summary>
            public int Version { get; set; } = 1;

            public int    Steps      { get; set; }
            public int    Nozzles    { get; set; }
            public double ScanStepUm { get; set; }

            /// <summary>방울 단계 수(2 = 찍/안찍). 값의 상한은 <c>DropLevels − 1</c>.</summary>
            public int DropLevels { get; set; }

            /// <summary>
            /// 패스 수. 1 이면 한 번 지나가며 찍는다.
            ///
            /// <para>
            /// 2 이상은 <b>가로 방향을 촘촘하게</b> 만들려고 나눈 것이다. 노즐 피치는 하드웨어라
            /// 못 줄이므로, 헤드를 피치의 1/N 만큼 옮겨 N 번 지나간다. 각 패스의 본체가
            /// <c>pattern.bin</c>, <c>pattern.p1.bin</c> … 으로 따로 있다.
            /// </para>
            /// </summary>
            public int PassCount { get; set; } = 1;

            /// <summary>패스 사이 크로스스캔 이동량 [µm]. 1패스면 0.</summary>
            public double PassOffsetXUm { get; set; }

            /// <summary>
            /// 그림을 다 덮는 데 필요한 스와스(가로 타일) 수. 1 이면 헤드 한 폭에 들어간다.
            ///
            /// <para>
            /// <b>패스와 다른 것이다.</b> <see cref="PassCount"/> 는 같은 자리를 촘촘하게
            /// 만드는 인터레이스고, 이쪽은 <b>헤드보다 넓은 그림</b>을 한 폭씩 나눠 덮는 것이다.
            /// </para>
            /// <para>
            /// ★생성 때 계산해 두는 이유: 이 값을 만들 재료가 <b>이 시점에만</b> 있다.
            /// 저장되는 <c>WidthMm</c> 은 노즐 X 범위(=헤드 폭)라서, 파일만 나중에 열면
            /// 원본이 얼마나 넓었는지 알 방법이 없다 — 넘친 부분은 이미 잘린 뒤다.
            /// </para>
            /// </summary>
            public int SwathCount { get; set; } = 1;

            /// <summary>스와스 사이 크로스스캔 이동량 [µm] = 쓰는 노즐의 X 범위. 1스와스면 0.</summary>
            public double SwathPitchUm { get; set; }

            /// <summary>원본 그림의 실제 가로 [mm]. 잘렸는지 따지려면 이것과 헤드 폭을 견줘야 한다.</summary>
            public double SourceWidthMm { get; set; }

            /// <summary>어떤 이미지에서 나왔는지 — 결과를 의심할 때 되짚을 유일한 실마리다.</summary>
            public string? SourceImage { get; set; }
            public double  SourceDpiX  { get; set; }
            public double  SourceDpiY  { get; set; }
            public string? CreatedAt   { get; set; }

            /// <summary>변환에서 버려진 노즐 번호(헤드 범위 밖). 비어 있지 않으면 번호 기준을 의심할 것.</summary>
            public IReadOnlyList<int> IgnoredNozzles { get; set; } = Array.Empty<int>();

            public IReadOnlyList<ColumnInfo> Columns { get; set; } = Array.Empty<ColumnInfo>();
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>패턴을 폴더에 저장한다. 폴더가 없으면 만든다. 반환: 메타 파일 경로.</summary>
        public static string Save(string folder, PrintPattern pattern, PatternMeta meta)
            => Save(folder, new[] { pattern ?? throw new ArgumentNullException(nameof(pattern)) }, meta);

        /// <summary>
        /// 여러 장을 한 폴더에 저장한다. 모두 <b>같은 스텝 수·같은 컬럼</b>이어야 한다 —
        /// 헤드가 X 로만 옮겨 다니므로 노즐 구성이 달라질 이유가 없고, 달라졌다면 만든 쪽이 틀린 것이다.
        ///
        /// <para><b>순서는 스와스 우선</b>이다 — <c>[스와스0 패스0, 스와스0 패스1, 스와스1 패스0, …]</c>.
        /// 장 수는 <c>SwathCount × PassCount</c> 여야 하고, <see cref="PatternMeta.SwathCount"/> 는
        /// 부르는 쪽이 채워 둔다(그 값을 셀 수 있는 곳은 만드는 자리뿐이다). PassCount 는 여기서 나눈다.</para>
        /// </summary>
        public static string Save(string folder, IReadOnlyList<PrintPattern> images, PatternMeta meta)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("폴더 경로가 비었습니다.", nameof(folder));
            if (images == null || images.Count == 0) throw new ArgumentException("패턴이 없습니다.", nameof(images));
            if (meta   == null) throw new ArgumentNullException(nameof(meta));

            int swaths = Math.Max(1, meta.SwathCount);
            if (images.Count % swaths != 0)
                throw new ArgumentException(
                    $"패턴 {images.Count}장이 스와스 {swaths}개로 나누어떨어지지 않습니다 — " +
                    "스와스마다 같은 수의 패스가 있어야 합니다.", nameof(images));

            var first = images[0] ?? throw new ArgumentException("패턴이 비었습니다.", nameof(images));
            int steps = first.Steps, cols = first.Nozzles;
            for (int i = 1; i < images.Count; i++)
            {
                var p = images[i] ?? throw new ArgumentException($"패턴 {i} 가 비었습니다.", nameof(images));
                if (p.Steps != steps || p.Nozzles != cols)
                    throw new ArgumentException(
                        $"패턴 {i} 크기가 다릅니다 — {p.Steps}×{p.Nozzles}, 기대 {steps}×{cols}.", nameof(images));
            }

            Directory.CreateDirectory(folder);

            meta.Steps      = steps;
            meta.Nozzles    = cols;
            meta.ScanStepUm = first.ScanStepUm;
            meta.SwathCount = swaths;
            meta.PassCount  = images.Count / swaths;

            var columns = new List<ColumnInfo>(cols);
            foreach (var c in first.Columns)
                columns.Add(new ColumnInfo { Nozzle = c.Number, Head = c.Head, Row = c.Row, XUm = c.XUm });
            meta.Columns = columns;

            // 본체를 먼저 쓴다 — 메타만 있고 데이터가 없는 폴더가 남으면 읽는 쪽이 헛돈다.
            for (int i = 0; i < images.Count; i++)
            {
                string name = ImageFileName(i / meta.PassCount, i % meta.PassCount);
                using var fs = File.Create(Path.Combine(folder, name));
                var row = new byte[cols];
                for (int s = 0; s < steps; s++)
                {
                    for (int c = 0; c < cols; c++) row[c] = images[i].Levels[s, c];
                    fs.Write(row, 0, cols);
                }
            }

            string metaPath = Path.Combine(folder, MetaFileName);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOpts));
            return metaPath;
        }

        /// <summary>저장된 패턴의 <b>1패스</b>를 읽는다. 폴더가 비었거나 크기가 안 맞으면 예외.</summary>
        public static (PrintPattern Pattern, PatternMeta Meta) Load(string folder)
        {
            var (passes, meta) = LoadAll(folder);
            return (passes[0], meta);
        }

        /// <summary>
        /// 저장된 패턴을 <b>전부</b> 읽는다 — 스와스 × 패스, 스와스 우선 순서.
        ///
        /// <para>인쇄는 장마다 버퍼가 따로 필요하다. 첫 장만 읽으면 나머지 폭·패스는 올라가지
        /// 않은 채 같은 그림이 되풀이 찍힌다.</para>
        /// </summary>
        public static (IReadOnlyList<PrintPattern> Images, PatternMeta Meta) LoadAll(string folder)
        {
            string metaPath = Path.Combine(folder, MetaFileName);
            if (!File.Exists(metaPath)) throw new FileNotFoundException("패턴 메타가 없습니다.", metaPath);

            var meta = JsonSerializer.Deserialize<PatternMeta>(File.ReadAllText(metaPath), JsonOpts)
                       ?? throw new InvalidDataException("패턴 메타를 읽지 못했습니다.");

            var columns = new List<NozzlePosition>(meta.Nozzles);
            foreach (var c in meta.Columns)
                columns.Add(new NozzlePosition(c.Nozzle, c.Head, c.Row, 0, c.XUm));

            int swaths = Math.Max(1, meta.SwathCount);
            int passes = Math.Max(1, meta.PassCount);

            var images = new List<PrintPattern>(swaths * passes);
            for (int s = 0; s < swaths; s++)
                for (int k = 0; k < passes; k++)
                    images.Add(new PrintPattern
                    {
                        Levels     = ReadImage(folder, s, k, meta),
                        Columns    = columns,
                        ScanStepUm = meta.ScanStepUm,
                    });

            return (images, meta);
        }

        private static byte[,] ReadImage(string folder, int swath, int pass, PatternMeta meta)
        {
            string dataPath = Path.Combine(folder, ImageFileName(swath, pass));
            if (!File.Exists(dataPath)) throw new FileNotFoundException("패턴 데이터가 없습니다.", dataPath);

            long expect = (long)meta.Steps * meta.Nozzles;
            var info = new FileInfo(dataPath);
            if (info.Length != expect)
                throw new InvalidDataException(
                    $"패턴 데이터 크기가 메타와 다릅니다 — {Path.GetFileName(dataPath)} {info.Length} bytes, " +
                    $"기대 {expect} ({meta.Steps}스텝 × {meta.Nozzles}노즐). 저장이 중간에 끊겼을 수 있습니다.");

            var levels = new byte[meta.Steps, meta.Nozzles];
            using var fs = File.OpenRead(dataPath);
            var row = new byte[meta.Nozzles];
            for (int s = 0; s < meta.Steps; s++)
            {
                int read = 0;
                while (read < row.Length)
                {
                    int n = fs.Read(row, read, row.Length - read);
                    if (n <= 0) throw new InvalidDataException("패턴 데이터가 잘렸습니다.");
                    read += n;
                }
                for (int c = 0; c < meta.Nozzles; c++) levels[s, c] = row[c];
            }
            return levels;
        }
    }
}
