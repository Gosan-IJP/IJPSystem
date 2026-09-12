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

        /// <summary>
        /// 단계 <b>모양</b>만 보는 시험에 쓰는 버퍼 번호. 넉넉히 둔다 — 인쇄는 필요한 만큼만 꺼내 쓴다.
        /// (버퍼가 모자랄 때의 동작은 <c>버퍼가_모자라면_단계를_만들지_않는다</c> 가 따로 본다)
        /// </summary>
        private static readonly uint[] Buffers = { 1, 2, 3, 4, 5, 6, 7, 8 };

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
            var names = Names(new PrintRunOptions { SwathCount = 3, Job = new RecordingJob(), BufferIds = Buffers });

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
            var names = Names(new PrintRunOptions { SwathCount = 1, Job = new RecordingJob(), BufferIds = Buffers });

            Assert.True(System.Array.IndexOf(names, "Step_Print_StartSwath")
                      < System.Array.IndexOf(names, "Step_Print_Scan"));
        }

        [Fact]
        public void 작업_시작은_첫_스와스보다_먼저고_종료는_맨_뒤다()
        {
            var names = Names(new PrintRunOptions { SwathCount = 2, Job = new RecordingJob(), BufferIds = Buffers });

            Assert.Equal(0, System.Array.IndexOf(names, "Step_Print_StartJob"));
            Assert.Equal(names.Length - 1, System.Array.IndexOf(names, "Step_Print_EndJob"));
        }

        /// <summary>한 스와스의 명령은 시작 → 이미지 → 끝 순서로 한 묶음이어야 한다.</summary>
        [Fact]
        public async Task 스와스_한_묶음의_명령_순서가_맞다()
        {
            var job = new RecordingJob();
            var steps = PrintPassSequence.Embedded(null!, null!,
                new PrintRunOptions { SwathCount = 1, Job = job, BufferIds = Buffers }, 1);

            // 명령 단계만 실제로 돌려 본다(모션 단계는 null 장비라 부르지 않는다).
            foreach (var s in steps.Where(s => s.Name is "Step_Print_StartJob"
                                                      or "Step_Print_StartSwath"
                                                      or "Step_Print_EndJob"))
                await s.Action(default);

            Assert.Equal(new[] { "StartJob", "StartSwath(FWD)", "SendImage", "EndSwath", "EndJob" },
                         job.Calls);
        }

        // ── 방향 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 양방향이면_패스마다_방향이_바뀐다()
        {
            var job = new RecordingJob();
            var steps = PrintPassSequence.Embedded(null!, null!,
                new PrintRunOptions { SwathCount = 3, Bidirectional = true, Job = job, BufferIds = Buffers }, 1);

            foreach (var s in steps.Where(s => s.Name == "Step_Print_StartSwath"))
                await s.Action(default);

            Assert.Equal(new[] { "StartSwath(FWD)", "SendImage", "EndSwath",
                                 "StartSwath(REV)", "SendImage", "EndSwath",
                                 "StartSwath(FWD)", "SendImage", "EndSwath" }, job.Calls);
        }

        [Fact]
        public async Task 단방향이면_늘_정방향이고_복귀_단계가_생긴다()
        {
            var job = new RecordingJob();
            var opts = new PrintRunOptions { SwathCount = 2, Bidirectional = false, Job = job, BufferIds = Buffers };
            var steps = PrintPassSequence.Embedded(null!, null!, opts, 1);

            foreach (var s in steps.Where(s => s.Name == "Step_Print_StartSwath"))
                await s.Action(default);

            Assert.Equal(2, job.Calls.Count(c => c == "StartSwath(FWD)"));
            Assert.DoesNotContain("StartSwath(REV)", job.Calls);
            Assert.Equal(2, steps.Count(s => s.Name == "Step_Print_Return"));
        }

        // ── 끝점은 누가 정하는가 ─────────────────────────────────────────
        //
        // ★Dry Run 은 사람(티칭 PRINT END), Print Run 은 데이터(패턴 길이)다.
        //   끝점을 늘 티칭으로 두면 진실이 둘이 되어, 패턴을 바꾼 뒤 티칭이 낡아도
        //   아무도 모르는 채 뒷부분이 안 찍힌다.

        /// <summary>모션 호출을 적어 두는 가짜 — 어디로 가라고 했는지 확인한다.</summary>
        private sealed class RecordingMotion : IJPSystem.Platform.Domain.Interfaces.IMotionService
        {
            public System.Collections.Generic.List<string> Moves { get; } = new();

            public System.Threading.Tasks.Task MoveAxisToPointAsync(
                string axisNo, string pointName, System.Threading.CancellationToken ct,
                IJPSystem.Platform.Domain.Models.Motion.MotionProfileKind profile
                    = IJPSystem.Platform.Domain.Models.Motion.MotionProfileKind.Move)
            {
                Moves.Add($"{axisNo}→{pointName}");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public System.Threading.Tasks.Task MoveAxisRelativeAsync(
                string axisNo, double delta, System.Threading.CancellationToken ct,
                IJPSystem.Platform.Domain.Models.Motion.MotionProfileKind profile
                    = IJPSystem.Platform.Domain.Models.Motion.MotionProfileKind.Move)
            {
                Moves.Add($"{axisNo}{delta:+0.###;-0.###}");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public System.Threading.Tasks.Task MoveToPointAsync(
                string pointName, System.Threading.CancellationToken ct,
                IJPSystem.Platform.Domain.Models.Motion.MotionProfileKind profile
                    = IJPSystem.Platform.Domain.Models.Motion.MotionProfileKind.Move)
                => System.Threading.Tasks.Task.CompletedTask;

            public System.Threading.Tasks.Task ServoOnAllAsync() => System.Threading.Tasks.Task.CompletedTask;
            public System.Threading.Tasks.Task HomeAllAsync(System.Threading.CancellationToken ct)
                => System.Threading.Tasks.Task.CompletedTask;
        }

        private static string[] ScanMoves(PrintRunOptions opts)
        {
            var motion = new RecordingMotion();
            foreach (var s in PrintPassSequence.Embedded(null!, motion, opts, 1)
                                               .Where(s => s.Name is "Step_Print_Scan" or "Step_Print_Return"))
                s.Action(default).GetAwaiter().GetResult();
            return motion.Moves.ToArray();
        }

        [Fact]
        public void 드라이런은_티칭된_PRINT_END_로_간다()
        {
            var moves = ScanMoves(new PrintRunOptions { SwathCount = 1, Job = null });

            Assert.Equal(new[] { "Y→PRINT END" }, moves);
        }

        [Fact]
        public void 프린트런은_패턴_길이만큼_간다()
        {
            var moves = ScanMoves(new PrintRunOptions
            {
                SwathCount   = 1,
                Job          = new RecordingJob(),
                BufferIds    = Buffers,
                ScanTravelMm = 204.0,
            });

            Assert.Equal(new[] { "Y+204" }, moves);
        }

        /// <summary>부호가 방향이다 — 티칭이 Y 가 줄어드는 쪽이면 음수로 들어온다.</summary>
        [Fact]
        public void 주행_부호가_인쇄_방향을_정한다()
        {
            var moves = ScanMoves(new PrintRunOptions
            {
                SwathCount   = 2,
                Bidirectional = true,
                Job          = new RecordingJob(),
                BufferIds    = Buffers,
                ScanTravelMm = -150.0,
            });

            // 1패스 정방향(-150), 2패스 역방향이라 되돌아온다(+150)
            Assert.Equal(new[] { "Y-150", "Y+150" }, moves);
        }

        /// <summary>거리로 갔으면 거리로 돌아온다 — 티칭 점으로 돌아가면 간 만큼과 어긋난다.</summary>
        [Fact]
        public void 단방향_복귀도_같은_거리로_돌아온다()
        {
            var moves = ScanMoves(new PrintRunOptions
            {
                SwathCount    = 1,
                Bidirectional = false,
                Job           = new RecordingJob(),
                BufferIds     = Buffers,
                ScanTravelMm  = 204.0,
            });

            Assert.Equal(new[] { "Y+204", "Y-204" }, moves);
        }

        // ── 다중 스와스 · 인터레이스 ─────────────────────────────────────
        //
        // ★스와스마다 그림이 다르다. 번호 하나를 되풀이 보내면 같은 그림이 옆으로 여러 번
        //   찍힌다 — 덜 찍히는 것보다 나쁘다(눈에 안 띄고 잉크·글라스를 버린다).

        /// <summary>보낸 버퍼 번호를 순서대로 적는 가짜.</summary>
        private sealed class BufferRecordingJob : IPrintJobCommands
        {
            public System.Collections.Generic.List<uint> Sent { get; } = new();
            public string Name => "테스트";
            public void StartJob(int jobId) { }
            public void StartSwath(bool forward) { }
            public void SendImage(uint b, int p, int x, int y, int w) => Sent.Add(b);
            public void EndSwath() { }
            public void EndJob() { }
        }

        /// <summary>
        /// 명령·이동 단계만 돌린다. <c>…Done</c> 단계는 실제 모션 완료를 기다리므로
        /// 장비 없이 부르면 터진다 — 여기서 보는 것은 <b>무엇을 어느 순서로 시켰는가</b>다.
        /// </summary>
        private static void RunAll(PrintRunOptions opts, IJPSystem.Platform.Domain.Interfaces.IMotionService motion)
        {
            foreach (var s in PrintPassSequence.Embedded(null!, motion, opts, 1))
                if (!s.Name.EndsWith("Done"))
                    s.Action(default).GetAwaiter().GetResult();
        }

        [Fact]
        public void 스와스마다_다른_버퍼가_나간다()
        {
            var job = new BufferRecordingJob();
            RunAll(new PrintRunOptions
            {
                SwathCount   = 3,
                SwathPitchMm = 120.0,
                Job          = job,
                BufferIds    = new uint[] { 11, 22, 33 },
                ScanTravelMm = 200,
            }, new RecordingMotion());

            Assert.Equal(new uint[] { 11, 22, 33 }, job.Sent);
        }

        [Fact]
        public void 인터레이스는_스와스_안에서_먼저_돈다()
        {
            var job = new BufferRecordingJob();
            RunAll(new PrintRunOptions
            {
                SwathCount   = 2,
                PassCount    = 2,
                SwathPitchMm = 120.0,
                PassPitchMm  = 0.02,
                Job          = job,
                // 스와스 우선 순서 — [스와스0 패스0, 스와스0 패스1, 스와스1 패스0, 스와스1 패스1]
                BufferIds    = new uint[] { 1, 2, 3, 4 },
                ScanTravelMm = 200,
            }, new RecordingMotion());

            Assert.Equal(new uint[] { 1, 2, 3, 4 }, job.Sent);
        }

        /// <summary>
        /// 스와스 이동은 인터레이스로 이미 옮겨 온 만큼을 뺀다 — 안 빼면 패스를 돌 때마다
        /// 스와스가 조금씩 오른쪽으로 밀린다.
        /// </summary>
        [Fact]
        public void 스와스_이동은_인터레이스_이동을_뺀_거리다()
        {
            var motion = new RecordingMotion();
            RunAll(new PrintRunOptions
            {
                SwathCount   = 2,
                PassCount    = 3,
                SwathPitchMm = 120.0,
                PassPitchMm  = 0.02,
                Job          = new BufferRecordingJob(),
                BufferIds    = new uint[] { 1, 2, 3, 4, 5, 6 },
                ScanTravelMm = 200,
            }, motion);

            // 인터레이스 2번(0.02×2=0.04) 뒤 스와스는 120−0.04 만큼만 간다.
            Assert.Contains("X+0.02", motion.Moves);
            Assert.Contains("X+119.96", motion.Moves);
        }

        /// <summary>버퍼가 모자라면 <b>돌기 전에</b> 막는다 — 도중에 멈추면 반만 찍힌 글라스가 남는다.</summary>
        [Fact]
        public void 버퍼가_모자라면_단계를_만들지_않는다()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                PrintPassSequence.Embedded(null!, null!, new PrintRunOptions
                {
                    SwathCount = 3,
                    Job        = new BufferRecordingJob(),
                    BufferIds  = new uint[] { 11 },      // 3장 필요한데 1개
                }, 1));

            Assert.Contains("버퍼가 모자랍니다", ex.Message);
        }

        // ── 번호 ─────────────────────────────────────────────────────────

        [Fact]
        public void 번호는_받은_자리에서_이어진다()
        {
            var steps = PrintPassSequence.Embedded(null!, null!,
                new PrintRunOptions { SwathCount = 2, Job = new RecordingJob(), BufferIds = Buffers }, 10);

            Assert.Equal(10, steps[0].Number);
            Assert.Equal(Enumerable.Range(10, steps.Count), steps.Select(s => s.Number));
        }
    }
}
