using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Auto Print — 자동 인쇄 시퀀스</summary>
    /// <remarks>
    /// SequenceStepDef.Name 은 표시용 **번역 키** (Step_AutoPrint_*).
    /// HMI 의 ViewModel 이 Loc.T(key) 로 사용자 언어로 변환해서 화면에 표시.
    /// 키→번역은 Common/Resources/Languages/ko-KR.xaml, en-US.xaml 참조.
    /// </remarks>
    public static class AutoPrintSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion) => new[]
        {
            // IO.json에 글라스 전용 센서가 없으므로 수동 로드 안착 대기. 글라스 클램프 여부는 이후 척 진공 압력으로 확인.
            new SequenceStepDef(1, "Step_AutoPrint_WaitGlass",
                ct => Task.Delay(1_500, ct)),

            // 진공 ON — VacuumConfirm(압력스위치 확인) 단계는 제외됨(사용자 요청). 안정화 대기로 이어감.
            new SequenceStepDef(2, "Step_AutoPrint_VacuumOn",
                ct =>
                {
                    machine.VacuumOn();
                    return Task.CompletedTask;
                }),

            new SequenceStepDef(3, "Step_AutoPrint_VacuumStabilize",
                ct => Task.Delay(1_000, ct)),

            new SequenceStepDef(4, "Step_AutoPrint_MoveStart",
                ct => motion.MoveToPointAsync(PointNames.PrintStart, ct)),

            new SequenceStepDef(5, "Step_AutoPrint_MoveStartDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),

            // ── 프린트 헤드 DOWN (글래스 가까이) ──
            new SequenceStepDef(6, "Step_AutoPrint_HeadDown",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadDown, ct)),

            new SequenceStepDef(7, "Step_AutoPrint_HeadDownDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)),

            new SequenceStepDef(8, "Step_AutoPrint_Print",
                ct => motion.MoveToPointAsync(PointNames.PrintEnd, ct, MotionProfileKind.Printing)),

            new SequenceStepDef(9, "Step_AutoPrint_PrintDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)),

            // ── 프린트 헤드 UP (글래스에서 떼기) ──
            new SequenceStepDef(10, "Step_AutoPrint_HeadUp",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadUp, ct)),

            new SequenceStepDef(11, "Step_AutoPrint_HeadUpDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)),

            new SequenceStepDef(12, "Step_AutoPrint_VacuumOff",
                ct =>
                {
                    machine.VacuumOff();
                    // Virtual 모드에서 압력스위치 OFF 를 시뮬레이션(실장 드라이버는 no-op → 물리 신호가 자연 발생).
                    // 제거하면 Virtual 모드에서 이 단계가 타임아웃 실패한다.
                    machine.IO.ScheduleInput("DI_PRESS_SW_CHUCK_VAC", false, 200);
                    return WaitHelper.ForIOSignal(machine.IO, "DI_PRESS_SW_CHUCK_VAC",
                                                 expected: false, timeoutMs: 10_000, ct);
                }),

            new SequenceStepDef(13, "Step_AutoPrint_MoveReady",
                ct => motion.MoveToPointAsync(PointNames.Ready, ct)),

            new SequenceStepDef(14, "Step_AutoPrint_MoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),
        };
    }
}
