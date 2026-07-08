namespace IJPSystem.Machines.Pulse
{
    // 도어 감지 — 현재 IO.json(ETS-D08MN 8/7점)에는 도어 센서/개폐 출력이 없다.
    // ※ IsDoorLocked()는 항상 false, OpenDoor/CloseDoor는 no-op(인터페이스 계약 유지용).
    //   도어 IO가 추가되면 IO.json 에 등록 후 아래 상수를 실제 Index 로 교체할 것.
    public partial class PulseMachine
    {
        private static partial class DI
        {
            public const string DOOR = "DI_DOOR";
        }

        // IO.json에 도어 개폐 출력이 없어 실제 제어 대상 없음
        public void OpenDoor()  { /* no door actuator on this hardware */ }
        public void CloseDoor() { /* no door actuator on this hardware */ }

        // 도어 닫힘/잠금 상태 — 도어 센서로 판단
        public bool IsDoorLocked()
            => IO?.GetInput(DI.DOOR) ?? false;

        // ── 시뮬레이션 전용 ──
        public void SimulateDoorLockAfter(int delayMs)
            => IO?.ScheduleInput(DI.DOOR, true, delayMs);
    }
}
