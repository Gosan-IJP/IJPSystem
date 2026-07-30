namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 메니스커스 화면 VV(Valve/Vacuum) Control 의 제어 출력(VV_CON) → EtherCAT DO.
    /// 화면 버튼: Final VV / Switching Pressure(=3-Way VV) / Pump Control.
    /// </summary>
    public enum VvControlOutput
    {
        FinalVv,            // Final VV (TOG_Final VV)
        SwitchingPressure,  // Switching Pressure = 3-Way VV (TOG_3WAY)
        PumpControl         // Pump Control (Pump Con)
    }

    /// <summary>
    /// VV 상태 표시등(VV_IND). 화면 LED: Meni / Purge / Final VV / Over Flow / Purge / Meniscus / Pump / MPC AL.
    /// (Purge 가 2개 = 서로 다른 밸브. Purge1=SV PURGE, Purge2=PV Purge)
    /// </summary>
    public enum VvIndicator
    {
        Meni,       // SV Meni  (DO 코일)
        Purge1,     // SV PURGE (DO 코일)
        FinalVv,    // Final VV (DO 코일)
        OverFlow,   // Over Flow 센서 (실제 DI, IO.json DI_OVER_FLOW_SND)
        Purge2,     // PV Purge (DO 코일)
        Meniscus,   // PV Meni  (DO 코일)
        Pump,       // Pump     (DO 코일)
        MpcAlarm    // MPC AL   (DMD Modbus 알람)
    }

    /// <summary>표시등 신호의 출처 계통.</summary>
    public enum VvSource
    {
        DoCoil,     // EtherCAT DO 코일 되읽기(ecdoGetOne) — LabVIEW "Get DOUT State"
        DiSensor,   // 실제 디지털 입력 센서(IO.json Index)
        Dmd         // DMD 메니스커스 컨트롤러(Modbus RTU) 상태/알람
    }

    /// <summary>표시등 1개의 신호 출처 서술자.</summary>
    public readonly struct VvIndicatorSource
    {
        public VvSource Source { get; }
        public int DoChannel { get; }       // Source=DoCoil 일 때 EtherCAT DO 채널
        public string DiIndex { get; }       // Source=DiSensor 일 때 IO.json Index 이름

        private VvIndicatorSource(VvSource src, int doCh, string diIndex)
        { Source = src; DoChannel = doCh; DiIndex = diIndex; }

        public static VvIndicatorSource Do(int ch) => new(VvSource.DoCoil, ch, "");
        public static VvIndicatorSource Di(string idx) => new(VvSource.DiSensor, -1, idx);
        public static VvIndicatorSource DmdAlarm() => new(VvSource.Dmd, -1, "");
    }

    /// <summary>
    /// EtherCAT 디지털 출력(DO) 채널 번호. LabVIEW Main.vi 각 밸브 배선의 DoChannel 상수와 동일해야 한다.
    ///
    /// ★★★ 아래 채널 번호는 전부 placeholder 다(InkSupply 이식본의 자리표시자와 동일 순번).
    ///   실제 값 = 장비의 EtherCAT DO 슬레이브 매핑 = Main.vi "Set DOUT.vi" 의 DoChannel.
    ///   확정 전에는 실장비에서 토글 금지(엉뚱한 밸브가 동작할 수 있음). 여기 숫자만 고치면 된다.
    ///   ※ ETS-D08MN(IO.json Y000~Y007)은 이미 척/DMPC/헤드/잉크·배기로 차 있으므로,
    ///     VV 밸브들은 별도 EtherCAT 슬레이브의 이어지는 DO 채널일 가능성이 크다.
    /// </summary>
    public static class VvDoChannel
    {
        public const int FinalVv  = 0;   // TOG_Final VV
        public const int ThreeWay = 1;   // TOG_3WAY (Switching Pressure)
        public const int Pump     = 2;   // Pump Con
        public const int SvPurge  = 3;   // SV PURGE
        public const int SvMeni   = 4;   // SV Meni
        public const int PvPurge  = 5;   // PV Purge
        public const int PvMeni   = 6;   // PV Meni
    }

    /// <summary>신호 → EtherCAT DO 채널 / DI Index / DMD 매핑.</summary>
    public static class VvSignalMap
    {
        /// <summary>제어 버튼 → EtherCAT DO 채널(ecdoPutOne).</summary>
        public static int OutputChannel(VvControlOutput o) => o switch
        {
            VvControlOutput.FinalVv           => VvDoChannel.FinalVv,
            VvControlOutput.SwitchingPressure => VvDoChannel.ThreeWay,
            VvControlOutput.PumpControl       => VvDoChannel.Pump,
            _ => -1
        };

        /// <summary>표시등 → 신호 출처(DO 코일 되읽기 / DI 센서 / DMD 알람).</summary>
        public static VvIndicatorSource IndicatorSource(VvIndicator ind) => ind switch
        {
            VvIndicator.Meni     => VvIndicatorSource.Do(VvDoChannel.SvMeni),
            VvIndicator.Purge1   => VvIndicatorSource.Do(VvDoChannel.SvPurge),
            VvIndicator.FinalVv  => VvIndicatorSource.Do(VvDoChannel.FinalVv),
            VvIndicator.OverFlow => VvIndicatorSource.Di("DI_OVER_FLOW_SND"),  // 실제 DI 센서(X006)
            VvIndicator.Purge2   => VvIndicatorSource.Do(VvDoChannel.PvPurge),
            VvIndicator.Meniscus => VvIndicatorSource.Do(VvDoChannel.PvMeni),
            VvIndicator.Pump     => VvIndicatorSource.Do(VvDoChannel.Pump),
            VvIndicator.MpcAlarm => VvIndicatorSource.DmdAlarm(),
            _ => VvIndicatorSource.Do(-1)
        };

        /// <summary>표시용 라벨.</summary>
        public static string Label(VvIndicator ind) => ind switch
        {
            VvIndicator.Meni => "Meni",
            VvIndicator.Purge1 => "Purge",
            VvIndicator.FinalVv => "Final VV",
            VvIndicator.OverFlow => "Over Flow",
            VvIndicator.Purge2 => "Purge",
            VvIndicator.Meniscus => "Meniscus",
            VvIndicator.Pump => "Pump",
            VvIndicator.MpcAlarm => "MPC AL",
            _ => ind.ToString()
        };
    }
}
