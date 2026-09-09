using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.Domain.Models.Printing;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>
    /// 인쇄가 실제로 나가는 데 필요한 것들 — 오토프린트와 패턴프린트가 <b>같이 쓴다</b>.
    ///
    /// <para>두 화면의 시퀀스는 얼라인 사용 여부만 다르고 나머지(진공·헤드 업다운·Meteor 준비·
    /// 스와스 루프)는 같다. 그래서 알맹이를 여기 한 벌만 두고 부르는 쪽이 번호를 이어 붙인다
    /// — <see cref="GlassAlignSequence.Embedded"/> 와 같은 방식이다.</para>
    /// </summary>
    public sealed class PrintRunOptions
    {
        /// <summary>스와스(패스) 수. 1 이면 편도 한 번.</summary>
        public int SwathCount { get; init; } = 1;

        /// <summary>패스 사이 X축 스텝오버[mm]. 0 이면 스텝 단계를 만들지 않는다.</summary>
        public double SwathPitchMm { get; init; }

        /// <summary>
        /// 정방향 스와스에서 스캔축이 갈 거리[mm]. <b>부호가 방향</b>이다(+면 좌표가 커지는 쪽).
        ///
        /// <para><b>0 이면 티칭된 PRINT END 까지 간다</b> — Dry Run 이 그렇다. 찍을 데이터가
        /// 없으니 어디까지 갈지 정할 근거도 없고, 그때는 사람이 잡아 둔 자리가 답이다.</para>
        ///
        /// <para>Print Run 은 값이 들어온다 — <b>패턴 길이가 곧 주행 거리</b>다
        /// (스텝 수 × 스캔 스텝). 끝점을 따로 티칭하면 진실이 둘이 되어, 패턴을 바꾼 뒤
        /// 티칭이 낡아도 아무도 모르는 채 뒷부분이 안 찍힌다.</para>
        /// </summary>
        public double ScanTravelMm { get; init; }

        /// <summary>양방향이면 패스마다 방향을 교대한다. 단방향이면 매번 복귀 후 같은 방향.</summary>
        public bool Bidirectional { get; init; } = true;

        /// <summary>
        /// 인쇄 명령을 낼 상대. <b>null 이면 Dry Run</b> — 모션은 그대로 돌고 잉크만 안 나간다.
        /// 단계 목록에서도 Meteor 단계가 통째로 빠지므로, 화면이 곧 무엇이 도는지를 말해 준다.
        /// </summary>
        public IPrintJobCommands? Job { get; init; }

        /// <summary>
        /// 올려 둔 인쇄 데이터의 버퍼 번호들 — <b>스와스 × 패스, 스와스 우선</b> 순서.
        /// Print Run 이면 <c>SwathCount × PassCount</c> 개가 있어야 한다.
        ///
        /// <para><b>번호가 하나가 아닌 이유</b>: 스와스마다 그림이 다르다. 왼쪽 폭과 그다음
        /// 폭은 다른 데이터라, 번호 하나를 되풀이 보내면 같은 그림이 옆으로 여러 번 찍힌다 —
        /// 덜 찍히는 것보다 나쁘다(눈에 안 띄고 잉크·글라스를 버린다).</para>
        /// </summary>
        public IReadOnlyList<uint> BufferIds { get; init; } = Array.Empty<uint>();

        /// <summary>
        /// 스와스 하나 안에서 도는 인터레이스 패스 수. 노즐 피치를 못 줄이므로 헤드를
        /// 피치의 1/N 씩 옮겨 N 번 지나간다 — <b>스와스와 다른 축</b>이다.
        /// </summary>
        public int PassCount { get; init; } = 1;

        /// <summary>인터레이스 패스 사이 크로스스캔 이동량[mm]. 1패스면 0.</summary>
        public double PassPitchMm { get; init; }

        /// <summary>인쇄 폭[화소] — 명령의 Width. 보통 패턴의 노즐 수.</summary>
        public int WidthPx { get; init; }

        /// <summary>색상 plane(1~). 우리 구성은 단일 plane 이라 1.</summary>
        public int Plane { get; init; } = 1;

        /// <summary>인쇄 작업 번호 — 엔진 로그와 대조할 때 쓴다.</summary>
        public int JobId { get; init; } = 1;

        /// <summary>Dry Run 인가 — 인쇄 명령 상대가 없으면 그렇다.</summary>
        public bool IsDryRun => Job == null;
    }

    /// <summary>
    /// 스캔 주행(스와스 루프)과 그 앞뒤의 Meteor 명령.
    ///
    /// <para><b>명령이 이동보다 먼저다.</b> 스캔 프린터는 엔코더 위치·방향으로 트리거가 자동
    /// 생성되므로(SDK 매뉴얼 6.2.1), 문서를 큐에 넣어 둔 상태에서 스캔축이 움직이기 시작하면
    /// 그 순간 발사된다. 순서가 반대면 이미 지나간 자리에 찍으라는 명령이 된다.</para>
    /// </summary>
    public static class PrintPassSequence
    {
        private const string ScanAxisNo = "Y";   // 프린트 스캔 축(스테이지 이송)
        private const string StepAxisNo = "X";   // 헤드 스텝오버 축(크로스스캔)

        /// <summary>
        /// 인쇄 시퀀스에 끼워 넣을 주행 단계 — 번호는 부르는 쪽이 이어서 매긴다.
        /// </summary>
        /// <param name="startNumber">이 묶음의 첫 단계 번호.</param>
        public static IReadOnlyList<SequenceStepDef> Embedded(
            IMachine machine, IMotionService motion, PrintRunOptions opts, int startNumber)
        {
            if (opts == null) throw new ArgumentNullException(nameof(opts));

            var steps = new List<SequenceStepDef>();
            int n = startNumber - 1;
            int swaths = Math.Max(1, opts.SwathCount);
            var job = opts.Job;

            // 작업 시작 — 캐리지가 인쇄 원점에 있는 지금이 X 좌표의 기준점이다(PiSetHome).
            // Dry Run 이면 이 단계가 생기지 않는다.
            if (job != null)
            {
                steps.Add(new SequenceStepDef(++n, "Step_Print_StartJob",
                    ct => { job.StartJob(opts.JobId); return Task.CompletedTask; }));
            }

            // ★루프가 둘이다 — 바깥은 스와스(옆자리를 덮는다), 안쪽은 인터레이스 패스(같은
            //   자리를 촘촘하게 만든다). 이동량이 서로 다르다: 스와스는 헤드 한 폭(수십 mm),
            //   패스는 노즐 피치의 1/N(수십 µm). 하나로 뭉치면 둘 중 하나가 틀린 거리로 간다.
            int passes = Math.Max(1, opts.PassCount);
            int traverse = 0;   // 지금까지 몇 번 왕복했는가 — 양방향 교대는 이 수로 정한다

            for (int swath = 1; swath <= swaths; swath++)
            {
                for (int pass = 1; pass <= passes; pass++)
                {
                    bool forward = opts.Bidirectional ? (traverse % 2 == 0) : true;
                    traverse++;
                    string scanTarget = forward ? PointNames.PrintEnd : PointNames.PrintOrigin;

                    // ★이동보다 먼저 — 트리거가 엔코더로 자동 발생하므로 데이터가 먼저 큐에 있어야 한다.
                    if (job != null)
                    {
                        bool fwd = forward;
                        // 스와스 우선 순서로 담겨 있다 — [스와스0 패스0, 스와스0 패스1, 스와스1 패스0, …]
                        int index = (swath - 1) * passes + (pass - 1);
                        uint buffer = index < opts.BufferIds.Count
                            ? opts.BufferIds[index]
                            : throw new InvalidOperationException(
                                  $"버퍼가 모자랍니다 — 스와스 {swath}/{swaths}, 패스 {pass}/{passes} 에 " +
                                  $"{index + 1}번째가 필요한데 {opts.BufferIds.Count}개뿐입니다.");

                        steps.Add(new SequenceStepDef(++n, "Step_Print_StartSwath",
                            ct =>
                            {
                                job.StartSwath(fwd);
                                job.SendImage(buffer, opts.Plane, xLeftPx: 0, yTopPx: 0, widthPx: opts.WidthPx);
                                job.EndSwath();
                                return Task.CompletedTask;
                            }));
                    }

                    // ★끝점을 누가 정하는가가 운전 모드로 갈린다.
                    //   Print Run  거리가 들어온다 — 패턴 길이만큼만 간다(데이터가 정한다)
                    //   Dry Run    거리가 0 이다 — 티칭된 PRINT END 로 간다(사람이 정한다)
                    double travel = opts.ScanTravelMm;
                    steps.Add(new SequenceStepDef(++n, "Step_Print_Scan",
                        travel != 0
                            ? ct => motion.MoveAxisRelativeAsync(
                                        ScanAxisNo, forward ? travel : -travel, ct, MotionProfileKind.Printing)
                            : ct => motion.MoveAxisToPointAsync(
                                        ScanAxisNo, scanTarget, ct, MotionProfileKind.Printing)));

                    steps.Add(new SequenceStepDef(++n, "Step_Print_ScanDone",
                        ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));

                    // 단방향: 인쇄 후 시작점으로 복귀(비인쇄, Move 프로파일).
                    // 거리로 왔으면 거리로 돌아간다 — 티칭 점으로 돌아가면 방금 간 만큼과 어긋난다.
                    if (!opts.Bidirectional)
                    {
                        string returnTarget = forward ? PointNames.PrintOrigin : PointNames.PrintEnd;
                        steps.Add(new SequenceStepDef(++n, "Step_Print_Return",
                            travel != 0
                                ? ct => motion.MoveAxisRelativeAsync(
                                            ScanAxisNo, forward ? -travel : travel, ct, MotionProfileKind.Move)
                                : ct => motion.MoveAxisToPointAsync(
                                            ScanAxisNo, returnTarget, ct, MotionProfileKind.Move)));

                        steps.Add(new SequenceStepDef(++n, "Step_Print_ReturnDone",
                            ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));
                    }

                    // 인터레이스: 다음 패스로 피치의 1/N 만큼(헤드는 내린 채).
                    if (pass < passes && opts.PassPitchMm > 0)
                    {
                        steps.Add(new SequenceStepDef(++n, "Step_Print_PassStep",
                            ct => motion.MoveAxisRelativeAsync(StepAxisNo, opts.PassPitchMm, ct)));

                        steps.Add(new SequenceStepDef(++n, "Step_Print_PassStepDone",
                            ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));
                    }
                }

                // 다음 스와스로 헤드 한 폭 — 단, 인터레이스로 이미 옮겨 온 만큼은 뺀다.
                // 안 빼면 패스를 돌 때마다 스와스가 조금씩 오른쪽으로 밀린다.
                if (swath < swaths && opts.SwathPitchMm > 0)
                {
                    double step = opts.SwathPitchMm - (passes - 1) * opts.PassPitchMm;
                    steps.Add(new SequenceStepDef(++n, "Step_Print_SwathStep",
                        ct => motion.MoveAxisRelativeAsync(StepAxisNo, step, ct)));

                    steps.Add(new SequenceStepDef(++n, "Step_Print_SwathStepDone",
                        ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));
                }
            }

            if (job != null)
            {
                steps.Add(new SequenceStepDef(++n, "Step_Print_EndJob",
                    ct => { job.EndJob(); return Task.CompletedTask; }));
            }

            return steps;
        }
    }
}
