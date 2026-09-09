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

        /// <summary>양방향이면 패스마다 방향을 교대한다. 단방향이면 매번 복귀 후 같은 방향.</summary>
        public bool Bidirectional { get; init; } = true;

        /// <summary>
        /// 인쇄 명령을 낼 상대. <b>null 이면 Dry Run</b> — 모션은 그대로 돌고 잉크만 안 나간다.
        /// 단계 목록에서도 Meteor 단계가 통째로 빠지므로, 화면이 곧 무엇이 도는지를 말해 준다.
        /// </summary>
        public IPrintJobCommands? Job { get; init; }

        /// <summary>올려 둔 인쇄 데이터의 버퍼 번호. Print Run 이면 반드시 있어야 한다.</summary>
        public uint BufferId { get; init; }

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

            for (int pass = 1; pass <= swaths; pass++)
            {
                bool forward = opts.Bidirectional ? (pass % 2 == 1) : true;
                string scanTarget = forward ? PointNames.PrintEnd : PointNames.PrintOrigin;

                // ★이동보다 먼저 — 트리거가 엔코더로 자동 발생하므로 데이터가 먼저 큐에 있어야 한다.
                if (job != null)
                {
                    bool fwd = forward;
                    steps.Add(new SequenceStepDef(++n, "Step_Print_StartSwath",
                        ct =>
                        {
                            job.StartSwath(fwd);
                            job.SendImage(opts.BufferId, opts.Plane, xLeftPx: 0, yTopPx: 0, widthPx: opts.WidthPx);
                            job.EndSwath();
                            return Task.CompletedTask;
                        }));
                }

                steps.Add(new SequenceStepDef(++n, "Step_Print_Scan",
                    ct => motion.MoveAxisToPointAsync(ScanAxisNo, scanTarget, ct, MotionProfileKind.Printing)));

                steps.Add(new SequenceStepDef(++n, "Step_Print_ScanDone",
                    ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));

                // 단방향: 인쇄 후 시작점으로 복귀(비인쇄, Move 프로파일).
                if (!opts.Bidirectional)
                {
                    string returnTarget = forward ? PointNames.PrintOrigin : PointNames.PrintEnd;
                    steps.Add(new SequenceStepDef(++n, "Step_Print_Return",
                        ct => motion.MoveAxisToPointAsync(ScanAxisNo, returnTarget, ct, MotionProfileKind.Move)));

                    steps.Add(new SequenceStepDef(++n, "Step_Print_ReturnDone",
                        ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));
                }

                // 마지막 패스가 아니면 X축을 헤드길이만큼 스텝오버(헤드는 내린 채).
                if (pass < swaths && opts.SwathPitchMm > 0)
                {
                    steps.Add(new SequenceStepDef(++n, "Step_Print_SwathStep",
                        ct => motion.MoveAxisRelativeAsync(StepAxisNo, opts.SwathPitchMm, ct)));

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
