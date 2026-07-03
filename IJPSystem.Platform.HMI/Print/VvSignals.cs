using System.Collections.Generic;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 메니스커스 화면 VV(Valve/Vacuum) Control 의 제어 출력(VV_CON) → DOUT.
    /// 화면 버튼: Final VV / Switching Pressure / Pump Control.
    /// </summary>
    public enum VvControlOutput
    {
        FinalVv,            // Final VV (Toggle Head VV)
        SwitchingPressure,  // Switching Pressure
        PumpControl         // Pump Control
    }

    /// <summary>
    /// VV 상태 표시등(VV_IND) → DIN(피드백).
    /// 화면 LED: Meni / Purge / Final VV / Over Flow / Purge / Meniscus / Pump / MPC AL.
    /// (Purge 가 2개 = 서로 다른 밸브/센서. Purge1/Purge2 로 구분)
    /// </summary>
    public enum VvIndicator
    {
        Meni,
        Purge1,
        FinalVv,
        OverFlow,
        Purge2,
        Meniscus,
        Pump,
        MpcAlarm   // MPC AL
    }

    /// <summary>
    /// 신호 → IIODriver 인덱스 이름 매핑.
    /// ※ 아래 인덱스 이름은 placeholder 다. Config/IO.json 에 실제 VV 배선이 추가되면
    ///   그 Index 이름으로 교체할 것. (미매핑 이름은 IIODriver 에서 안전하게 무시됨)
    /// </summary>
    public static class VvSignalMap
    {
        /// <summary>제어 출력(DOUT) 인덱스 이름.</summary>
        public static readonly IReadOnlyDictionary<VvControlOutput, string> OutputIndex =
            new Dictionary<VvControlOutput, string>
            {
                { VvControlOutput.FinalVv,           "DO_VV_FINAL" },
                { VvControlOutput.SwitchingPressure, "DO_VV_SWITCHING_PRESSURE" },
                { VvControlOutput.PumpControl,       "DO_VV_PUMP_CONTROL" },
            };

        /// <summary>상태 표시등(DIN) 인덱스 이름.</summary>
        public static readonly IReadOnlyDictionary<VvIndicator, string> IndicatorIndex =
            new Dictionary<VvIndicator, string>
            {
                { VvIndicator.Meni,     "DI_VV_MENI" },
                { VvIndicator.Purge1,   "DI_VV_PURGE1" },
                { VvIndicator.FinalVv,  "DI_VV_FINAL" },
                { VvIndicator.OverFlow, "DI_VV_OVERFLOW" },
                { VvIndicator.Purge2,   "DI_VV_PURGE2" },
                { VvIndicator.Meniscus, "DI_VV_MENISCUS" },
                { VvIndicator.Pump,     "DI_VV_PUMP" },
                { VvIndicator.MpcAlarm, "DI_VV_MPC_ALARM" },
            };

        /// <summary>표시용 한글/영문 라벨.</summary>
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
