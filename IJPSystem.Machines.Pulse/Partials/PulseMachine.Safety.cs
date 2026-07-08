namespace IJPSystem.Machines.Pulse
{
    // 안전 센서 계통 (EMO, Pressure Switch) — IO.json(COMIZOA ETS-D08MN) 실배선 기준
    public partial class PulseMachine
    {
        private static partial class DI
        {
            // ※ 현재 IO.json(ETS-D08MN 8/7점)에는 EMO 입력이 없다 → IsEmoActive()는 항상 false.
            //   EMO 배선이 추가되면 IO.json 에 등록 후 이 상수를 실제 Index 로 교체할 것.
            public const string EMO = "DI_EMO";

            // 압력 스위치 (1-based index, swNo). 값은 IO.json Index 와 일치해야 한다.
            // 호출부(MainDashboard)는 swNo 1~3 을 확인한다.
            public static readonly string[] PRESSURE_SW =
            {
                "",                          // [0] 미사용
                "DI_PRESS_SW_CHUCK_VAC",     // [1] X000 Vacuum, Chuck
                "DI_PRESS_SW_DMPC_VAC",      // [2] X001 Vacuum, DMPC
                "DI_PRESS_SW_3WAY",          // [3] X003 Positive, 3Way Valve
                "DI_PRESS_SW_HEAD",          // [4] X002 Positive, Head
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
