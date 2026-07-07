using System;
using System.Collections.Generic;
using System.IO;

namespace IJPSystem.Drivers.Motion.Comizoa
{
    /// <summary>
    /// Comizoa EtherCAT 라이브러리 설정. LabVIEW "BIN_x64/ComiEcatLibCfg.ini" 대응.
    /// 기본값은 코드에서 확인된 ini 값으로 채웠다.
    /// </summary>
    public sealed class ComiEcatConfig
    {
        // [LIB_TUNES]
        public int DcSetupDelay { get; set; } = 4000;
        /// <summary>0=PCI, 1=Embedded.</summary>
        public bool EmbeddedMode { get; set; } = true;
        /// <summary>시뮬레이션 모드.</summary>
        public bool SimulationMode { get; set; } = false;

        // [CONTROL_MSG_COMM]
        public string LocalIp { get; set; } = "192.168.1.253";
        public string DaemonIp { get; set; } = "192.168.1.253";
        public int DaemonPortL { get; set; } = 55000;
        public int DaemonPortM { get; set; } = 5000;
        public int RxTimeoutL { get; set; } = 3000;
        public int RxTimeoutM { get; set; } = 2000;
        public int CommRetryNum { get; set; } = 10;

        // [IO_MSG_COMM]
        /// <summary>EtherCAT 원격 디바이스(마스터) IP.</summary>
        public string MasterIp { get; set; } = "192.168.1.100";
        public int InPort { get; set; } = 35040;
        public int OutPort { get; set; } = 35041;

        /// <summary>로드에 사용한 ini 파일의 실제 경로. ecmGn_InitFromFile 에 그대로 전달한다.</summary>
        public string? SourcePath { get; set; }

        /// <summary>ini 파일에서 로드(간단 파싱). 파일이 없으면 기본값 반환.</summary>
        public static ComiEcatConfig Load(string iniPath)
        {
            var cfg = new ComiEcatConfig();
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) return cfg;
            cfg.SourcePath = Path.GetFullPath(iniPath);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(iniPath))
            {
                string line = raw;
                int semi = line.IndexOf(';');           // 주석 제거
                if (semi >= 0) line = line.Substring(0, semi);
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (k.Length > 0) map[k] = v;
            }

            int I(string k, int d) => map.TryGetValue(k, out var s) && int.TryParse(s, out var n) ? n : d;
            string S(string k, string d) => map.TryGetValue(k, out var s) ? s : d;

            cfg.DcSetupDelay = I("DC_Setup_Delay", cfg.DcSetupDelay);
            cfg.EmbeddedMode = I("EmbeddedMode", 1) == 1;
            cfg.SimulationMode = I("SimulationMode", 0) == 1;
            cfg.LocalIp = S("LOCAL_IP", cfg.LocalIp);
            cfg.DaemonIp = S("DAEMON_IP", cfg.DaemonIp);
            cfg.DaemonPortL = I("DAEMON_PORT_L", cfg.DaemonPortL);
            cfg.DaemonPortM = I("DAEMON_PORT_M", cfg.DaemonPortM);
            cfg.CommRetryNum = I("COMM_RETRY_NUM", cfg.CommRetryNum);
            cfg.MasterIp = S("MASTER_IP", cfg.MasterIp);
            cfg.InPort = I("IN_PORT", cfg.InPort);
            cfg.OutPort = I("OUT_PORT", cfg.OutPort);
            return cfg;
        }
    }
}
