using System.Linq;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Models.Printing;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 인쇄 주행 단계가 운전 모드에 따라 <b>어떻게 달라지는가</b>.
    ///
    /// <para>Dry Run 이면 Meteor 단계를 <b>아예 만들지 않는다</b>. 단계를 내 놓고 안에서
    /// 건너뛰면 화면에는 인쇄하는 것처럼 보이는 목록이 남는다 — 잉크가 안 나갔는데 나간 줄
    /// 아는 것이 이 장비에서 가장 나쁜 실패다.</para>
    /// </summary>
    public class PrintRunOptionsTests
    {
        /// <summary>명령을 받은 대로 적어 두는 가짜 — 순서를 확인한다.</summary>
        private sealed class RecordingJob : IPrintJobCommands
        {
            public System.Collections.Generic.List<string> Calls { get; } = new();
            public string Name => "테스트";
            public void StartJob(int jobId) => Calls.Add("StartJob");
            public void StartSwath(bool forward) => Calls.Add(forward ? "StartSwath(FWD)" : "StartSwath(REV)");
            public void SendImage(uint b, int p, int x, int y, int w) => Calls.Add("SendImage");
            public void EndSwath() => Calls.Add("EndSwath");
            public void EndJob() => Calls.Add("EndJob");
        }

        private static string[] Names(PrintRunOptions opts) =>
            PrintPassSequence.Embedded(null!, null!, opts, 1).Select(s => s.Name).ToArray();

        // ── Dry Run ──────────────────────────────────────────────────────

        [Fact]
        public void 드라이런이면_Meteor_단계가_생기지_않는다()
        {
            var names = Names(new PrintRunOptions { SwathCount = 2, Job = null });

            Assert.DoesNotContain("Step_Print_StartJob", names);
            Assert.DoesNotContain("Step_Print_StartSwath", names);
            Assert.DoesNotContain("Step_Print_EndJob", names);
        }

        [Fact]
        public void 드라이런이어도_모션은_그대로_돈다()
        {
            var names = Names(new PrintRunOptions { SwathCount = 2, SwathPitchMm = 120, Job = null });

            Assert.Equal(2, names.Count(n => n == "Step_Print_Scan"));
            Assert.Contains("Step_Print_SwathStep", names);
        }

        [Fact]
        public void Job_이_없으면_드라이런이다()
            => Assert.True(new PrintRunOptions().IsDryRun);

        // ── Print Run ────────────────────────────────────────────────────

        [Fact]
        public void 프린트런이면_작업_시작과_끝이_한_번씩이다()
        {
            var names = Names(new PrintRunOptions { SwathCount = 3, Job = new RecordingJob() });

            Assert.Equal(1, names.Count(n => n == "Step_Print_StartJob"));
            Assert.Equal(1, names.Count(n => n == "Step_Print_EndJob"));
            Assert.Equal(3, names.Count(n => n == "Step_Print_StartSwath"));
        }

        /// <summary>
        /// ★<b>데이터가 이동보다 먼저</b>다. 스캔 프린터는 엔코더 위치·방향으로 트리거가
        /// 자동 생성되므로, 문서를 큐에 넣기 전에 축이 움직이면 이미 지나간 자리에 찍으라는
        /// 명령이 된다. 순서가 뒤집혀도 에러는 나지 않고 그림만 어긋난다.
        /// </summary>
        [Fact]
        public void 스와스_데이터가_스캔_이동보다_먼저다()
        {
            var names = Names(new PrintRunOptions { SwathCount = 1, Job = new RecordingJob() });

            Assert.True(System.Array.IndexOf(names, "Step_Print_StartSwath")
                      < System.Array.IndexOf(names, "Step_Print_Scan"));
        }

        [Fact]
        public void 작업_시작은_첫_스와스보다_먼저고_종료는_맨_뒤다()
        {
            var names = Names(new PrintRunOptions { SwathCount = 2, Job = new RecordingJob() });

            Assert.Equal(0, System.Array.IndexOf(names, "Step_Print_StartJob"));
            Assert.Equal(names.Length - 1, System.Array.IndexOf(names, "Step_Print_EndJob"));
        }

        /// <summary>한 스와스의 명령은 시작 → 이미지 → 끝 순서로 한 묶음이어야 한다.</summary>
        [Fact]
        public void 스와스_한_묶음의_명령_순서가_맞다()
        {
            var job = new RecordingJob();
            var steps = PrintPassSequence.Embedded(null!, null!,
                new PrintRunOptions { SwathCount = 1, Job = job }, 1);

            // 명령 단계만 실제로 돌려 본다(모션 단계는 null 장비라 부르지 않는다).
            foreach (var s in steps.Where(s => s.Name is "Step_Print_StartJob"
                                                      or "Step_Print_StartSwath"
                                                      or "Step_Print_EndJob"))
                s.Action(default).GetAwaiter().GetResult();

            Assert.Equal(new[] { "StartJob", "StartSwath(FWD)", "SendImage", "EndSwath", "EndJob" },
                         job.Calls);
        }

        // ── 방향 ─────────────────────────────────────────────────────────

        [Fact]
        public void 양방향이면_패스마다_방향이_바뀐다()
        {
            var job = new RecordingJob();
            var steps = PrintPassSequence.Embedded(null!, null!,
                new PrintRunOptions { SwathCount = 3, Bidirectional = true, Job = job }, 1);

            foreach (var s in steps.Where(s => s.Name == "Step_Print_StartSwath"))
                s.Action(default).GetAwaiter().GetResult();

            Assert.Equal(new[] { "StartSwath(FWD)", "SendImage", "EndSwath",
                                 "StartSwath(REV)", "SendImage", "EndSwath",
                                 "StartSwath(FWD)", "SendImage", "EndSwath" }, job.Calls);
        }

        [Fact]
        public void 단방향이면_늘_정방향이고_복귀_단계가_생긴다()
        {
            var job = new RecordingJob();
            var opts = new PrintRunOptions { SwathCount = 2, Bidirectional = false, Job = job };
            var steps = PrintPassSequence.Embedded(null!, null!, opts, 1);

            foreach (var s in steps.Where(s => s.Name == "Step_Print_StartSwath"))
                s.Action(default).GetAwaiter().GetResult();

            Assert.Equal(2, job.Calls.Count(c => c == "StartSwath(FWD)"));
            Assert.DoesNotContain("StartSwath(REV)", job.Calls);
            Assert.Equal(2, steps.Count(s => s.Name == "Step_Print_Return"));
        }

        // ── 번호 ─────────────────────────────────────────────────────────

        [Fact]
        public void 번호는_받은_자리에서_이어진다()
        {
            var steps = PrintPassSequence.Embedded(null!, null!,
                new PrintRunOptions { SwathCount = 2, Job = new RecordingJob() }, 10);

            Assert.Equal(10, steps[0].Number);
            Assert.Equal(Enumerable.Range(10, steps.Count), steps.Select(s => s.Number));
        }
    }
}
