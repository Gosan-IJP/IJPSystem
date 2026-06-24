namespace IJPSystem.Machines.Pulse
{
    // 도어 감지 — IO.json(COMIZOA ETS-D08MN): DI_DOOR(Door Sensor)만 존재.
    // ※ 도어 개폐 액추에이터 출력은 IO.json에 없으므로 OpenDoor/CloseDoor는 구동 대상이 없다(인터페이스 계약 유지용).
    public partial class PulseMachine
    {
        private static partial class DI
        {
            public const string DOOR = "DI_DOOR"; // X200 Door Sensor (N.C)
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
