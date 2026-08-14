using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 트리거 체인 "계산" 검증 — DAQ 하드웨어 없이 확인 가능한 부분.
    /// 여기서 틀리면 실장에서 스트로브 위상이 통째로 어긋나므로, 장비 붙이기 전에 잡아야 한다.
    /// </summary>
    public class TriggerChainSettingsTests
    {
        // ※ 카운터 배정은 전장도면(2026-08-14)으로 확정됐다 — 분주기 ctr1 / LED ctr0(→PFI12) /
        //   카메라는 PFI12 공유(전용 카운터 없음). 코드 기본값과 Config 파일이 이제 같은 값이다.
        private static TriggerChainSettings Cfg() => new();

        /// <summary>카메라 전용 카운터가 있는 구성(배선을 분리한 장비).</summary>
        private static TriggerChainSettings CfgSeparateCam()
        {
            var c = Cfg();
            c.CamCounter = "Dev1/ctr3";
            return c;
        }

        // ── 틱 환산 ───────────────────────────────────────────────────────────
        [Theory]
        [InlineData(1.0, 100)]      // 100MHz → 1µs = 100틱
        [InlineData(0.01, 1)]       // 10ns = 1틱 (분해능 하한)
        [InlineData(10.0, 1000)]
        [InlineData(0.0, 0)]
        public void UsToTicks_100MHz(double us, int expectedTicks)
            => Assert.Equal(expectedTicks, Cfg().UsToTicks(us));

        [Fact]
        public void TicksToUs_IsInverseOfUsToTicks()
        {
            var c = Cfg();
            foreach (double us in new[] { 0.05, 1.0, 2.5, 37.5, 1000.0 })
                Assert.Equal(us, c.TicksToUs(c.UsToTicks(us)), precision: 6);
        }

        // ── 분주 ──────────────────────────────────────────────────────────────
        [Fact]
        public void EffectiveFrameRate_IsSpitFreqDividedByRatio()
        {
            var c = Cfg();
            c.DivideRatio = 100;
            Assert.Equal(10.0, c.EffectiveFrameRate(1000));   // 1kHz 토출 → 10fps
            Assert.Equal(50.0, c.EffectiveFrameRate(5000));
        }

        [Fact]
        public void EffectiveFrameRate_ZeroRatio_ReturnsZero()
        {
            var c = Cfg();
            c.DivideRatio = 0;
            Assert.Equal(0.0, c.EffectiveFrameRate(1000));
        }

        // ── 카운터 출력 터미널 파싱 ───────────────────────────────────────────
        // 이름이 틀리면 LED/Cam 이 트리거 소스를 못 찾아 조용히 안 돌아간다.
        [Theory]
        [InlineData("Dev1/ctr1", "/Dev1/Ctr1InternalOutput")]
        [InlineData("Dev1/ctr0", "/Dev1/Ctr0InternalOutput")]
        [InlineData("Dev2/ctr3", "/Dev2/Ctr3InternalOutput")]
        [InlineData("ctr2",      "/Dev1/Ctr2InternalOutput")]   // 디바이스 생략 → 기본 Dev1
        public void DividerOutputTerminal_Parses(string counter, string expected)
        {
            var c = Cfg();
            c.DividerCounter = counter;
            Assert.Equal(expected, c.DividerOutputTerminal());
        }

        // ── 설정 정합성 ───────────────────────────────────────────────────────
        [Fact]
        public void Validate_DefaultConfig_IsValid() => Assert.Null(Cfg().Validate());

        [Fact]
        public void Validate_RejectsDivideRatioBelowTwo()
        {
            var c = Cfg();
            c.DivideRatio = 1;
            Assert.NotNull(c.Validate());
        }

        [Fact]
        public void Validate_RejectsPulseWidthBelowTimebaseResolution()
        {
            // 1MHz 타임베이스(1틱=1µs)에서 0.1µs 폭은 0틱이 되어 펄스가 안 나간다.
            var c = Cfg();
            c.TimebaseRateHz = 1e6;
            c.LedWidthUs = 0.1;
            Assert.NotNull(c.Validate());
        }

        // ── 트리거 마진 ───────────────────────────────────────────────────────
        // DAQmx: 펄스 생성 중 도착한 트리거는 큐잉이 아니라 폐기된다.
        [Fact]
        public void TriggerMargin_ComfortableSpacing_NoWarning()
        {
            var c = Cfg();               // 분주 100, LED 1µs, Cam 10µs
            // 1kHz/100 = 10fps → 주기 100,000µs. 점유 10µs → 여유 충분.
            Assert.Null(c.ValidateTriggerMargin(1000));
        }

        [Fact]
        public void TriggerMargin_PulseLongerThanPeriod_Warns()
        {
            var c = CfgSeparateCam();
            c.DivideRatio = 2;
            c.CamDelayUs  = 0;
            c.CamWidthUs  = 600;         // 20kHz/2 = 10kHz → 주기 100µs < 점유 600µs
            Assert.NotNull(c.ValidateTriggerMargin(20000));
        }

        [Fact]
        public void TriggerMargin_DelayCountsTowardOccupancy()
        {
            // 폭만 보면 여유롭지만 지연까지 더하면 주기를 넘는 경우 —
            // 지연을 빼먹고 계산하면 놓치는 유형이다.
            var c = CfgSeparateCam();
            c.DivideRatio = 10;
            c.CamDelayUs  = 900;
            c.CamWidthUs  = 200;         // 10kHz/10 = 1kHz → 주기 1000µs < 900+200
            Assert.NotNull(c.ValidateTriggerMargin(10000));
        }

        [Fact]
        public void TriggerMargin_SharedWiring_IgnoresCamOccupancy()
        {
            // 공유 배선이면 Cam 카운터가 생성되지 않으므로 그 점유는 존재하지 않는다.
            // 여기서 빼지 않으면 쓰지도 않는 CamWidthUs 때문에 없는 경고가 뜨고,
            // 경고가 상시 뜨면 진짜 경고까지 같이 무시하게 된다.
            var c = Cfg();               // CamCounter 비어 있음 = 공유
            c.DivideRatio = 2;
            c.CamWidthUs  = 600;         // 분리 구성이었다면 경고가 났을 값
            Assert.True(c.CamSharesLed);
            Assert.Null(c.ValidateTriggerMargin(20000));
        }

        // ── 공유 배선(조명·카메라가 PFI12 한 가닥) ────────────────────────────
        // 전장도면(2026-08-14): PFI12/P2.4(2) → iCore DIGITAL INPUT+ 와 DWC 카메라 OPTO IN+(PIN2) 양쪽.

        [Fact]
        public void CamSharesLed_DefaultIsShared()
        {
            // 10호기 실배선이 공유다 — 기본값이 실배선을 나타내야 한다.
            Assert.True(Cfg().CamSharesLed);
            Assert.False(CfgSeparateCam().CamSharesLed);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Dev1/ctr0")]        // LED 와 같은 카운터를 명시해도 같은 뜻
        [InlineData("Dev1/CTR0")]        // 대소문자만 다른 경우
        public void CamSharesLed_EmptyOrSameAsLed(string cam)
        {
            var c = Cfg();               // LED = Dev1/ctr0
            c.CamCounter = cam;
            Assert.True(c.CamSharesLed);
        }

        [Fact]
        public void Validate_SharedWiring_IsValid()
        {
            // LED == Cam 은 "겹침"이 아니라 공유다 — 막으면 실배선을 설정으로 표현할 수 없다.
            var c = Cfg();
            c.CamCounter = "Dev1/ctr0";
            Assert.Null(c.Validate());
        }

        [Fact]
        public void Validate_SharedWiring_IgnoresCamWidth()
        {
            // 공유면 CamWidthUs 는 쓰이지 않으므로 0 이어도 기동을 막을 이유가 없다.
            var c = Cfg();
            c.CamWidthUs = 0;
            Assert.Null(c.Validate());

            // 분리 구성에서는 여전히 막아야 한다.
            var sep = CfgSeparateCam();
            sep.CamWidthUs = 0;
            Assert.NotNull(sep.Validate());
        }

        [Fact]
        public void SharedPulseWidth_TooShortForOptoInput_Warns()
        {
            // 조명은 짧을수록 좋고 카메라 광절연 입력은 짧으면 못 받는다 — 공유 배선의 정면 충돌.
            // 증상이 "LED 는 번쩍이는데 카메라가 안 찍힌다" 라 카메라 설정만 뒤지게 된다.
            var c = Cfg();
            c.LedWidthUs = 1.0;
            Assert.NotNull(c.ValidateSharedPulseWidth());

            c.LedWidthUs = 10.0;
            Assert.Null(c.ValidateSharedPulseWidth());
        }

        [Fact]
        public void SharedPulseWidth_SeparateCam_NoWarning()
        {
            // 배선을 분리했으면 조명 폭은 카메라와 무관하다 — 1µs 여도 경고할 일이 아니다.
            var c = CfgSeparateCam();
            c.LedWidthUs = 1.0;
            Assert.Null(c.ValidateSharedPulseWidth());
        }

        [Fact]
        public void StartupWarning_CombinesBothChecks()
        {
            // 경고가 둘 다 있으면 둘 다 보여야 한다 — 하나만 고치고 끝났다고 생각하면 안 된다.
            var c = Cfg();
            c.DivideRatio = 2;
            c.LedDelayUs  = 0;
            c.LedWidthUs  = 1.0;         // 공유 폭 경고
            string? both = c.StartupWarning(20000);   // 10kHz → 주기 100µs, 점유 1µs → 마진은 정상
            Assert.NotNull(both);
            Assert.Contains("광절연", both!);

            // 경고가 없으면 null 이어야 한다(빈 문자열이면 화면에 빈 줄이 남는다).
            c.LedWidthUs = 10.0;
            Assert.Null(c.StartupWarning(20000));
        }

        // ── 카운터 배정 ───────────────────────────────────────────────────────
        [Fact]
        public void Validate_DefaultCounters_Ok()
            => Assert.Null(Cfg().Validate());

        [Theory]
        [InlineData("Dev1/ctr1", "Dev1/ctr1", "Dev1/ctr3")]   // 분주기 == LED
        [InlineData("Dev1/ctr0", "Dev1/ctr1", "Dev1/ctr0")]   // 분주기 == Cam
        [InlineData("Dev1/ctr0", "Dev1/CTR0", "Dev1/ctr3")]   // 대소문자만 다른 같은 카운터
        [InlineData("Dev1/ctr0", " Dev1/ctr0 ", "Dev1/ctr3")] // 공백만 다른 같은 카운터
        public void Validate_DuplicateCounter_Rejected(string divider, string led, string cam)
        {
            // 겹치면 DAQmx 는 두 번째 태스크에서 "리소스 사용 중" 으로 실패하는데,
            // 그 메시지만으로는 설정이 겹쳤다는 걸 알기 어렵다.
            // ※ LED == Cam 은 여기 없다 — 그건 겹침이 아니라 공유 배선이다.
            var c = Cfg();
            c.DividerCounter = divider;
            c.LedCounter     = led;
            c.CamCounter     = cam;
            Assert.NotNull(c.Validate());
        }

        [Fact]
        public void Validate_EmptyCounter_Rejected()
        {
            // 분주기·LED 는 반드시 있어야 한다. (Cam 은 비어 있는 것이 정상 — 공유 배선)
            var c = Cfg();
            c.LedCounter = "";
            Assert.NotNull(c.Validate());

            var d = Cfg();
            d.DividerCounter = "";
            Assert.NotNull(d.Validate());
        }

        [Theory]
        [InlineData("Dev1/ctr0", "PFI12")]
        [InlineData("Dev1/ctr1", "PFI13")]
        [InlineData("Dev1/ctr2", "PFI14")]
        [InlineData("Dev1/ctr3", "PFI15")]
        [InlineData("ctr1",      "PFI13")]   // 디바이스 접두어가 없어도 같은 결과
        [InlineData("Dev1/ctr9", "PFI?")]    // X-Series 에 없는 번호는 단정하지 않는다
        public void DefaultOutputPfi_FollowsXSeriesRouting(string counter, string expected)
            => Assert.Equal(expected, TriggerChainSettings.DefaultOutputPfi(counter));

        [Fact]
        public void Describe_ShowsWhichPinEachCounterDrivesTo()
        {
            // 이 한 줄과 실제 케이블을 대조하는 것이 배선 확인의 전부다 —
            // 카운터·핀·분주비가 모두 들어 있어야 한다.
            var c = Cfg();
            c.DividerCounter = "Dev1/ctr1";
            c.LedCounter     = "Dev1/ctr0";
            c.CamCounter     = "Dev1/ctr3";

            string s = c.Describe();

            Assert.Contains("/Dev1/Ctr1InternalOutput", s);   // 분주 출력 = LED/Cam 의 트리거 소스
            Assert.Contains("LED ctr0→PFI12", s);
            Assert.Contains("Cam ctr3→PFI15", s);
            Assert.Contains($"1/{c.DivideRatio}", s);
        }

        [Fact]
        public void Describe_SharedWiring_SaysSoInsteadOfShowingEmptyCounter()
        {
            // 공유일 때 "Cam →PFI?" 같은 줄이 나오면 설정이 빠진 것처럼 읽힌다 —
            // 이 한 줄로 배선을 대조하는 것이 목적이므로 공유라는 사실을 명시해야 한다.
            var c = Cfg();               // 실배선 기본값
            string s = c.Describe();

            Assert.Contains("입력 /Dev1/PFI5", s);            // PCC2-E 에서 오는 토출 펄스
            Assert.Contains("LED ctr0→PFI12", s);
            Assert.Contains("LED 공유", s);
            Assert.DoesNotContain("PFI?", s);
        }
    }
}
