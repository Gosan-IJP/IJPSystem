namespace IJPSystem.Machines.Pulse
{
    // Chuck / DMD 진공 제어 + Glass(척 안착) 감지
    // IO 인덱스는 Config/IO.json(COMIZOA ETS-D08MN 실배선) 기준.
    public partial class PulseMachine
    {
        private static partial class DI
        {
            // 진공 압력 스위치(Regulator Pressure Switch) — 진공이 형성되면 ON
            public const string PRESS_SW_CHUCK_VAC = "DI_PRESS_SW_CHUCK_VAC"; // X000
            public const string PRESS_SW_DMD_VAC   = "DI_PRESS_SW_DMD_VAC";   // X001
        }
        private static partial class DO
        {
            public const string CHUCK_VAC_VALVE = "DO_CHUCK_VAC_VALVE"; // Y000 Chuck Table Vacuum Sol' Valve
            public const string DMD_VAC_VALVE   = "DO_DMD_VAC_VALVE";   // Y001 DMD Vacuum Sol' Valve
        }

        // ── Chuck 진공 제어 ──
        public void VacuumOn()
        {
            IO?.SetOutput(DO.CHUCK_VAC_VALVE, true);
            IO?.SetOutput(DO.DMD_VAC_VALVE,   true);
        }

        public void VacuumOff()
        {
            IO?.SetOutput(DO.CHUCK_VAC_VALVE, false);
            IO?.SetOutput(DO.DMD_VAC_VALVE,   false);
        }

        // ── Glass(척 안착) 감지 ──
        // 전용 글라스 센서가 없는 구성이므로, 척 진공 압력 스위치로 글라스 안착(진공 형성)을 판단한다.
        public bool IsGlassDetected()
            => IO?.GetInput(DI.PRESS_SW_CHUCK_VAC) ?? false;
    }
}
