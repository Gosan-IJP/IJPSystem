using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>
    /// 인쇄 한 사이클의 알맹이 — 오토프린트와 패턴프린트가 <b>같은 것</b>을 쓴다.
    ///
    /// <para><b>왜 하나인가</b>: 글라스 진공 온오프 · 헤드 업다운 · Meteor 준비 · 스와스 루프는
    /// 두 화면 모두에 필요하다. 실제로 다른 것은 <b>얼라인을 하느냐</b> 하나뿐이다. 두 벌로
    /// 두면 인쇄에 관한 지식이 두 파일에 갈라져, 한쪽만 고치는 일이 반드시 생긴다.
    /// (GlassAlignSequence 가 Definitions 한 벌에 Build/Embedded 두 입구를 둔 것과 같은 방식)</para>
    ///
    /// <para>번호는 <see cref="SequenceStepDef.Number"/> 로 1부터 이어 붙인다 — 중간 묶음이
    /// 비어도(얼라인 미사용·Dry Run) 번호가 끊기지 않는다.</para>
    /// </summary>
    public static class PrintCycleSequence
    {
        /// <param name="useGlassAlign">
        /// 얼라인 묶음을 넣을지. <b>두 시퀀스의 유일한 차이</b>다.
        /// 넣더라도 레시피가 [미사용]이면 <see cref="GlassAlignSequence.Embedded"/> 가 비운다.
        /// </param>
        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, PrintRunOptions opts, bool useGlassAlign)
        {
            var steps = new List<SequenceStepDef>();
            int n = 0;

            // IO.json 에 글라스 전용 센서가 없으므로 수동 로드 안착 대기.
            // 클램프 여부는 이후 척 진공 압력으로 확인한다.
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_WaitGlass",
                ct => Task.Delay(1_500, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumOn",
                ct => { machine.VacuumOn(); return Task.CompletedTask; }));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumStabilize",
                ct => Task.Delay(1_000, ct)));

            // ── 글라스 자동 정렬 ──────────────────────────────────────
            // 진공으로 글라스를 붙든 뒤, 인쇄 시작 위치로 가기 전에 넣는다 — 정렬은 글라스가
            // 척에 고정된 상태에서만 뜻이 있고, 정렬로 옮긴 자리를 인쇄 이동이 덮어쓰면 안 된다.
            if (useGlassAlign)
            {
                foreach (var s in GlassAlignSequence.Embedded(machine, GlassAlignServices.Current, n + 1))
                {
                    steps.Add(s);
                    n = s.Number;
                }
            }

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveStart",
                ct => motion.MoveToPointAsync(PointNames.PrintOrigin, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveStartDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));

            // ── 프린트 헤드 DOWN — 멀티 스와스여도 1회만(내린 채 스텝오버) ──
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadDown",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadDown, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadDownDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)));

            // ── 인쇄 주행(스와스 루프 + Meteor 명령) ──
            // Dry Run 이면 Meteor 단계 없이 모션만 들어온다.
            foreach (var s in PrintPassSequence.Embedded(machine, motion, opts, n + 1))
            {
                steps.Add(s);
                n = s.Number;
            }

            // ── 헤드 UP + READY 복귀 (동시) ──
            // 헤드 상승(Z)과 스테이지 복귀(Y)는 다른 축이라 순차로 기다릴 이유가 없다.
            // ※ 전제: READY 포인트에 Z 가 사용축으로 포함되어 있지 않을 것 — 포함되면 Z 에
            //   두 이동 명령이 겹친다(레시피 티칭에서 확인).
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadUpAndMoveReady",
                async ct => await Task.WhenAll(
                    motion.MoveToPointAsync(PointNames.PrintHeadUp, ct),
                    motion.MoveToPointAsync(PointNames.Ready, ct))));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadUpAndMoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));

            // ※ 진공 해제는 복귀 이후다 — 이동 중에도 글라스를 척에 붙들고 있어 가감속에
            //   밀릴 여지가 없다(기존에는 해제 후 이동이었다).
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumOff",
                ct =>
                {
                    machine.VacuumOff();
                    // Virtual 모드에서 압력스위치 OFF 를 시뮬레이션(실장 드라이버는 no-op).
                    // 제거하면 Virtual 모드에서 이 단계가 타임아웃 실패한다.
                    machine.IO.ScheduleInput("DI_PRESS_SW_CHUCK_VAC", false, 200);
                    return WaitHelper.ForIOSignal(machine.IO, "DI_PRESS_SW_CHUCK_VAC",
                                                 expected: false, timeoutMs: 10_000, ct);
                }));

            return steps;
        }
    }
}
