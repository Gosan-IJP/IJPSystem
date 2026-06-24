namespace IJPSystem.Machines.Pulse
{
    // 안전 센서 계통 (EMO, Pressure Switch) — IO.json(COMIZOA ETS-D08MN) 실배선 기준
    public partial class PulseMachine
    {
        private static partial class DI
        {
            public const string EMO = "DI_EMO"; // X004 EMO (N.C)

            // 압력 스위치 (1-based index) — IO.json 실배선 4개
            public static readonly string[] PRESSURE_SW =
            {
                "",                          // [0] 미사용
                "DI_PRESS_SW_CHUCK_VAC",     // [1] X000 Vacuum, Chuck
                "DI_PRESS_SW_DMD_VAC",       // [2] X001 Vacuum, DMD
                "DI_PRESS_SW_3WAY",          // [3] X002 Positive, 3Way Valve
                "DI_PRESS_SW_HEAD_MODULE",   // [4] X003 Positive, Head Module Valve
            };
        }

        // ── EMO (비상정지) ──
        public bool IsEmoActive()
            => IO?.GetInput(DI.EMO) ?? false;

        // ── Pressure Switch ──
        public bool IsPressureOk(int swNo)
        {
            if (swNo < 1 || swNo >= DI.PRESSURE_SW.Length) return false;
            return IO?.GetInput(DI.PRESSURE_SW[swNo]) ?? false;
        }
    }
}
