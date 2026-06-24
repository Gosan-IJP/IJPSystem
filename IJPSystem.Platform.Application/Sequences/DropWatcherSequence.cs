using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Drop Watcher — 드랍와처 검사 시퀀스.</summary>
    /// <remarks>
    /// 1단계: 드랍와처 위치로 이동만 우선 구현.
    /// (조명/딜레이/측정 등 후속 단계는 추후 추가 예정)
    /// </remarks>
    public static class DropWatcherSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion) => new[]
        {
            new SequenceStepDef(1, "Step_DropWatcher_Move",
                ct => motion.MoveToPointAsync(PointNames.DropWatcher, ct)),

            new SequenceStepDef(2, "Step_DropWatcher_MoveDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),
        };
    }
}
