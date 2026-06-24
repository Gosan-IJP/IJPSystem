namespace IJPSystem.Machines.Inkjet5G
{
    // Chuck 진공 (VC/NC) + Glass 감지
    public partial class InkjetMachine
    {
        private static partial class DI
        {
            public const string GLASS_STOP   = "DI_VC_SENSOR_GLASS_STOP";
            public const string GLASS_DETECT = "DI_NC_SENSOR_GLASS_DETECT";
        }
        private static partial class DO
        {
            // V/C Vacuum
            public const string VC_CHUCK_VAC_SUPPLY = "DO_VC_CHUCK_VAC_SUPPLY";
            public const string VC_CHUCK_VAC_BREAK  = "DO_VC_CHUCK_VAC_BREAK";
            // N/C Vacuum
            public const string NC_CHUCK_VACUUM     = "DO_NC_CHUCK_VACUUM_ON";
        }

        // ── Vacuum 제어 ──
        public void VacuumOn()
        {
            IO?.SetOutput(DO.VC_CHUCK_VAC_BREAK,  false); // BREAK 먼저 끄고
            IO?.SetOutput(DO.VC_CHUCK_VAC_SUPPLY, true);
            IO?.SetOutput(DO.NC_CHUCK_VACUUM,     true);
        }

        public void VacuumOff()
        {
            IO?.SetOutput(DO.VC_CHUCK_VAC_SUPPLY, false);
            IO?.SetOutput(DO.VC_CHUCK_VAC_BREAK,  true);  // 진공 파괴
            IO?.SetOutput(DO.NC_CHUCK_VACUUM,     false);
        }

        // ── Glass 감지 ──
        public bool IsGlassDetected()
            => (IO?.GetInput(DI.GLASS_STOP)   ?? false) ||
               (IO?.GetInput(DI.GLASS_DETECT) ?? false);
    }
}
