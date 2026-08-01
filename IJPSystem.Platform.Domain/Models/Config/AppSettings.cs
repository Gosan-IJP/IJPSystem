using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Domain.Models.Config
{
    public class AppSettings
    {
        public string MachineType       { get; set; } = "PULSE";
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

        // ── 드라이버 선택 (디바이스별) ──────────────────────────────
        // 시뮬레이션은 "Virtual", 실장비는 벤더명. 값이 인식되지 않으면 Virtual 로 동작.
        public DriverModeSettings DriverMode { get; set; } = new();
    }

    /// <summary>
    /// 디바이스별 드라이버 선택.
    ///   IO     : Virtual | Comizoa | EtherCat
    ///   Motion : Virtual | Comizoa | Acs
    ///   Vision : Virtual | Imaqdx | Ebus
    ///   Head   : None | Meteor
    /// </summary>
    public class DriverModeSettings
    {
        public string IO     { get; set; } = "Virtual";
        public string Motion { get; set; } = "Virtual";
        public string Vision { get; set; } = "Virtual";

        // 프린트 헤드(Meteor PCC). "None" 이면 스플래시 확인·상태바 폴링을 아예 하지 않는다
        // — 헤드가 없는 장비에서 불필요한 PiOpenPrinter 점유/지연을 막기 위함.
        public string Head   { get; set; } = "None";
    }
}
