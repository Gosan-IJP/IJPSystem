using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Auto Print — 자동 인쇄 시퀀스 (멀티 스와스 지원)</summary>
    /// <remarks>
    /// SequenceStepDef.Name 은 표시용 **번역 키** (Step_AutoPrint_*).
    /// HMI 의 ViewModel 이 Loc.T(key) 로 사용자 언어로 변환해서 화면에 표시.
    /// 키→번역은 Common/Resources/Languages/ko-KR.xaml, en-US.xaml 참조.
    ///
    /// 프린팅수(swathCount) — 기타정보 화면 설정:
    ///   1 : PrintStart→End 1회 프린트 후 Ready (기본/현행)
    ///   2 : Start→End 프린트 → X축 +headLength 스텝오버 → End→Start 프린트 → Ready
    ///   3 : Start→End → 스텝 → End→Start → 스텝 → Start→End → Ready
    ///   N : 패스 N회, 패스 사이 X축 +headLength 스텝.
    ///   ※ 헤드(Z)는 첫 패스 전 1회 다운, 마지막 패스 후 1회 업(내린 채 스텝오버).
    ///   ※ 스캔은 Y축(스테이지), 스텝오버는 X축(크로스스캔). 스캔은 단일 축 이동으로 X 유지.
    ///
    /// 프린팅 방향(bidirectional):
    ///   양방향(true) : 패스마다 방향 교대(홀수 Start→End / 짝수 End→Start). 복귀 없이 왕복하며 인쇄.
    ///   단방향(false): 매 패스 Start→End 인쇄 후, End→Start 로 복귀(Move 프로파일=비인쇄). 항상 같은 방향으로만 인쇄.
    /// </remarks>
    public static class AutoPrintSequence
    {
        private const string ScanAxisNo = "Y";   // 프린트 스캔 축(스테이지 이송)
        private const string StepAxisNo = "X";   // 해드 스텝오버 축(크로스스캔)

        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, int swathCount = 1, double headLength = 0,
            bool bidirectional = true)
        {
            if (swathCount < 1) swathCount = 1;

            var steps = new List<SequenceStepDef>();
            int n = 0;

            // IO.json에 글라스 전용 센서가 없으므로 수동 로드 안착 대기. 글라스 클램프 여부는 이후 척 진공 압력으로 확인.
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_WaitGlass",
                ct => Task.Delay(1_500, ct)));

            // 진공 ON — VacuumConfirm(압력스위치 확인) 단계는 제외됨(사용자 요청). 안정화 대기로 이어감.
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumOn",
                ct => { machine.VacuumOn(); return Task.CompletedTask; }));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumStabilize",
                ct => Task.Delay(1_000, ct)));

            // ── 글라스 자동 정렬 ──────────────────────────────────────
            // 진공으로 글라스를 붙든 뒤, 인쇄 시작 위치로 가기 전에 넣는다 —
            // 정렬은 글라스가 척에 고정된 상태에서만 뜻이 있고, 정렬로 옮긴 자리를
            // 인쇄 이동이 덮어쓰면 안 되기 때문이다.
            //
            // 레시피에서 [미사용]이면 단계 자체가 생기지 않는다(GlassAlignSequence.Embedded).
            foreach (var s in GlassAlignSequence.Embedded(machine, GlassAlignServices.Current, n + 1))
            {
                steps.Add(s);
                n = s.Number;
            }

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveStart",
                ct => motion.MoveToPointAsync(PointNames.PrintOrigin, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveStartDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));

            // ── 프린트 헤드 DOWN (글래스 가까이) — 멀티 스와스여도 1회만 ──
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadDown",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadDown, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadDownDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)));

            // ── 스와스 패스 루프 ──
            // 양방향: 방향 교대(홀수=Start→End, 짝수=End→Start), 복귀 없음.
            // 단방향: 매 패스 Start→End 인쇄 후 End→Start 복귀(Move=비인쇄) → 항상 같은 방향으로 인쇄.
            // 스캔은 Y축 단일 이동(스텝오버로 옮긴 X 위치 유지). 패스 사이 X축 +headLength 스텝.
            for (int pass = 1; pass <= swathCount; pass++)
            {
                // 인쇄 주행 방향: 양방향은 패스마다 교대, 단방향은 항상 Start→End.
                bool forward = bidirectional ? (pass % 2 == 1) : true;
                string scanTarget = forward ? PointNames.PrintEnd : PointNames.PrintOrigin;

                steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_Print",
                    ct => motion.MoveAxisToPointAsync(ScanAxisNo, scanTarget, ct, MotionProfileKind.Printing)));

                steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_PrintDone",
                    ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));

                // 단방향: 인쇄 후 시작점으로 복귀(비인쇄, Move 프로파일). 다음 패스도 같은 방향으로 인쇄하기 위함.
                if (!bidirectional)
                {
                    string returnTarget = forward ? PointNames.PrintOrigin : PointNames.PrintEnd;
                    steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_Return",
                        ct => motion.MoveAxisToPointAsync(ScanAxisNo, returnTarget, ct, MotionProfileKind.Move)));

                    steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_ReturnDone",
                        ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));
                }

                // 마지막 패스가 아니면 X축을 헤드길이만큼 +방향 스텝오버(헤드는 내린 채).
                if (pass < swathCount && headLength > 0)
                {
                    steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_SwathStep",
                        ct => motion.MoveAxisRelativeAsync(StepAxisNo, headLength, ct)));

                    steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_SwathStepDone",
                        ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));
                }
            }

            // ── 프린트 헤드 UP + READY 복귀 (동시) ──
            // 헤드 상승(Z)과 스테이지 복귀(Y)는 서로 다른 축이라 순차로 기다릴 이유가 없다.
            // 두 포인트 이동을 같이 걸고 한 번에 대기해 사이클 타임을 줄인다.
            // ※ 진공 해제(VacuumOff)는 복귀 이후로 옮겼다 — 이동 중에도 글래스를 척에 붙들고 있어
            //   가감속에 밀릴 여지가 없다(기존에는 해제 후 이동).
            // ※ 전제: READY 포인트에 Z 가 사용축으로 포함되어 있지 않을 것.
            //   포함되면 Z 에 두 이동 명령이 겹친다(레시피 티칭에서 확인).
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadUpAndMoveReady",
                async ct => await Task.WhenAll(
                    motion.MoveToPointAsync(PointNames.PrintHeadUp, ct),
                    motion.MoveToPointAsync(PointNames.Ready, ct))));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadUpAndMoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumOff",
                ct =>
                {
                    machine.VacuumOff();
                    // Virtual 모드에서 압력스위치 OFF 를 시뮬레이션(실장 드라이버는 no-op → 물리 신호가 자연 발생).
                    // 제거하면 Virtual 모드에서 이 단계가 타임아웃 실패한다.
                    machine.IO.ScheduleInput("DI_PRESS_SW_CHUCK_VAC", false, 200);
                    return WaitHelper.ForIOSignal(machine.IO, "DI_PRESS_SW_CHUCK_VAC",
                                                 expected: false, timeoutMs: 10_000, ct);
                }));

            return steps;
        }
    }
}
