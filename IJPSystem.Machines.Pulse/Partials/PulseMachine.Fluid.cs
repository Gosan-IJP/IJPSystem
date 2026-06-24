namespace IJPSystem.Machines.Inkjet5G
{
    // 약액/유체 계통 (Bottle / Reservoir / Drain / Overflow / Head pack valves)
    public partial class InkjetMachine
    {
        private static partial class DI
        {
            // ── Bottle 1 (Ink) ──
            public const string BOTTLE_1_LEVEL_HIGH = "DI_BOTTLE_1_LEVEL_SENSOR_HIGH";
            public const string BOTTLE_1_DETECT     = "DI_BOTTLE_1_DETECT_SENSOR";
            public const string BOTTLE_1_LEAK       = "DI_BOTTLE_1_LEAK_SENSOR";

            // ── Bottle 2 (M/T) ──
            public const string BOTTLE_2_LEVEL_HIGH = "DI_BOTTLE2_MT_LEVEL_SENSOR_HIGH";
            public const string BOTTLE_2_DETECT     = "DI_BOTTLE2_MT_DETECT_SENSOR";
            public const string BOTTLE_2_LEAK       = "DI_BOTTLE2_MT_LEAK_SENSOR";

            // ── Bottle 3 (M/T) ──
            public const string BOTTLE_3_LEVEL_HIGH = "DI_BOTTLE3_MT_LEVEL_SENSOR_HIGH";
            public const string BOTTLE_3_DETECT     = "DI_BOTTLE3_MT_DETECT_SENSOR";
            public const string BOTTLE_3_LEAK       = "DI_BOTTLE3_MT_LEAK_SENSOR";

            // ── Bottle 4 (H/M — 설비 신호명은 LEVEL=HM, DETECT/LEAK=MT 로 혼용되어 있음. IO.json 과 일치) ──
            public const string BOTTLE_4_LEVEL_LOW  = "DI_BOTTLE4_HM_LEVEL_SENSOR_LOW";
            public const string BOTTLE_4_DETECT     = "DI_BOTTLE4_MT_DETECT_SENSOR";
            public const string BOTTLE_4_LEAK       = "DI_BOTTLE4_MT_LEAK_SENSOR";

            // ── Reservoir ──
            public const string RESERVOIR_OVERFLOW_1 = "DI_RESERVOIR_OVERFLOW_1";
            public const string RESERVOIR_HIGH_1     = "DI_RESERVOIR_HIGH_1";
            public const string RESERVOIR_SET_1      = "DI_RESERVOIR_SET_1";
            public const string RESERVOIR_EMPTY_1    = "DI_RESERVOIR_EMPTY_1";

            // ── Overflow ──
            public const string OVERFLOW            = "DI_OVERFLOW_SENSOR";
            public const string MT_SPT_OVERFLOW     = "DI_MT_SPT_OVERFLOW_SENSOR";
        }

        private static partial class DO
        {
            // ── 약액 공급/회수 ──
            public const string BOTTLE_INK_SUPPLY        = "DO_BOTTLE_INK_SUPPLY";
            public const string SUCTION_TO_SPT           = "DO_SUCTION_TO_SPT";
            public const string PURGE_TO_SPT             = "DO_PURGE_TO_SPT";
            public const string BATH_TO_DRAIN_PUMP       = "DO_BATH_TO_DRAIN_PUMP";
            public const string DIE_TO_DRAIN_PUMP        = "DO_DIE_TO_DRAIN_PUMP";
            public const string VALVE_SEPARATOR_TO_DRAIN = "DO_VALVE_SEPARATOR_TO_DRAIN_BOTTLE";

            // ── Head pack 인렛/리턴 밸브 (Y700~Y715, 1-based head index) ──
            public static readonly string[] HEAD_INLET_IN =
            {
                "",                  // [0] 미사용
                "DO_PACK1_H1_IN",    // [1] Y700
                "DO_PACK1_H2_IN",    // [2] Y702
                "DO_PACK1_H3_IN",    // [3] Y704
                "DO_PACK1_H4_IN",    // [4] Y706
                "DO_PACK1_H5_IN",    // [5] Y708
                "DO_PACK1_H6_IN",    // [6] Y710
                "DO_PACK1_H7_IN",    // [7] Y712
                "DO_PACK1_H8_IN",    // [8] Y714
            };
            public static readonly string[] HEAD_INLET_OUT =
            {
                "",                  // [0] 미사용
                "DO_PACK1_H1_OUT",   // [1] Y701
                "DO_PACK1_H2_OUT",   // [2] Y703
                "DO_PACK1_H3_OUT",   // [3] Y705
                "DO_PACK1_H4_OUT",   // [4] Y707
                "DO_PACK1_H5_OUT",   // [5] Y709
                "DO_PACK1_H6_OUT",   // [6] Y711
                "DO_PACK1_H7_OUT",   // [7] Y713
                "DO_PACK1_H8_OUT",   // [8] Y715
            };
        }

        // ── Bottle 1 (Ink / IMS 잉크 병) ──
        public bool IsInkBottleLevelHigh() => IO?.GetInput(DI.BOTTLE_1_LEVEL_HIGH) ?? false;
        public bool IsInkBottleDetected()  => IO?.GetInput(DI.BOTTLE_1_DETECT)     ?? false;
        public bool IsInkBottleLeak()      => IO?.GetInput(DI.BOTTLE_1_LEAK)       ?? false;

        // ── Bottle 2~3 (Maintenance 병) ──
        public bool IsMtBottleLevelHigh(int no) => no switch
        {
            2 => IO?.GetInput(DI.BOTTLE_2_LEVEL_HIGH) ?? false,
            3 => IO?.GetInput(DI.BOTTLE_3_LEVEL_HIGH) ?? false,
            _ => false,
        };

        public bool IsMtBottleDetected(int no) => no switch
        {
            2 => IO?.GetInput(DI.BOTTLE_2_DETECT) ?? false,
            3 => IO?.GetInput(DI.BOTTLE_3_DETECT) ?? false,
            4 => IO?.GetInput(DI.BOTTLE_4_DETECT) ?? false,
            _ => false,
        };

        public bool IsMtBottleLeak(int no) => no switch
        {
            2 => IO?.GetInput(DI.BOTTLE_2_LEAK) ?? false,
            3 => IO?.GetInput(DI.BOTTLE_3_LEAK) ?? false,
            4 => IO?.GetInput(DI.BOTTLE_4_LEAK) ?? false,
            _ => false,
        };

        // ── Bottle 4 (Head Maintenance 병) ──
        public bool IsHmBottleLevelLow() => IO?.GetInput(DI.BOTTLE_4_LEVEL_LOW) ?? false;

        // ── Reservoir ──
        public bool IsReservoirOverflow() => IO?.GetInput(DI.RESERVOIR_OVERFLOW_1) ?? false;
        public bool IsReservoirHigh()     => IO?.GetInput(DI.RESERVOIR_HIGH_1)     ?? false;
        public bool IsReservoirSet()      => IO?.GetInput(DI.RESERVOIR_SET_1)      ?? false;
        public bool IsReservoirEmpty()    => IO?.GetInput(DI.RESERVOIR_EMPTY_1)    ?? false;

        // ── 오버플로우 감지 ──
        public bool IsOverflowDetected()  => IO?.GetInput(DI.OVERFLOW)        ?? false;
        public bool IsMtSptOverflow()     => IO?.GetInput(DI.MT_SPT_OVERFLOW) ?? false;

        // ── 약액 공급/회수 밸브/펌프 ──
        public void InkSupplyOn(bool on)      => IO?.SetOutput(DO.BOTTLE_INK_SUPPLY,        on);
        public void SuctionToSpt(bool on)     => IO?.SetOutput(DO.SUCTION_TO_SPT,           on);
        public void PurgeToSpt(bool on)       => IO?.SetOutput(DO.PURGE_TO_SPT,             on);
        public void BathToDrainPump(bool on)  => IO?.SetOutput(DO.BATH_TO_DRAIN_PUMP,       on);
        public void DieToDrainPump(bool on)   => IO?.SetOutput(DO.DIE_TO_DRAIN_PUMP,        on);
        public void SeparatorToDrain(bool on) => IO?.SetOutput(DO.VALVE_SEPARATOR_TO_DRAIN, on);

        // ── Head pack 인렛/리턴 밸브 (1..8) ──
        // headNo: 1-based head index. Head 1 = Y700/Y701, Head 8 = Y714/Y715
        public void HeadInletIn(int headNo, bool on)
        {
            if (headNo < 1 || headNo >= DO.HEAD_INLET_IN.Length) return;
            IO?.SetOutput(DO.HEAD_INLET_IN[headNo], on);
        }

        public void HeadInletOut(int headNo, bool on)
        {
            if (headNo < 1 || headNo >= DO.HEAD_INLET_OUT.Length) return;
            IO?.SetOutput(DO.HEAD_INLET_OUT[headNo], on);
        }

        public bool IsHeadInletInOn(int headNo)
            => headNo >= 1
               && headNo < DO.HEAD_INLET_IN.Length
               && (IO?.GetOutput(DO.HEAD_INLET_IN[headNo]) ?? false);

        public bool IsHeadInletOutOn(int headNo)
            => headNo >= 1
               && headNo < DO.HEAD_INLET_OUT.Length
               && (IO?.GetOutput(DO.HEAD_INLET_OUT[headNo]) ?? false);

        // 모든 head 인렛/리턴 일괄 제어
        public void AllHeadInletsIn(bool on)
        {
            for (int i = 1; i < DO.HEAD_INLET_IN.Length; i++)
                IO?.SetOutput(DO.HEAD_INLET_IN[i], on);
        }
        public void AllHeadInletsOut(bool on)
        {
            for (int i = 1; i < DO.HEAD_INLET_OUT.Length; i++)
                IO?.SetOutput(DO.HEAD_INLET_OUT[i], on);
        }
    }
}
