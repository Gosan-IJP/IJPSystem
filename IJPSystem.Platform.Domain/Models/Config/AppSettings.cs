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

        // ── 메니스커스 DMD(Modbus RTU / 시리얼) 압력 모듈 연결 설정 ──
        // Enabled=false 면 연결 시도하지 않고 UI 는 mock 으로 동작.
        public bool   MeniscusEnabled  { get; set; } = false;
        public string MeniscusComPort  { get; set; } = "COM3";
        public int    MeniscusBaudRate { get; set; } = 9600;
        public byte   MeniscusUnitId   { get; set; } = 1;
    }
}
