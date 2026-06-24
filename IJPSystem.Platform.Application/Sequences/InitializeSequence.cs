using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <remarks>
    /// SequenceStepDef.Name 은 표시용 **번역 키** (Step_Init_*).
    /// HMI 의 ViewModel 이 Loc.T(key) 로 사용자 언어로 변환.
    /// </remarks>
    public static class InitializeSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion) => new[]
        {
            new SequenceStepDef(1, "Step_Init_ServoOn",
                ct => motion.ServoOnAllAsync()),

            new SequenceStepDef(2, "Step_Init_Homing",
                ct => motion.HomeAllAsync(ct)),

            new SequenceStepDef(3, "Step_Init_MoveReady",
                ct => motion.MoveToPointAsync(PointNames.Ready, ct)),

            new SequenceStepDef(4, "Step_Init_MoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 30_000, ct)),

            new SequenceStepDef(5, "Step_Init_MoveLoad",
                ct => motion.MoveToPointAsync(PointNames.Load, ct)),

            new SequenceStepDef(6, "Step_Init_MoveLoadDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),
        };
    }
}
