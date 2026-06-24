namespace IJPSystem.Machines.Inkjet5G
{
    // 도어 개폐 / 잠금
    public partial class InkjetMachine
    {
        private static partial class DI
        {
            public const string DOOR_LOCK_ALL         = "DI_DOOR_LOCK_ALL";
            public const string DOOR_LOCK_FRONT_LEFT  = "DI_DOOR_LOCK_FRONT_LEFT";
            public const string DOOR_LOCK_FRONT_RIGHT = "DI_DOOR_LOCK_FRONT_RIGHT";
        }
        private static partial class DO
        {
            public const string DOOR_OPEN      = "DO_DOOR_OPEN_SIGNAL";
            public const string DOOR_INTERLOCK = "DO_MANUAL_OPEN_INTERLOCK";
        }

        public void OpenDoor()
        {
            IO?.SetOutput(DO.DOOR_OPEN,      true);
            IO?.SetOutput(DO.DOOR_INTERLOCK, false);
        }

        public void CloseDoor()
        {
            IO?.SetOutput(DO.DOOR_OPEN,      false);
            IO?.SetOutput(DO.DOOR_INTERLOCK, true);
        }

        public bool IsDoorLocked()
            => IO?.GetInput(DI.DOOR_LOCK_ALL) ?? false;

        // ── 시뮬레이션 전용 ──
        public void SimulateDoorLockAfter(int delayMs)
            => IO?.ScheduleInput(DI.DOOR_LOCK_ALL, true, delayMs);
    }
}
