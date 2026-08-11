using System;
using System.IO;
using System.Linq;
using IJPSystem.Platform.Infrastructure.Print;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 토출 패턴 저장/복원.
    ///
    /// <para>
    /// RIP 결과는 인쇄 직전에 다시 만들 것이 아니라 <b>확인하고 재현할 수 있어야</b> 한다.
    /// 같은 DXF 를 두 번 변환해 다른 결과가 나오면 인쇄 사고의 원인을 찾을 수 없다.
    /// 여기서는 "저장한 것이 그대로 돌아온다"와 "깨진 파일은 조용히 넘어가지 않는다"를 고정한다.
    /// </para>
    /// </summary>
    public class PrintPatternFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ptn_{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static PrintPattern Sample(int steps = 5, int cols = 4)
        {
            var levels = new byte[steps, cols];
            for (int s = 0; s < steps; s++)
                for (int c = 0; c < cols; c++)
                    levels[s, c] = (byte)((s * cols + c) % 4);   // 0~3 단계

            var columns = Enumerable.Range(0, cols)
                .Select(i => new NozzlePosition(number: i + 1, head: 0, row: i % 2,
                                                indexInRow: i / 2, xUm: i * 42.35))
                .ToList();

            return new PrintPattern { Levels = levels, Columns = columns, ScanStepUm = 42.35 };
        }

        [Fact]
        public void 저장한_패턴이_그대로_돌아온다()
        {
            var p = Sample();
            PrintPatternFile.Save(_dir, p, new PrintPatternFile.PatternMeta { DropLevels = 4 });

            var (loaded, meta) = PrintPatternFile.Load(_dir);

            Assert.Equal(p.Steps,   loaded.Steps);
            Assert.Equal(p.Nozzles, loaded.Nozzles);
            Assert.Equal(p.ScanStepUm, loaded.ScanStepUm, 6);
            Assert.Equal(4, meta.DropLevels);

            for (int s = 0; s < p.Steps; s++)
                for (int c = 0; c < p.Nozzles; c++)
                    Assert.Equal(p.Levels[s, c], loaded.Levels[s, c]);
        }

        [Fact]
        public void 노즐_번호와_X_위치가_보존된다()
        {
            // 이게 틀리면 패턴은 맞는데 엉뚱한 노즐이 쏜다 — 그림이 통째로 밀린다.
            var p = Sample();
            PrintPatternFile.Save(_dir, p, new PrintPatternFile.PatternMeta());

            var (loaded, _) = PrintPatternFile.Load(_dir);

            Assert.Equal(p.Columns.Select(c => c.Number), loaded.Columns.Select(c => c.Number));
            Assert.Equal(p.Columns.Select(c => c.Row),    loaded.Columns.Select(c => c.Row));
            for (int i = 0; i < p.Columns.Count; i++)
                Assert.Equal(p.Columns[i].XUm, loaded.Columns[i].XUm, 6);
        }

        [Fact]
        public void 버려진_노즐_번호가_메타에_남는다()
        {
            // 조용히 빠지면 번호 기준(0/1 시작) 오류를 인쇄 결과를 보고서야 알게 된다.
            PrintPatternFile.Save(_dir, Sample(), new PrintPatternFile.PatternMeta
            {
                IgnoredNozzles = new[] { 801, 802 },
                SourceImage    = @"C:\out\BMP_260811.png",
            });

            var (_, meta) = PrintPatternFile.Load(_dir);

            Assert.Equal(new[] { 801, 802 }, meta.IgnoredNozzles);
            Assert.Equal(@"C:\out\BMP_260811.png", meta.SourceImage);
        }

        [Fact]
        public void 데이터_크기가_메타와_다르면_실패한다()
        {
            PrintPatternFile.Save(_dir, Sample(), new PrintPatternFile.PatternMeta());

            // 저장이 중간에 끊긴 상황 — 조용히 읽으면 패턴이 밀린 채로 인쇄된다.
            string data = Path.Combine(_dir, PrintPatternFile.DataFileName);
            var bytes = File.ReadAllBytes(data);
            File.WriteAllBytes(data, bytes.Take(bytes.Length - 3).ToArray());

            var ex = Assert.Throws<InvalidDataException>(() => PrintPatternFile.Load(_dir));
            Assert.Contains("크기", ex.Message);
        }

        [Fact]
        public void 폴더가_비어_있으면_실패한다()
        {
            Directory.CreateDirectory(_dir);
            Assert.Throws<FileNotFoundException>(() => PrintPatternFile.Load(_dir));
        }

        [Fact]
        public void 파일_크기는_스텝곱하기노즐_바이트다()
        {
            var p = Sample(steps: 7, cols: 9);
            PrintPatternFile.Save(_dir, p, new PrintPatternFile.PatternMeta());

            var info = new FileInfo(Path.Combine(_dir, PrintPatternFile.DataFileName));
            Assert.Equal(7 * 9, info.Length);
        }
    }
}
