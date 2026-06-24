using IJPSystem.Platform.Domain.Enums;

namespace IJPSystem.Machines.Inkjet5G
{
    // Tower Lamp + Buzzer (시스템 상태 표시)
    public partial class InkjetMachine
    {
        private static partial class DO
        {
            public const string LAMP_RED    = "DO_TOWER_LAMP_RED";
            public const string LAMP_YELLOW = "DO_TOWER_LAMP_YELLOW";
            public const string LAMP_GREEN  = "DO_TOWER_LAMP_GREEN";
            public const string BUZZER      = "DO_BUZZER";
        }

        public void SetSystemStatus(MachineState state)
        {
            if (IO == null) return;

            // 전부 끄고
            IO.SetOutput(DO.LAMP_RED,    false);
            IO.SetOutput(DO.LAMP_YELLOW, false);
            IO.SetOutput(DO.LAMP_GREEN,  false);
            IO.SetOutput(DO.BUZZER,      false);

            switch (state)
            {
                case MachineState.Alarm:
                    IO.SetOutput(DO.LAMP_RED, true);
                    break;
                case MachineState.Emergency:
                    IO.SetOutput(DO.LAMP_RED, true);
                    IO.SetOutput(DO.BUZZER,   true); // 비상정지 시 부저 추가
                    break;
                case MachineState.Running:
                    IO.SetOutput(DO.LAMP_GREEN, true);
                    break;
                case MachineState.Standby:
                case MachineState.Idle:
                    IO.SetOutput(DO.LAMP_YELLOW, true);
                    break;
            }
        }
    }
}
