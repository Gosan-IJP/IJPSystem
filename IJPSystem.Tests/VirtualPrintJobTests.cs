using System.Linq;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Infrastructure.Print;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 헤드 없이 인쇄 명령을 기록하는 구현.
    ///
    /// <para>
    /// <b>왜 이것이 있는가</b>: 이게 없으면 인쇄 경로를 시험할 자리가 실기 + 잉크뿐이다.
    /// 그런데 여기서 제일 틀리기 쉬운 것은 <b>순서</b>다 — 스캔 프린터는 엔코더로 트리거가
    /// 자동 생성되므로 명령이 스캔 이동보다 먼저 큐에 들어가야 하고, 뒤집히면 이미 지나간
    /// 자리에 찍으라는 말이 된다. 그 순서를 여기서 고정한다.
    /// </para>
    /// </summary>
    public class VirtualPrintJobTests
    {
        [Fact]
        public void 스캔_인쇄_명령_순서를_그대로_남긴다()
        {
            var job = new VirtualPrintJob();

            job.StartJob(7);
            job.StartSwath(forward: true);
            job.SendImage(bufferId: 3, plane: 1, xLeftPx: 0, yTopPx: 0, widthPx: 800);
            job.EndSwath();
            job.EndJob();

            Assert.Equal(
                new[] { "PiSetHome", "PCMD_STARTJOB", "PCMD_STARTSCAN",
                        "PCMD_IMAGE_BUFFER", "PCMD_ENDDOC", "PCMD_ENDJOB" },
                job.Commands.Select(c => c.Split(' ')[0]).ToArray());
        }

        /// <summary>인자까지 남겨야 대조가 된다 — 이름만으로는 방향·버퍼를 못 본다.</summary>
        [Fact]
        public void 명령에_인자가_함께_남는다()
        {
            var job = new VirtualPrintJob();

            job.StartJob(7);
            job.StartSwath(forward: false);
            job.SendImage(bufferId: 3, plane: 1, xLeftPx: 0, yTopPx: 0, widthPx: 800);

            // STARTJOB [뒤따르는 DWORD 수, Job ID, JT_SCAN, RES_HIGH, 문서폭]
            Assert.Contains("PCMD_STARTJOB [4, 7, 4, 1, 0]", job.Commands);
            // 역방향 = SD_REV(1)
            Assert.Contains("PCMD_STARTSCAN [1, 1]", job.Commands);
            Assert.Contains("PCMD_IMAGE_BUFFER [5, 1, 0, 0, 800, 3]", job.Commands);
        }

        /// <summary>
        /// 이 구현을 넣은 목적 그 자체 — 하드웨어 없이 <b>스와스 데이터가 이동보다 먼저</b>인지
        /// 본다. 실제 시퀀스를 돌려 확인한다.
        /// </summary>
        [Fact]
        public void 가상_헤드로도_인쇄_단계가_만들어진다()
        {
            var job = new VirtualPrintJob();
            var steps = PrintPassSequence.Embedded(null!, null!, new PrintRunOptions
            {
                SwathCount = 2,
                Bidirectional = true,
                Job = job,
                BufferIds = new uint[] { 1, 2 },
                WidthPx = 800,
            }, startNumber: 1);

            var names = steps.Select(s => s.Name).ToArray();

            Assert.Contains("Step_Print_StartJob", names);
            Assert.True(System.Array.IndexOf(names, "Step_Print_StartSwath")
                      < System.Array.IndexOf(names, "Step_Print_Scan"),
                        "스와스 데이터는 스캔 이동보다 먼저 나가야 한다.");
        }

        /// <summary>
        /// 가상 전송기는 버퍼 번호를 <b>내준다</b> — 그래야 Print Run 이 끝까지 돈다.
        /// 대신 엔진 번호(1·2…)와 헷갈릴 수 없는 값이어야 한다.
        /// </summary>
        [Fact]
        public void 가상_전송기의_버퍼번호는_엔진과_헷갈리지_않는다()
        {
            var d = new VirtualPrintDataDownloader();

            Assert.Empty(d.BufferIds);                  // 올리기 전에는 없다

            d.Download(new PrintJob());
            Assert.Equal(new[] { VirtualPrintDataDownloader.VirtualBufferId }, d.BufferIds);
            Assert.True(d.BufferIds[0] > 1000, "엔진은 작은 번호를 준다 — 눈으로 구분돼야 한다.");

            d.Release();
            Assert.Empty(d.BufferIds);
        }

        /// <summary>
        /// 장 수만큼 번호를 내준다 — 실물과 개수가 같아야 인쇄가 같은 경로를 탄다.
        /// 하나만 주면 가상에서만 다른 길로 가서 시험이 헛돈다.
        /// </summary>
        [Fact]
        public void 가상_전송기는_장_수만큼_번호를_준다()
        {
            var d = new VirtualPrintDataDownloader();

            d.Download(new PrintJob
            {
                Images = new[] { new PrintPattern(), new PrintPattern(), new PrintPattern() },
            });

            Assert.Equal(3, d.BufferIds.Count);
            Assert.Equal(3, d.BufferIds.Distinct().Count());   // 번호가 겹치면 같은 그림이 나간다
        }

        /// <summary>헤드 없는 구성(None)은 그대로 막혀야 한다 — 가상과 갈라 둔 이유다.</summary>
        [Fact]
        public void 헤드_없는_구성은_버퍼번호를_내주지_않는다()
        {
            var d = new NullPrintDataDownloader();

            d.Download(new PrintJob());

            Assert.Empty(d.BufferIds);
        }
    }
}
