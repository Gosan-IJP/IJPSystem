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
    ///   <c>pattern.json</c> 메타(스텝·컬럼·노즐 번호·X 위치·스캔 스텝·단계 수)
    ///   <c>pattern.bin</c>  본체. 스텝 순서로 이어 붙인 <c>스텝수 × 노즐수</c> 바이트(행 우선).
    /// CSV 가 아닌 이유: 100mm 인쇄 × 800노즐이면 200만 칸이라 텍스트로는 수 MB 가 되고,
    /// 어차피 사람이 읽는 것은 메타뿐이다.
    /// </para>
    /// </summary>
    public static class PrintPatternFile
    {
        public const string MetaFileName = "pattern.json";
        public const string DataFileName = "pattern.bin";

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
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("폴더 경로가 비었습니다.", nameof(folder));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (meta    == null) throw new ArgumentNullException(nameof(meta));

            Directory.CreateDirectory(folder);

            int steps = pattern.Steps, cols = pattern.Nozzles;
            meta.Steps      = steps;
            meta.Nozzles    = cols;
            meta.ScanStepUm = pattern.ScanStepUm;

            var columns = new List<ColumnInfo>(cols);
            foreach (var c in pattern.Columns)
                columns.Add(new ColumnInfo { Nozzle = c.Number, Head = c.Head, Row = c.Row, XUm = c.XUm });
            meta.Columns = columns;

            // 본체를 먼저 쓴다 — 메타만 있고 데이터가 없는 폴더가 남으면 읽는 쪽이 헛돈다.
            string dataPath = Path.Combine(folder, DataFileName);
            using (var fs = File.Create(dataPath))
            {
                var row = new byte[cols];
                for (int s = 0; s < steps; s++)
                {
                    for (int c = 0; c < cols; c++) row[c] = pattern.Levels[s, c];
                    fs.Write(row, 0, cols);
                }
            }

            string metaPath = Path.Combine(folder, MetaFileName);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOpts));
            return metaPath;
        }

        /// <summary>저장된 패턴을 읽는다. 폴더가 비었거나 크기가 안 맞으면 예외.</summary>
        public static (PrintPattern Pattern, PatternMeta Meta) Load(string folder)
        {
            string metaPath = Path.Combine(folder, MetaFileName);
            string dataPath = Path.Combine(folder, DataFileName);
            if (!File.Exists(metaPath)) throw new FileNotFoundException("패턴 메타가 없습니다.", metaPath);
            if (!File.Exists(dataPath)) throw new FileNotFoundException("패턴 데이터가 없습니다.", dataPath);

            var meta = JsonSerializer.Deserialize<PatternMeta>(File.ReadAllText(metaPath), JsonOpts)
                       ?? throw new InvalidDataException("패턴 메타를 읽지 못했습니다.");

            long expect = (long)meta.Steps * meta.Nozzles;
            var info = new FileInfo(dataPath);
            if (info.Length != expect)
                throw new InvalidDataException(
                    $"패턴 데이터 크기가 메타와 다릅니다 — {info.Length} bytes, 기대 {expect} " +
                    $"({meta.Steps}스텝 × {meta.Nozzles}노즐). 저장이 중간에 끊겼을 수 있습니다.");

            var levels = new byte[meta.Steps, meta.Nozzles];
            using (var fs = File.OpenRead(dataPath))
            {
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
            }

            var columns = new List<NozzlePosition>(meta.Nozzles);
            foreach (var c in meta.Columns)
                columns.Add(new NozzlePosition(c.Nozzle, c.Head, c.Row, 0, c.XUm));

            var pattern = new PrintPattern
            {
                Levels     = levels,
                Columns    = columns,
                ScanStepUm = meta.ScanStepUm,
            };
            return (pattern, meta);
        }
    }
}
