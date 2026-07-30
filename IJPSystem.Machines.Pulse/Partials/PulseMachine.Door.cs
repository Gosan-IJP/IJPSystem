namespace IJPSystem.Machines.Pulse
{
    // 도어 감지 — 2호기 정면 DOOR SW(IO.json X007 = DI_DOOR_FRONT)로 판단.
    // ※ 1호기 IO.json 에는 도어 Index 가 없어 GetInput→echo false 가 되지만,
    //   도어 인터록은 현재 가동을 막지 않으므로(CanOperate 미사용, CheckSafetyBeforeStart 도 도어 제외)
    //   동일 코드로 무해하다. 개폐 액추에이터 출력은 없어 OpenDoor/CloseDoor 는 no-op.
    //   극성 가정: 입력 true = 도어 닫힘(잠김). 실장에서 반대면 아래 한 줄만 반전.
    public partial class PulseMachine
    {
        private static partial class DI
        {
            public const string DOOR = "DI_DOOR_FRONT";   // 2호기 X007 정면 DOOR SW
        }

        // 도어 개폐 액추에이터 출력이 없어 실제 제어 대상 없음
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
