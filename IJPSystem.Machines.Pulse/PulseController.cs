using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;

namespace IJPSystem.Machines.Inkjet5G
{
    public class InkjetController
    {
        private readonly IMachine _machine;

        public MachineState CurrentState { get; private set; } = MachineState.Idle;

        public InkjetController(IMachine machine)
        {
            _machine = machine;
        }

        public IMachine GetMachine() => _machine; 

        // ── 인터락 (장비 수준 정책) ──

        // 어떤 동작이든 시작하기 위한 최소 안전 조건:
        // EMO가 눌려 있지 않고, 모든 도어가 잠겨 있어야 함
        public bool CanOperate()
            => !_machine.IsEmoActive() && _machine.IsDoorLocked();

        // 자동 시퀀스 시작 가능 여부:
        // 안전 조건 충족 + 현재 Idle 상태 (Running/Alarm 중 새 시퀀스 진입 차단)
        public bool CanStartSequence()
            => CanOperate() && CurrentState == MachineState.Idle;
    }
}
