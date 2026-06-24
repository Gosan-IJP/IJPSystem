using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Domain.Models.Config
{
    public class AppSettings
    {
        public string MachineType       { get; set; } = "Pulse";
        public string AdminPassword     { get; set; } = "admin";
        public string EngineerPassword  { get; set; } = "engineer";
        public string OperatorPassword  { get; set; } = "operator";
        public int    LogSaveDays       { get; set; } = 30;

        // true 면 가동 전 도어 잠금 체크 활성 / false 면 우회 (현장 안전키 미연결 환경)
        public bool   IsDoorCheckEnabled { get; set; } = true;
    }
}
