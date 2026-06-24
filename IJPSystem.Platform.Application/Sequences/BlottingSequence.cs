using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    public static class BlottingSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion) => new[]
        {
            new SequenceStepDef(1, "Step_Blotting_VacuumOff",
                ct =>
                {
                    machine.VacuumOff();
                    machine.IO.ScheduleInput("DI_PRESS_SW_CHUCK_VAC", false, 500);
                    return WaitHelper.ForIOSignal(machine.IO, "DI_PRESS_SW_CHUCK_VAC",
                                                 expected: false, timeoutMs: 10_000, ct);
                }),

            new SequenceStepDef(2, "Step_Blotting_MoveBlotting",
                ct => motion.MoveToPointAsync(PointNames.Blotting, ct)),

            new SequenceStepDef(3, "Step_Blotting_MoveBlottingDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),

            new SequenceStepDef(4, "Step_Blotting_Wait",
                ct => Task.Delay(3_000, ct)),

            new SequenceStepDef(5, "Step_Blotting_MoveReady",
                ct => motion.MoveToPointAsync(PointNames.Ready, ct)),

            new SequenceStepDef(6, "Step_Blotting_MoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),
        };
    }
}
