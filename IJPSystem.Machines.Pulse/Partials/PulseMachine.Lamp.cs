using IJPSystem.Platform.Domain.Enums;

namespace IJPSystem.Machines.Pulse
{
    // Tower Lamp(경광등 R/G/B) + Buzzer + Mist Collector
    //
    // 2호기 IO.json 출력:
    //   Y007 DO_BUZZER / Y008 DO_TOWER_LAMP_RED / Y009 DO_TOWER_LAMP_GREEN
    //   Y010 DO_TOWER_LAMP_BLUE / Y006 DO_MIST_COLLECTOR
    //
    // ※ 1호기 IO.json 에는 이 Index 들이 없다 → SetOutput(indexName) 이 no-op(무해).
    //   따라서 동일 코드/바이너리로 1·2호기 모두 성립한다(2호기만 실제 출력 동작).
    //
    // 상태→표시 매핑(경광등은 R/G/B 개별 램프, 황색 없음):
    //   Idle       → 소등
    //   Standby    → 청색(대기)
    //   Running    → 녹색 + 미스트 콜렉터 ON
    //   Alarm      → 적색 + 부저
    //   Emergency  → 적색 + 부저
    public partial class PulseMachine
    {
        private static partial class DO
        {
            public const string TOWER_LAMP_RED   = "DO_TOWER_LAMP_RED";
            public const string TOWER_LAMP_GREEN = "DO_TOWER_LAMP_GREEN";
            public const string TOWER_LAMP_BLUE  = "DO_TOWER_LAMP_BLUE";
            public const string BUZZER           = "DO_BUZZER";
            public const string MIST_COLLECTOR   = "DO_MIST_COLLECTOR";
        }

        public void SetSystemStatus(MachineState state)
        {
            bool red    = state == MachineState.Alarm || state == MachineState.Emergency;
            bool green   = state == MachineState.Running;
            bool blue    = state == MachineState.Standby;
            bool buzzer  = state == MachineState.Alarm || state == MachineState.Emergency;
            bool mist    = state == MachineState.Running;   // 인쇄/동작 중 미스트 집진

            IO?.SetOutput(DO.TOWER_LAMP_RED,   red);
            IO?.SetOutput(DO.TOWER_LAMP_GREEN, green);
            IO?.SetOutput(DO.TOWER_LAMP_BLUE,  blue);
            IO?.SetOutput(DO.BUZZER,           buzzer);
            IO?.SetOutput(DO.MIST_COLLECTOR,   mist);
        }
    }
}
