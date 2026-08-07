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

        /// <summary>
        /// 차트(LiveCharts/Skia) 축 글자에 쓸 글꼴 파일. <b>기본값 "none" = 지정하지 않음.</b>
        /// <para>
        /// <b>기본을 끈 이유</b>: 제어 PC 는 축 글자를 못 그린다(격자선은 나온다). 글꼴 <b>조회</b>가
        /// 깨진 것으로 보고 2026-08-07 에 <c>SKTypeface.FromFile</c> 로 파일을 직접 읽어봤지만,
        /// 드랍와처 화면(차트 첫 렌더)에서 앱이 그대로 죽었다 — 2026-07-23 의
        /// <c>FromFamilyName</c> 때와 같은 증상이다. 즉 깨진 것은 글꼴 조회가 아니라 <b>Skia 의
        /// 텍스트 렌더 경로 자체</b>이고, 글꼴을 어떻게 주든 지정하는 순간 죽는다.
        /// </para>
        /// <para>
        /// 값을 넣으면(파일명 또는 절대경로) 다시 시도한다. 다른 PC 나 OS 를 수리한 뒤 검증용.
        /// 파일명만 쓰면 C:\Windows\Fonts 기준. 빈 문자열도 자동 탐색으로 <b>켜진다</b>.
        /// </para>
        /// </summary>
        public string ChartFontFile     { get; set; } = "none";

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
