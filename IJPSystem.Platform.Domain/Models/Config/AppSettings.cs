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

        /// <summary>
        /// PCC 가 읽는 Meteor PrintEngine 설정(.cfg) 경로. <b>비우면</b> 예전대로
        /// <c>Config\PrintEngine.cfg</c> 를 찾는다.
        ///
        /// <para><b>왜 설정으로 뺐나</b>: 이 파일은 Meteor 설치가 관리하는 파일이고 이름도
        /// 헤드마다 다르다(<c>DefaultEpsonS3200_PccE.cfg</c>). 우리 Config 로 복사해 두면
        /// 원본과 두 벌이 되어 현장에서 어느 쪽이 쓰이는지 알 수 없게 된다.</para>
        ///
        /// <para>상대 경로면 <c>Config</c> 폴더 기준, 절대 경로면 그대로 쓴다 —
        /// 제어 PC 는 <c>C:\Users\Public\Documents\Meteor\Config\PccE\...</c> 를 그대로 가리키면 된다.</para>
        /// </summary>
        public string MeteorConfigPath { get; set; } = "";

        // ── 메니스커스 DMD — 옛 위치(하위호환 전용) ────────────────────────────
        // ★새 설정은 Config/MeniscusConfig.json + DriverMode.Meniscus 다. 여기 값은 쓰지 말 것.
        //
        // 남겨 둔 이유: 제어 PC 의 AppConfig.json 에 이미 이 키들이 들어 있다. MeniscusConfig.json
        //   이 없을 때만 읽어 그대로 동작시키고 경고를 남긴다 — 없으면 배포 직후 현장에서 맞춘
        //   COM 포트가 조용히 기본값으로 돌아간다.
        // 현장 파일이 모두 옮겨진 뒤 삭제할 것.
        public bool   MeniscusEnabled  { get; set; } = false;
        public string MeniscusComPort  { get; set; } = "COM3";
        public int    MeniscusBaudRate { get; set; } = 9600;
        public byte   MeniscusUnitId   { get; set; } = 1;

        /// <summary>옛 메니스커스 키가 AppConfig 에 남아 있는가 — 폴백 경고를 낼지 판단한다.</summary>
        public bool HasLegacyMeniscusKeys => MeniscusEnabled;

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
    ///   Meniscus : Virtual | Dmd
    /// </summary>
    public class DriverModeSettings
    {
        public string IO     { get; set; } = "Virtual";
        public string Motion { get; set; } = "Virtual";
        public string Vision { get; set; } = "Virtual";

        // 메니스커스 압력 컨트롤러. "Dmd" 면 MeniscusConfig.json 으로 Modbus RTU 연결,
        // 그 외("Virtual")면 연결하지 않고 화면만 mock 으로 동작한다.
        //   여기 둔 이유: "실물이 달렸나" 판정은 IO/Motion/Vision/Head 와 같은 성격이다.
        //   장치 설정 파일에 Enabled 를 또 두면 가상↔실장 전환 때 볼 곳이 둘로 갈라진다.
        //   ※ 값이 비어 있으면 옛 AppConfig 의 MeniscusEnabled 를 따른다(하위호환).
        public string Meniscus { get; set; } = "";

        // 프린트 헤드(Meteor PCC).
        //   "Meteor"  = 실물 폴링
        //   "Virtual" = 헤드 없이 PCC-E 화면을 확인하기 위한 가상 값(화면에 계속 '가상'으로 표시된다)
        //   그 외("None") = 폴링 자체를 하지 않음. 헤드가 없는 장비에서 불필요한
        //                   PiOpenPrinter 점유/지연을 막기 위함.
        // ※ 실물이 실패해도 Virtual 로 떨어지지 않는다 — 안 붙은 헤드가 초록불로 보이면 안 된다.
        public string Head   { get; set; } = "None";
    }
}
