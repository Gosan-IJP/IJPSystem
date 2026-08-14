using System;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 드랍와쳐 하드웨어 트리거 체인 설정. (LabVIEW "Pules Reducer.vi" 프론트패널 대응)
    ///
    /// 신호 흐름:
    ///   헤드 토출 펄스(PFI8) → [분주기] N분주 → ┬→ [LED]  지연/폭 → 스트로브 발광
    ///                                          └→ [Cam]  지연/폭 → 카메라 노출
    /// </summary>
    public sealed class TriggerChainSettings
    {
        // ── 소스 ──────────────────────────────────────────────────────────────
        /// <summary>
        /// 분주기 입력 터미널 — <b>토출 펄스가 실제로 들어오는 핀</b>.
        /// <para>
        /// ★전장도면(2026-08-14) 확정: <c>PFI5/P1.5(6)</c> 가 <b>PCC2-E 의 PL3 2번 핀</b>에,
        /// <c>DGND(7)</c> 가 PL3 5번 핀에 물려 있다. 토출 펄스를 만드는 것은 헤드 컨트롤러(PCC)이므로
        /// 이 핀이 분주기 입력이다.
        /// </para>
        /// <para>
        /// <b>PFI8 이 아니다</b> — 도면상 <c>AI1(33) · PFI8/P2.0(37) · PFI15/P2.7(39)</c> 는 자기들끼리
        /// 점퍼(JUMP)만 되어 있고 외부 배선이 없다. PFI15 는 ctr3 출력이니 "ctr3 로 펄스를 만들어
        /// PFI8 로 되먹인다"는 <b>장비 없이 돌리는 자체 시험용 루프백</b>으로 보인다. 여기를 PFI8 로
        /// 두면 실제 토출과 무관한(또는 아예 오지 않는) 펄스를 분주하게 된다.
        /// </para>
        /// <para>
        /// 드랍와처는 정지 상태 토출이라 이 펄스가 토출 펄스지만, 인쇄 중에는 스테이지 엔코더에
        /// 동기된 펄스가 같은 경로로 들어온다 — 즉 이 체인은 <b>위치 동기 스트로브</b>다. 이동 거리에
        /// 동기해 일정 간격마다 LED 를 발광시키므로, 스테이지 속도가 변해도 촬영 간격(거리)이
        /// 일정하다. 시간 기반으로 바꾸면 인쇄 중 촬영 위치가 속도에 따라 흔들린다.
        /// </para>
        /// </summary>
        public string SpitPulseTerminal { get; set; } = "/Dev1/PFI5";

        /// <summary>
        /// 자체 시험용 루프백 입력(도면의 PFI8-PFI15 점퍼). 장비 없이 체인을 돌려볼 때만 쓴다 —
        /// 평시 운전에서는 <see cref="SpitPulseTerminal"/> 이 실제 경로다.
        /// </summary>
        public string LoopbackTerminal { get; set; } = "/Dev1/PFI8";

        /// <summary>LED/Cam 펄스 폭 산출용 고속 타임베이스.</summary>
        public string TimebaseTerminal { get; set; } = "/Dev1/100MHzTimebase";

        /// <summary>타임베이스 주파수[Hz]. 틱↔시간 환산에 쓴다.</summary>
        public double TimebaseRateHz { get; set; } = 100e6;

        // ── 카운터 배정 ───────────────────────────────────────────────────────
        // ★전장도면 「(9~12호기)PULSE 200 전장도면_0720-1.dwg」 로 확정(2026-08-14).
        //
        //   PFI12/P2.4(2)  ┬→ DWC 조명 iCore DRIVER  DIGITAL INPUT+
        //                  └→ DWC 카메라            OPTO IN+(PIN2)
        //   DGND(36)/AIGND(67) → 각 상대의 −측
        //
        // X-Series 기본 라우팅에서 PFI12 = Ctr0 출력이므로 <b>LED 카운터는 ctr0</b> 이고,
        // 남는 ctr1 이 분주기다. (이전 기본값은 랩뷰 주석의 "PFI13(ctr1 out) → DWC LED" 를 근거로
        // LED=ctr1 이었는데, 도면상 PFI13 에는 아무것도 꽂혀 있지 않다. 도면이 이긴다)
        //
        // ★조명과 카메라가 <b>같은 PFI12 한 가닥을 병렬로</b> 물고 있다 — 즉 별도의 카메라 트리거
        //   카운터가 없다. 랩뷰 CO 채널 목록에 카메라 출력이 없던 이유가 이것이다.
        //   그래서 <see cref="CamCounter"/> 는 비워 두는 것이 정상이며(= LED 공유), 이때 카메라
        //   지연/폭은 LED 값을 따라간다. CamDelayUs/CamWidthUs 를 따로 만져도 아무 효과가 없다.
        public string DividerCounter { get; set; } = "Dev1/ctr1";
        public string LedCounter     { get; set; } = "Dev1/ctr0";

        /// <summary>
        /// 카메라 전용 트리거 카운터. <b>비어 있거나 <see cref="LedCounter"/> 와 같으면
        /// "카메라가 LED 와 같은 펄스를 공유한다"</b> 는 뜻이고, 이것이 10호기 실배선이다.
        /// 별도 배선으로 바꾼 장비에서만 카운터를 지정한다.
        /// </summary>
        public string CamCounter { get; set; } = "";

        /// <summary>카메라가 LED 와 같은 펄스를 쓰는가(= 전용 카운터 없음).</summary>
        public bool CamSharesLed =>
            string.IsNullOrWhiteSpace(CamCounter) || CounterName(CamCounter) == CounterName(LedCounter);

        // ── 분주 ──────────────────────────────────────────────────────────────
        /// <summary>
        /// 분주비 N — 토출 N발당 1회 촬영.
        /// 노즐은 수 kHz 로 토출하지만 카메라/LED 가 못 따라가므로 반드시 필요하다.
        /// </summary>
        public int DivideRatio { get; set; } = 100;

        /// <summary>
        /// 초기 무시 펄스 수. 토출 초기의 불안정한 방울을 건너뛰고 안정된 뒤부터 촬영한다.
        /// (분주기 initial delay — 단위는 <b>입력 토출 펄스</b>, 타임베이스 틱이 아니다)
        /// </summary>
        public int InitialSkipPulses { get; set; } = 0;

        // ── LED 스트로브 ──────────────────────────────────────────────────────
        /// <summary>트리거 후 LED 발광까지 지연[µs]. <b>조(coarse)</b> 단계.</summary>
        public double LedDelayUs { get; set; } = 0;

        /// <summary>LED 발광 폭[µs]. 짧을수록 방울이 선명하게 얼어붙는다.</summary>
        public double LedWidthUs { get; set; } = 1.0;

        // ── 카메라 트리거 ─────────────────────────────────────────────────────
        // ※ 10호기 실배선은 카메라가 LED 와 PFI12 를 공유한다(CamSharesLed=true) — 이때 아래 두 값은
        //   <b>쓰이지 않는다</b>. 카메라 노출 위상은 LedDelayUs/LedWidthUs 가 정한다.
        //   값을 만졌는데 아무 변화가 없다면 공유 배선이라 그런 것이다(Describe() 로그로 확인).

        /// <summary>트리거 후 카메라 노출 시작까지 지연[µs]. 카메라 전용 카운터가 있을 때만 유효.</summary>
        public double CamDelayUs { get; set; } = 0;

        /// <summary>카메라 트리거 펄스 폭[µs]. 카메라 전용 카운터가 있을 때만 유효.</summary>
        public double CamWidthUs { get; set; } = 10.0;

        /// <summary>토출 펄스의 상승/하강 엣지 선택. true=상승.</summary>
        public bool TriggerOnRisingEdge { get; set; } = true;

        /// <summary>
        /// JSON 의 미지의 키(_comment 메모 등)를 보존한다.
        /// 없으면 저장 한 번에 배선 근거·미확인 표시가 전부 사라진다.
        /// </summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtraKeys { get; set; }

        // ── 환산 헬퍼 (NI 구현과 무관한 순수 계산 — 여기서 검증 가능) ─────────

        /// <summary>µs → 타임베이스 틱.</summary>
        public int UsToTicks(double us) => (int)Math.Round(us * 1e-6 * TimebaseRateHz);

        /// <summary>타임베이스 틱 → µs.</summary>
        public double TicksToUs(int ticks) => ticks / TimebaseRateHz * 1e6;

        /// <summary>분주 후 실제 촬영 주파수[Hz] = 토출주파수 / 분주비.</summary>
        public double EffectiveFrameRate(double spitFreqHz)
            => DivideRatio > 0 ? spitFreqHz / DivideRatio : 0;

        /// <summary>
        /// 분주 카운터의 내부 출력 터미널명. 예: "Dev1/ctr1" → "/Dev1/Ctr1InternalOutput".
        /// LED/Cam 이 이 터미널을 Start Trigger 소스로 받는다.
        /// </summary>
        public string DividerOutputTerminal()
        {
            string c = DividerCounter ?? "";
            int slash = c.LastIndexOf('/');
            string dev = slash > 0 ? c.Substring(0, slash) : "Dev1";
            string ctr = slash > 0 ? c.Substring(slash + 1) : c;          // "ctr1"
            string idx = new string(Array.FindAll(ctr.ToCharArray(), char.IsDigit));
            if (idx.Length == 0) idx = "0";
            return $"/{dev}/Ctr{idx}InternalOutput";
        }

        /// <summary>
        /// 트리거 주기 대비 펄스 폭이 안전한지 검사한다.
        /// DAQmx 문서: "The device ignores a trigger if it is in the process of generating pulses."
        /// → 펄스 생성 중 도착한 트리거는 <b>큐잉이 아니라 폐기</b>된다. 지연+폭이 트리거 주기보다
        ///   길면 프레임이 조용히 누락되는데, 화면상으로는 그냥 "느린 촬영"으로 보여 원인 추적이 어렵다.
        /// </summary>
        /// <param name="spitFreqHz">토출 주파수[Hz].</param>
        /// <returns>누락 위험이 있으면 사유, 없으면 null.</returns>
        public string? ValidateTriggerMargin(double spitFreqHz)
        {
            double fps = EffectiveFrameRate(spitFreqHz);
            if (fps <= 0) return null;

            double periodUs = 1e6 / fps;
            double ledBusyUs = LedDelayUs + LedWidthUs;
            // 공유 배선이면 Cam 카운터가 아예 생성되지 않으므로 그 점유는 존재하지 않는다.
            // 여기서 빼지 않으면 쓰지도 않는 CamWidthUs(기본 10µs) 때문에 없는 경고가 뜬다.
            double camBusyUs = CamSharesLed ? 0 : CamDelayUs + CamWidthUs;
            double busyUs = Math.Max(ledBusyUs, camBusyUs);

            if (busyUs >= periodUs)
                return $"트리거 주기 {periodUs:F1}µs 보다 펄스 점유 {busyUs:F1}µs 가 길어 트리거가 폐기됩니다. " +
                       $"분주비를 키우거나 지연/폭을 줄이세요.";

            // 여유가 10% 미만이면 지터에 따라 간헐 누락이 난다 — 재현이 어려운 유형이라 미리 경고한다.
            if (busyUs > periodUs * 0.9)
                return $"트리거 주기 {periodUs:F1}µs 대비 펄스 점유 {busyUs:F1}µs — 여유 10% 미만이라 " +
                       $"간헐적 프레임 누락이 발생할 수 있습니다.";
            return null;
        }

        /// <summary>
        /// 조명·카메라가 한 가닥을 공유할 때만 생기는 위험 — <b>펄스 폭 하나로 둘을 동시에 만족시켜야 한다</b>.
        ///
        /// <para>스트로브는 짧을수록 좋다(방울이 선명하게 얼어붙는다). 반대로 카메라의 <b>광절연(opto)
        /// 입력은 느려서</b> 너무 짧은 펄스는 통과하지 못한다 — 포토커플러 상승/하강이 수 µs 단위다.
        /// 공유 배선에서는 이 둘이 정면으로 충돌한다.</para>
        ///
        /// <para>이게 걸리면 증상이 <b>"LED 는 번쩍이는데 카메라가 안 찍힌다"</b> 인데, 조명이 멀쩡히
        /// 보이니 트리거가 나가고 있다고 착각해 카메라 설정만 뒤지게 된다.</para>
        ///
        /// <para>기준 5µs 는 산업용 카메라 광절연 입력의 통상값일 뿐 <b>데이터시트 값이 아니다</b> —
        /// 실물 매뉴얼의 최소 트리거 폭을 확인해 판단할 것. 카메라가 정말 짧은 펄스를 못 받으면
        /// 폭을 늘리는 대신 <see cref="CamCounter"/> 에 전용 카운터를 배정하고 배선을 분리해야 한다
        /// (폭을 늘리면 그만큼 방울이 흐려진다).</para>
        /// </summary>
        public string? ValidateSharedPulseWidth()
        {
            const double OptoMinUs = 5.0;
            if (!CamSharesLed || LedWidthUs >= OptoMinUs) return null;

            return $"조명·카메라가 {DefaultOutputPfi(LedCounter)} 한 가닥을 공유하는데 펄스 폭이 " +
                   $"{LedWidthUs:F1}µs 입니다. 카메라 광절연 입력이 {OptoMinUs:F0}µs 미만을 놓칠 수 있습니다 " +
                   $"— 조명만 켜지고 촬영이 안 되면 이것부터 의심하세요.";
        }

        /// <summary>
        /// 기동 시 화면·로그에 남길 경고 전부(프레임 누락 위험 + 공유 펄스 폭). 없으면 null.
        /// 경고는 기동을 막지 않는다 — 다만 이걸 모르고 측정값을 믿으면 안 된다.
        /// </summary>
        public string? StartupWarning(double spitFreqHz)
        {
            string?[] all = { ValidateTriggerMargin(spitFreqHz), ValidateSharedPulseWidth() };
            string joined = string.Join(" / ", Array.FindAll(all, w => !string.IsNullOrEmpty(w)));
            return joined.Length == 0 ? null : joined;
        }

        /// <summary>
        /// 카운터 번호만 뽑는다("Dev1/ctr1" → "ctr1"). 표기 흔들림(대소문자·공백)을 흡수해
        /// 중복 검사와 PFI 환산이 같은 기준으로 돌게 한다.
        /// </summary>
        private static string CounterName(string counter)
        {
            string c = (counter ?? "").Trim();
            int slash = c.LastIndexOf('/');
            return (slash >= 0 ? c.Substring(slash + 1) : c).ToLowerInvariant();
        }

        /// <summary>
        /// 카운터의 기본 출력 PFI 번호. X-Series 기본 라우팅은 ctr0→PFI12, ctr1→PFI13,
        /// ctr2→PFI14, ctr3→PFI15 다(즉 12 + 카운터번호).
        /// <para>※ 라우팅을 바꾸지 않았을 때의 값이다 — <b>배선 대조용 참고</b>지 보증이 아니다.</para>
        /// </summary>
        public static string DefaultOutputPfi(string counter)
        {
            string idx = new string(Array.FindAll(CounterName(counter).ToCharArray(), char.IsDigit));
            return int.TryParse(idx, out int n) && n is >= 0 and <= 3 ? $"PFI{12 + n}" : "PFI?";
        }

        /// <summary>
        /// 지금 설정이 실제로 어느 핀으로 나가는지 한 줄로. <b>기동 로그에 남기기 위한 것</b>이다.
        ///
        /// <para>코드 기본값과 <c>TriggerChainConfig.json</c> 이 서로 다른 카운터를 가리키고 있어도
        /// 이기는 쪽은 config 인데, 그 사실이 어디에도 안 남으면 "LED 가 안 켜진다" 는 증상만 보고
        /// 배선부터 뜯게 된다. 이 한 줄과 실제 케이블만 대조하면 끝난다.</para>
        /// </summary>
        public string Describe()
            => $"분주기 {CounterName(DividerCounter)}(입력 {SpitPulseTerminal}, 1/{DivideRatio}) " +
               $"→ {DividerOutputTerminal()} / " +
               $"LED {CounterName(LedCounter)}→{DefaultOutputPfi(LedCounter)} " +
               $"(지연 {LedDelayUs:F1}µs, 폭 {LedWidthUs:F1}µs) / " +
               (CamSharesLed
                   ? $"Cam = LED 공유({DefaultOutputPfi(LedCounter)} 한 가닥에 조명·카메라 병렬)"
                   : $"Cam {CounterName(CamCounter)}→{DefaultOutputPfi(CamCounter)} " +
                     $"(지연 {CamDelayUs:F1}µs, 폭 {CamWidthUs:F1}µs)");

        /// <summary>설정 정합성 검사. 문제가 있으면 사유를, 없으면 null 을 반환한다.</summary>
        public string? Validate()
        {
            // 카운터가 겹치면 DAQmx 는 두 번째 태스크에서 "리소스 사용 중" 으로 실패하는데,
            // 그 메시지만 보고는 설정이 겹쳤다는 걸 알기 어렵다 — 여기서 먼저 잡는다.
            // ※ LED == Cam 은 겹침이 아니라 <b>공유 배선</b>이다(10호기 실배선). 이때 Cam 태스크를
            //   아예 만들지 않으므로 리소스 충돌이 없다.
            string dv = CounterName(DividerCounter), led = CounterName(LedCounter);
            if (dv.Length == 0 || led.Length == 0)
                return "카운터 지정이 비어 있습니다(DividerCounter / LedCounter).";
            if (dv == led) return $"분주기와 LED 가 같은 카운터({dv})입니다. 서로 다른 카운터를 지정하세요.";

            if (!CamSharesLed)
            {
                string cam = CounterName(CamCounter);
                if (dv == cam) return $"분주기와 Cam 이 같은 카운터({dv})입니다. 서로 다른 카운터를 지정하세요.";

                if (CamWidthUs <= 0) return "카메라 트리거 폭은 0보다 커야 합니다.";
                if (UsToTicks(CamWidthUs) < 1)
                    return $"카메라 폭 {CamWidthUs}µs 가 타임베이스 분해능({TicksToUs(1):F3}µs)보다 작습니다.";
            }

            if (DivideRatio < 2)   return "분주비는 2 이상이어야 합니다(1이면 분주 의미 없음).";
            if (TimebaseRateHz <= 0) return "타임베이스 주파수가 0 이하입니다.";
            if (LedWidthUs <= 0)   return "LED 발광 폭은 0보다 커야 합니다.";
            if (InitialSkipPulses < 0) return "초기 무시 펄스 수는 음수일 수 없습니다.";

            // 폭이 타임베이스 분해능보다 작으면 0틱이 되어 펄스가 나가지 않는다.
            if (UsToTicks(LedWidthUs) < 1)
                return $"LED 폭 {LedWidthUs}µs 가 타임베이스 분해능({TicksToUs(1):F3}µs)보다 작습니다.";
            return null;
        }
    }

    /// <summary>
    /// 드랍와쳐 하드웨어 트리거 체인 — 토출 펄스를 분주해 LED 스트로브와 카메라를 동기시킨다.
    ///
    /// <b>이것이 있어야 "매 토출마다 정확히 1회 촬영"이 하드웨어로 보장된다.</b>
    /// 소프트 트리거(자유 촬영)로는 스트로브가 얼린 방울이 아니라 임의 위상의 화면을 찍게 된다.
    ///
    /// <b>지연 2단계를 혼동하지 말 것</b> — 서로 다른 층이다:
    ///   조(coarse) : <see cref="TriggerChainSettings.LedDelayUs"/>  — 트리거 체인, 하드웨어 확정
    ///   미세(fine) : <see cref="IStrobeController.SetDelayMicroseconds"/> — Modbus, 궤적 스윕용
    /// 2점 측정(<see cref="DropVelocitySequence"/>)이 훑는 것은 <b>미세</b> 쪽이다.
    ///
    /// <b>NI-DAQmx 구현 시 반드시 지킬 것</b> (원본 VI 에서 확인된 사항):
    ///   ① Start 순서 — LED/Cam 을 먼저 arm 한 뒤 분주기를 시작해야 첫 트리거를 놓치지 않는다.
    ///   ② LED/Cam 은 <c>StartTrigger.Retriggerable = true</c>. 이걸 빠뜨리면 첫 펄스만 나가고
    ///      이후 트리거가 전부 무시된다 — "첫 프레임만 찍히고 멈춤" 증상의 원인이라 추적이 까다롭다.
    ///   ③ Stop 은 역순 — 분주기(트리거 공급)부터 차단한다.
    /// </summary>
    public interface ITriggerChain : IDisposable
    {
        bool IsRunning { get; }

        /// <summary>분주 후 실제 촬영 주파수[Hz]. 미기동이면 0.</summary>
        double EffectiveFrameRateHz { get; }

        /// <summary>
        /// 기동 시 감지된 프레임 누락 위험 경고. 없으면 null.
        /// 기동을 막을 정도는 아니지만 측정값 해석에 영향이 있어 화면/로그에 남겨야 한다.
        /// </summary>
        string? MarginWarning { get; }

        /// <summary>체인 기동. <paramref name="spitFrequencyHz"/> 는 실촬영 주파수 산출용.</summary>
        void Start(double spitFrequencyHz);

        void Stop();
    }

    /// <summary>
    /// 실장 DAQ 없이 트리거 동기 획득 경로를 검증하기 위한 가상 트리거 체인.
    /// 분주 후 주파수에 맞춰 <paramref name="onTrigger"/> 를 주기적으로 호출한다
    /// — HMI 가 이걸 가상 카메라의 하드웨어 트리거 시뮬레이션에 연결한다.
    ///
    /// ※ 실장 연결 시 <see cref="ITriggerChain"/> 을 구현한 NI-DAQmx 어댑터로 교체할 것.
    ///   CO Pulse Ticks(분주) + Retriggerable 카운터(LED/Cam) 구성이며,
    ///   위 인터페이스 주석의 ①②③ 을 그대로 따르면 된다.
    ///   (NationalInstruments.DAQmx 어셈블리 — NI-DAQmx 드라이버 설치 시 동봉 — 가 있어야 작성 가능)
    /// </summary>
    public sealed class VirtualTriggerChain : ITriggerChain
    {
        private readonly TriggerChainSettings _cfg;
        private readonly Func<Task> _onTrigger;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public VirtualTriggerChain(TriggerChainSettings cfg, Func<Task> onTrigger)
        {
            _cfg       = cfg       ?? throw new ArgumentNullException(nameof(cfg));
            _onTrigger = onTrigger ?? throw new ArgumentNullException(nameof(onTrigger));
        }

        public bool    IsRunning            { get; private set; }
        public double  EffectiveFrameRateHz { get; private set; }
        public string? MarginWarning        { get; private set; }

        public void Start(double spitFrequencyHz)
        {
            if (IsRunning) return;

            string? bad = _cfg.Validate();
            if (bad != null) throw new InvalidOperationException($"트리거 체인 설정 오류: {bad}");

            EffectiveFrameRateHz = _cfg.EffectiveFrameRate(spitFrequencyHz);
            if (EffectiveFrameRateHz <= 0)
                throw new InvalidOperationException("실촬영 주파수가 0 입니다. 토출 주파수/분주비를 확인하세요.");

            // 가상은 마진 경고만 본다 — 공유 펄스 폭 경고(광절연 입력)는 실물 카메라에만 해당하고,
            // 여기서 띄우면 가상 모드에서 상시로 뜨는 가짜 경고가 된다.
            MarginWarning = _cfg.ValidateTriggerMargin(spitFrequencyHz);

            _cts = new CancellationTokenSource();
            IsRunning = true;
            _loop = RunAsync(_cts.Token);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            // 초기 무시 펄스 — 토출이 안정될 때까지 트리거를 내보내지 않는다.
            if (_cfg.InitialSkipPulses > 0 && EffectiveFrameRateHz > 0)
            {
                double skipMs = _cfg.InitialSkipPulses / Math.Max(1e-9, EffectiveFrameRateHz * _cfg.DivideRatio) * 1000.0;
                try { await Task.Delay(TimeSpan.FromMilliseconds(skipMs), ct); } catch (OperationCanceledException) { return; }
            }

            int periodMs = Math.Max(1, (int)Math.Round(1000.0 / EffectiveFrameRateHz));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _onTrigger().ConfigureAwait(false);
                    await Task.Delay(periodMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch { /* 트리거 소비자 예외가 체인을 죽이지 않게 한다 */ }
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts?.Cancel();
            try { _loop?.Wait(500); } catch { }
            _cts?.Dispose();
            _cts  = null;
            _loop = null;
            EffectiveFrameRateHz = 0;
            MarginWarning = null;
        }

        public void Dispose() => Stop();
    }
}
