using IJPSystem.Platform.Infrastructure.Print;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 인쇄 데이터 로드 — 저장해 둔 폴더를 다시 읽어 PCC 로 올리기 전까지.
    ///
    /// <para>Meteor 는 파일을 직접 읽지 않는다. PC 가 읽어서 넘긴다 — 그래서 읽기와 검증은
    /// 장비 없이 전부 확인할 수 있고, 여기서 막아야 잉크가 나가기 전에 걸린다.</para>
    /// </summary>
    public class PrintJobTests : IDisposable
    {
        private readonly string _dir;

        public PrintJobTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ijp_job_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static PrintPattern MakePattern(int steps, int nozzles, Func<int, int, byte> level,
                                                double scanStepUm = 42.3)
        {
            var lv = new byte[steps, nozzles];
            for (int s = 0; s < steps; s++)
                for (int c = 0; c < nozzles; c++)
                    lv[s, c] = level(s, c);

            var cols = new NozzlePosition[nozzles];
            for (int c = 0; c < nozzles; c++)
                cols[c] = new NozzlePosition(c + 1, 0, 0, c, xUm: c * 84.7);

            return new PrintPattern { Levels = lv, Columns = cols, ScanStepUm = scanStepUm };
        }

        /// <summary>저장 버튼이 만드는 것과 같은 폴더 하나. dropLevels 는 저장·복원이 같아야 한다.</summary>
        private string SaveJob(int steps = 6, int nozzles = 4, int dropLevels = 2,
                               bool withPatternFile = true, string name = "Job1")
        {
            string folder = Path.Combine(_dir, name);
            var pattern = MakePattern(steps, nozzles, (s, c) => (byte)((s + c) % dropLevels));

            var para = new PrintDataSet.PrintPara
            {
                DpiX = 600, DpiY = 600,
                WidthPx = nozzles, HeightPx = steps,
                WidthMm = (nozzles - 1) * 84.7 / 1000.0,
                HeightMm = steps * 42.3 / 1000.0,
                HeadCount = 1, NozzlePerHead = 800,
                BitsPerPixel = dropLevels <= 2 ? 1 : 8,
                SubPixelX = 1, SubPixelY = 1,
            };

            PrintDataSet.Save(folder, name, pattern, para, dropLevels);

            if (withPatternFile)
                PrintPatternFile.Save(folder, pattern, new PrintPatternFile.PatternMeta { DropLevels = dropLevels });

            return folder;
        }

        // ── 비트맵 되읽기 ────────────────────────────────────────────────

        [Fact]
        public void 비트맵을_되읽으면_같은_방울단계가_나온다()
        {
            var pattern = MakePattern(5, 7, (s, c) => (byte)((s * c) % 2));
            string bmp = Path.Combine(_dir, "p.bmp");
            PrintDataSet.WritePatternBmp(bmp, pattern, dropLevels: 2);

            var back = PrintDataSet.ReadPatternBmp(bmp, dropLevels: 2);

            Assert.Equal(5, back.GetLength(0));
            Assert.Equal(7, back.GetLength(1));
            for (int s = 0; s < 5; s++)
                for (int c = 0; c < 7; c++)
                    Assert.Equal(pattern.Levels[s, c], back[s, c]);
        }

        [Fact]
        public void 여러_단계도_되읽어진다()
        {
            var pattern = MakePattern(3, 5, (s, c) => (byte)((s + c) % 4));
            string bmp = Path.Combine(_dir, "p.bmp");
            PrintDataSet.WritePatternBmp(bmp, pattern, dropLevels: 4);

            var back = PrintDataSet.ReadPatternBmp(bmp, dropLevels: 4);

            for (int s = 0; s < 3; s++)
                for (int c = 0; c < 5; c++)
                    Assert.Equal(pattern.Levels[s, c], back[s, c]);
        }

        [Fact]
        public void 스텝_순서가_뒤집히지_않는다()
        {
            // BMP 는 아래 행부터 저장한다 — 되읽을 때 안 뒤집으면 그림이 상하로 뒤집힌 채 인쇄된다.
            var pattern = MakePattern(4, 3, (s, c) => (byte)(s == 0 ? 1 : 0));
            string bmp = Path.Combine(_dir, "p.bmp");
            PrintDataSet.WritePatternBmp(bmp, pattern, dropLevels: 2);

            var back = PrintDataSet.ReadPatternBmp(bmp, dropLevels: 2);

            Assert.Equal(1, back[0, 0]);
            Assert.Equal(0, back[3, 0]);
        }

        [Fact]
        public void 비트맵이_아니면_읽지_않는다()
        {
            string fake = Path.Combine(_dir, "fake.bmp");
            File.WriteAllBytes(fake, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 });   // PNG

            Assert.Throws<InvalidDataException>(() => PrintDataSet.ReadPatternBmp(fake, 2));
        }

        // ── 폴더 읽기 ────────────────────────────────────────────────────

        [Fact]
        public void 저장한_폴더를_그대로_다시_읽는다()
        {
            string folder = SaveJob(steps: 6, nozzles: 4);

            var job = PrintJobFile.Load(folder);

            Assert.Equal(6, job.Steps);
            Assert.Equal(4, job.Nozzles);
            Assert.Equal(4, job.NozzleXUm.Count);
            Assert.Equal(600, job.Para.DpiX);
            Assert.Equal(PatternSource.PatternFile, job.Source);
            Assert.EndsWith("Job1.bmp", job.BmpPath);
        }

        [Fact]
        public void 패턴파일이_있으면_그것을_쓴다()
        {
            // pattern.bin 에는 스캔 스텝이 그대로 들어 있다 — 비트맵에서는 되짚어야 한다.
            string folder = SaveJob(steps: 6, nozzles: 4);

            var job = PrintJobFile.Load(folder);

            Assert.Equal(PatternSource.PatternFile, job.Source);
            Assert.Equal(42.3, job.Pattern.ScanStepUm, 3);
        }

        [Fact]
        public void 패턴파일이_없으면_비트맵에서_되짚는다()
        {
            // 랩뷰가 남긴 폴더에는 pattern.bin 이 없다.
            string folder = SaveJob(steps: 6, nozzles: 4, withPatternFile: false);

            var job = PrintJobFile.Load(folder);

            Assert.Equal(PatternSource.Bitmap, job.Source);
            Assert.Equal(6, job.Steps);
            Assert.Equal(4, job.Nozzles);
            Assert.Equal(42.3, job.Pattern.ScanStepUm, 1);   // 세로 치수 ÷ 스텝 수
        }

        [Fact]
        public void 비트맵에서_되짚어도_노즐_위치는_POS에서_온다()
        {
            string folder = SaveJob(steps: 4, nozzles: 5, withPatternFile: false);

            var job = PrintJobFile.Load(folder);

            for (int c = 0; c < 5; c++)
                Assert.Equal(c * 84.7, job.Pattern.Columns[c].XUm, 3);
        }

        [Fact]
        public void 폴더가_없으면_읽다가_막는다()
        {
            Assert.Throws<DirectoryNotFoundException>(
                () => PrintJobFile.Load(Path.Combine(_dir, "없는폴더")));
        }

        [Fact]
        public void POS가_빠진_폴더는_읽지_않는다()
        {
            string folder = SaveJob();
            File.Delete(Path.Combine(folder, PrintDataSet.NozzlePosFileName));

            var ex = Assert.Throws<FileNotFoundException>(() => PrintJobFile.Load(folder));
            Assert.Contains("저장이 끝나지 않은", ex.Message);
        }

        [Fact]
        public void 패턴도_비트맵도_없으면_읽지_않는다()
        {
            string folder = SaveJob();
            foreach (string f in Directory.GetFiles(folder, "*.bmp")) File.Delete(f);
            File.Delete(Path.Combine(folder, PrintPatternFile.DataFileName));

            Assert.Throws<FileNotFoundException>(() => PrintJobFile.Load(folder));
        }

        // ── 가장 최근 것 찾기 ────────────────────────────────────────────

        [Fact]
        public void 가장_최근에_저장한_폴더를_고른다()
        {
            string a = SaveJob(name: "A");
            string b = SaveJob(name: "B");
            // 이름 순서가 아니라 파일 시각으로 골라야 한다.
            File.SetLastWriteTimeUtc(Path.Combine(a, PrintDataSet.PrintParaFileName),
                                     DateTime.UtcNow.AddMinutes(5));

            Assert.Equal(a, PrintJobFile.FindLatest(_dir));
            Assert.NotEqual(b, PrintJobFile.FindLatest(_dir));
        }

        [Fact]
        public void 저장하다_만_폴더는_고르지_않는다()
        {
            string good = SaveJob(name: "Good");
            string half = Path.Combine(_dir, "Half");
            Directory.CreateDirectory(half);
            PrintDataSet.WritePrintPara(Path.Combine(half, PrintDataSet.PrintParaFileName),
                                        new PrintDataSet.PrintPara());   // POS.dat 이 없다
            File.SetLastWriteTimeUtc(Path.Combine(half, PrintDataSet.PrintParaFileName),
                                     DateTime.UtcNow.AddMinutes(5));

            Assert.Equal(good, PrintJobFile.FindLatest(_dir));
        }

        [Fact]
        public void 패턴이_없는_폴더도_고르지_않는다()
        {
            string good = SaveJob(name: "Good");
            string noPattern = SaveJob(name: "NoPattern", withPatternFile: false);
            foreach (string f in Directory.GetFiles(noPattern, "*.bmp")) File.Delete(f);
            File.SetLastWriteTimeUtc(Path.Combine(noPattern, PrintDataSet.PrintParaFileName),
                                     DateTime.UtcNow.AddMinutes(5));

            Assert.Equal(good, PrintJobFile.FindLatest(_dir));
        }

        [Fact]
        public void 아무것도_없으면_null_이다()
        {
            Assert.Null(PrintJobFile.FindLatest(_dir));
            Assert.Null(PrintJobFile.FindLatest(Path.Combine(_dir, "없는폴더")));
            Assert.Null(PrintJobFile.FindLatest(""));
        }

        [Fact]
        public void 루트_자체가_한_벌이어도_찾는다()
        {
            // 폴더를 직접 지정해 저장한 경우 — 하위 폴더가 아니라 그 폴더가 곧 한 벌이다.
            string folder = SaveJob(name: "Only");

            Assert.Equal(folder, PrintJobFile.FindLatest(folder));
        }

        [Fact]
        public void 최근_목록은_새것부터_나온다()
        {
            string a = SaveJob(name: "A");
            string b = SaveJob(name: "B");
            string c = SaveJob(name: "C");
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(Path.Combine(a, PrintDataSet.PrintParaFileName), now.AddMinutes(-10));
            File.SetLastWriteTimeUtc(Path.Combine(b, PrintDataSet.PrintParaFileName), now);
            File.SetLastWriteTimeUtc(Path.Combine(c, PrintDataSet.PrintParaFileName), now.AddMinutes(-5));

            var recent = PrintJobFile.FindRecent(_dir);

            Assert.Equal(new[] { b, c, a }, recent.Select(e => e.Folder));
        }

        [Fact]
        public void 최근_목록은_개수를_지킨다()
        {
            for (int i = 0; i < 8; i++) SaveJob(name: "J" + i);

            Assert.Equal(3, PrintJobFile.FindRecent(_dir, 3).Count);
            Assert.Empty(PrintJobFile.FindRecent(_dir, 0));
        }

        [Fact]
        public void 최근_목록에는_요약이_들어_있다()
        {
            SaveJob(steps: 6, nozzles: 4, name: "Job1");

            var e = Assert.Single(PrintJobFile.FindRecent(_dir));

            Assert.Equal("Job1", e.Name);
            Assert.Equal(4, e.Nozzles);
            Assert.Equal(6, e.Steps);
            Assert.Contains("Job1", e.Label);
            Assert.Contains("6×4", e.Label);
        }

        [Fact]
        public void 요약을_못_읽는_폴더는_목록에서_뺀다()
        {
            // 눌러 봐야 실패할 것을 목록에 띄우지 않는다.
            string good = SaveJob(name: "Good");
            string broken = SaveJob(name: "Broken");
            File.WriteAllBytes(Path.Combine(broken, PrintDataSet.PrintParaFileName), new byte[10]);

            var recent = PrintJobFile.FindRecent(_dir);

            Assert.Equal(good, Assert.Single(recent).Folder);
        }

        [Fact]
        public void 저장하다_만_폴더는_목록에도_없다()
        {
            SaveJob(name: "Good");
            string half = Path.Combine(_dir, "Half");
            Directory.CreateDirectory(half);
            PrintDataSet.WritePrintPara(Path.Combine(half, PrintDataSet.PrintParaFileName),
                                        new PrintDataSet.PrintPara());

            Assert.Single(PrintJobFile.FindRecent(_dir));
        }

        [Fact]
        public void 목록에서_고른_옛것도_그대로_올라간다()
        {
            string old = SaveJob(steps: 4, nozzles: 3, name: "Old");
            string recent = SaveJob(steps: 6, nozzles: 4, name: "Recent");
            File.SetLastWriteTimeUtc(Path.Combine(old, PrintDataSet.PrintParaFileName),
                                     DateTime.UtcNow.AddHours(-2));

            var list = PrintJobFile.FindRecent(_dir);
            Assert.Equal(recent, list[0].Folder);

            var ctl = new PrintJobController(new NullPrintDataDownloader());
            var job = ctl.LoadAndDownload(list.First(e => e.Name == "Old").Folder);

            Assert.NotNull(job);
            Assert.Equal(4, job!.Steps);
            Assert.Equal(3, job.Nozzles);
            Assert.True(ctl.CanPrint);
        }
        [Fact]
        public void 찾은_폴더는_그대로_로드된다()
        {
            SaveJob(name: "A");
            string latest = SaveJob(name: "B");
            File.SetLastWriteTimeUtc(Path.Combine(latest, PrintDataSet.PrintParaFileName),
                                     DateTime.UtcNow.AddMinutes(5));

            var ctl = new PrintJobController(new NullPrintDataDownloader());
            var job = ctl.LoadAndDownload(PrintJobFile.FindLatest(_dir)!);

            Assert.NotNull(job);
            Assert.Equal(latest, job!.Folder);
            Assert.True(ctl.CanPrint);
        }

        // ── 검증 ─────────────────────────────────────────────────────────

        [Fact]
        public void 제대로_저장된_것은_검증을_통과한다()
        {
            var job = PrintJobFile.Load(SaveJob());

            Assert.Empty(PrintJobFile.Validate(job));
        }

        [Fact]
        public void 노즐_위치_개수가_다르면_걸린다()
        {
            // 다른 저장물의 POS.dat 이 섞이는 상황 — 그림이 가로로 밀린다.
            string folder = SaveJob(nozzles: 4);
            PrintDataSet.WriteNozzlePos(Path.Combine(folder, PrintDataSet.NozzlePosFileName),
                                        new double[] { 0, 84.7 });

            var problems = PrintJobFile.Validate(PrintJobFile.Load(folder));

            Assert.Contains(problems, p => p.Contains("가로로 밀립니다"));
        }

        [Fact]
        public void 파라미터_크기가_패턴과_다르면_걸린다()
        {
            string folder = SaveJob(steps: 6, nozzles: 4);
            var para = PrintDataSet.ReadPrintPara(Path.Combine(folder, PrintDataSet.PrintParaFileName));
            para.HeightPx = 999;
            PrintDataSet.WritePrintPara(Path.Combine(folder, PrintDataSet.PrintParaFileName), para);

            var problems = PrintJobFile.Validate(PrintJobFile.Load(folder));

            Assert.Contains(problems, p => p.Contains("다른 저장물이 섞였습니다"));
        }

        [Fact]
        public void 노즐_위치가_거꾸로면_걸린다()
        {
            string folder = SaveJob(nozzles: 4);
            PrintDataSet.WriteNozzlePos(Path.Combine(folder, PrintDataSet.NozzlePosFileName),
                                        new double[] { 0, 200, 100, 300 });

            var problems = PrintJobFile.Validate(PrintJobFile.Load(folder));

            Assert.Contains(problems, p => p.Contains("거꾸로"));
        }

        [Fact]
        public void 방울이_없으면_걸린다()
        {
            string folder = Path.Combine(_dir, "Empty");
            var pattern = MakePattern(4, 3, (s, c) => 0);
            PrintDataSet.Save(folder, "Empty", pattern,
                new PrintDataSet.PrintPara { DpiX = 600, DpiY = 600, WidthPx = 3, HeightPx = 4, HeightMm = 0.1692 },
                dropLevels: 2);
            PrintPatternFile.Save(folder, pattern, new PrintPatternFile.PatternMeta { DropLevels = 2 });

            var problems = PrintJobFile.Validate(PrintJobFile.Load(folder));

            Assert.Contains(problems, p => p.Contains("빈 그림"));
        }

        // ── 상태 흐름 ────────────────────────────────────────────────────

        [Fact]
        public void 로드하면_READY_까지_간다()
        {
            var sink = new NullPrintDataDownloader();
            var ctl = new PrintJobController(sink);
            var seen = new List<PrintReadyState>();
            ctl.StateChanged += seen.Add;

            var job = ctl.LoadAndDownload(SaveJob());

            Assert.NotNull(job);
            Assert.Equal(PrintReadyState.ReadyToPrint, ctl.State);
            Assert.True(ctl.CanPrint);
            Assert.Same(job, sink.Last);
            Assert.Equal(
                new[] { PrintReadyState.Loading, PrintReadyState.Downloading, PrintReadyState.ReadyToPrint },
                seen);
        }

        [Fact]
        public void 검증에_걸리면_전송하지_않는다()
        {
            // 여기서 막아야 한다 — PCC 에 올라간 뒤에 알면 잉크가 이미 나가 있다.
            string folder = SaveJob(nozzles: 4);
            PrintDataSet.WriteNozzlePos(Path.Combine(folder, PrintDataSet.NozzlePosFileName),
                                        new double[] { 0, 84.7 });

            var sink = new NullPrintDataDownloader();
            var ctl = new PrintJobController(sink);

            Assert.Null(ctl.LoadAndDownload(folder));
            Assert.Equal(PrintReadyState.Fault, ctl.State);
            Assert.Null(sink.Last);
            Assert.NotEmpty(ctl.Problems);
            Assert.False(ctl.CanPrint);
        }

        [Fact]
        public void 폴더가_없으면_Fault_로_남고_이유가_보인다()
        {
            var ctl = new PrintJobController(new NullPrintDataDownloader());

            Assert.Null(ctl.LoadAndDownload(Path.Combine(_dir, "없는폴더")));
            Assert.Equal(PrintReadyState.Fault, ctl.State);
            Assert.Contains("읽지 못했습니다", ctl.Message);
        }

        [Fact]
        public void 헤드가_준비되지_않으면_전송하지_않는다()
        {
            var ctl = new PrintJobController(new NotReadyDownloader());

            Assert.Null(ctl.LoadAndDownload(SaveJob()));
            Assert.Equal(PrintReadyState.Fault, ctl.State);
            Assert.Contains("준비되지 않았습니다", ctl.Message);
        }

        [Fact]
        public void 전송이_실패하면_READY_가_되지_않는다()
        {
            var ctl = new PrintJobController(new ThrowingDownloader());

            Assert.Null(ctl.LoadAndDownload(SaveJob()));
            Assert.Equal(PrintReadyState.Fault, ctl.State);
            Assert.Contains("PCC 전송에 실패", ctl.Message);
            Assert.False(ctl.CanPrint);
        }

        [Fact]
        public void 로드하지_않고는_인쇄를_시작할_수_없다()
        {
            var ctl = new PrintJobController(new NullPrintDataDownloader());

            var ex = Assert.Throws<InvalidOperationException>(() => ctl.BeginPrint());
            Assert.Contains("인쇄 데이터 로드", ex.Message);
        }

        [Fact]
        public void 한번_올리면_여러번_찍을_수_있다()
        {
            // 저장과 인쇄가 갈라져 있는 이유다 — 데이터는 PCC 에 남아 파일을 다시 안 읽는다.
            var sink = new NullPrintDataDownloader();
            var ctl = new PrintJobController(sink);
            ctl.LoadAndDownload(SaveJob());

            ctl.BeginPrint();
            Assert.Equal(PrintReadyState.Printing, ctl.State);
            ctl.EndPrint();
            Assert.Equal(PrintReadyState.ReadyToPrint, ctl.State);

            ctl.BeginPrint();
            ctl.EndPrint();
            Assert.True(ctl.CanPrint);
            Assert.Same(sink.Last, ctl.CurrentJob);
        }

        [Fact]
        public void 내리면_처음으로_돌아가고_메모리를_반납한다()
        {
            var sink = new NullPrintDataDownloader();
            var ctl = new PrintJobController(sink);
            ctl.LoadAndDownload(SaveJob());

            ctl.Unload();

            Assert.Equal(PrintReadyState.Idle, ctl.State);
            Assert.Null(ctl.CurrentJob);
            Assert.Null(sink.Last);
            Assert.False(ctl.CanPrint);
        }

        [Fact]
        public void 가상_전송기는_이름으로_드러난다()
        {
            // 준비됐다는 초록 표시만 보고 실제로 올라간 줄 알면 그게 사고다.
            var ctl = new PrintJobController(new NullPrintDataDownloader());
            ctl.LoadAndDownload(SaveJob());

            Assert.Contains("[가상]", ctl.DownloaderName);
            Assert.Contains("[가상]", ctl.Message);
        }

        private sealed class NotReadyDownloader : IPrintDataDownloader
        {
            public string Name => "테스트";
            public bool IsReady => false;
            public void Download(PrintJob job) => throw new InvalidOperationException("불려서는 안 된다");
            public void Release() { }
        }

        private sealed class ThrowingDownloader : IPrintDataDownloader
        {
            public string Name => "테스트";
            public bool IsReady => true;
            public void Download(PrintJob job) => throw new IOException("버퍼를 못 잡았다");
            public void Release() { }
        }
    }
}
