namespace IJPSystem.Machines.Inkjet5G
{
    // 안전 센서 계통 (EMO, Pressure Switch)
    public partial class InkjetMachine
    {
        private static partial class DI
        {
            // ── EMO ──
            public const string EMO_FRONT = "DI_EMO_FRONT";
            public const string EMO_LEFT  = "DI_EMO_LEFT";
            public const string EMO_RIGHT = "DI_EMO_RIGHT";
            public const string EMO_BACK  = "DI_EMO_BACK";

            // ── Pressure Switch (1-based index) ──
            public static readonly string[] PRESSURE_SW =
            {
                "",                                           // [0] 미사용
                "DI_PRESSURE_SW1_N2_IMS_AIR_KNIFE",           // [1]
                "DI_PRESSURE_SW2_CDA_IMS_N_EJEC",             // [2]
                "DI_PRESSURE_SW3_CDA_HM_SOL_P_MT_P",          // [3]
                "DI_PRESSURE_SW4_VACUUM_CV_P",                // [4]
                "DI_PRESSURE_SW5_NORMAL_CV_P_AIR_SPRING",     // [5]
                "DI_PRESSURE_SW6_VACUUM_CV_POROUS_EJEC",      // [6]
                "DI_PRESSURE_SW7_EJEC_ELEC_BOX",              // [7]
                "DI_PRESSURE_VC_POROUS_VACUUM_EJECT_8",       // [8]
                "DI_PRESSURE_SW9_IMS_P_N2",                   // [9]
                "DI_PRESSURE_SW9_IMS_N_VAC",                  // [10]
                "DI_PRESSURE_SW11_EJEC_VAC_ELEC_BOX",         // [11]
            };
        }

        // ── EMO (비상정지) ──
        public bool IsEmoActive()
            => (IO?.GetInput(DI.EMO_FRONT) ?? false) ||
               (IO?.GetInput(DI.EMO_LEFT)  ?? false) ||
               (IO?.GetInput(DI.EMO_RIGHT) ?? false) ||
               (IO?.GetInput(DI.EMO_BACK)  ?? false);

        // ── Pressure Switch ──
        public bool IsPressureOk(int swNo)
        {
            if (swNo < 1 || swNo >= DI.PRESSURE_SW.Length) return false;
            return IO?.GetInput(DI.PRESSURE_SW[swNo]) ?? false;
        }
    }
}
