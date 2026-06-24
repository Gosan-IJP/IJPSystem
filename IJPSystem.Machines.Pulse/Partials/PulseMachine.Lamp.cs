using IJPSystem.Platform.Domain.Enums;

namespace IJPSystem.Machines.Pulse
{
    // Tower Lamp + Buzzer
    // ※ IO.json(COMIZOA ETS-D08MN)에는 타워램프/부저 출력이 없어 물리 출력 대상이 없다.
    //    인터페이스(IMachine.SetSystemStatus) 계약 유지용 — 시스템 상태 표시는 HMI 화면에서 처리한다.
    public partial class PulseMachine
    {
        public void SetSystemStatus(MachineState state)
        {
            // No tower-lamp / buzzer outputs on this hardware (see Config/IO.json).
        }
    }
}
