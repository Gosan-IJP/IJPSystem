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
        // ※ 코드 기본값과 Config/TriggerChainConfig.json 은 카운터 배정이 서로 다르다
        //   (코드 분주기=ctr0/LED=ctr1, 파일 분주기=ctr1/LED=ctr0). 실배선 확인 전까지 확정 못 하며,
        //   실행 시 이기는 쪽은 파일이다 — 그래서 Describe() 로 해석 결과를 로그에 남긴다.
        private static TriggerChainSettings Cfg() => new();

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
            var c = Cfg();
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
            var c = Cfg();
            c.DivideRatio = 10;
            c.CamDelayUs  = 900;
            c.CamWidthUs  = 200;         // 10kHz/10 = 1kHz → 주기 1000µs < 900+200
            Assert.NotNull(c.ValidateTriggerMargin(10000));
        }

        // ── 카운터 배정 ───────────────────────────────────────────────────────
        [Fact]
        public void Validate_DefaultCounters_Ok()
            => Assert.Null(Cfg().Validate());

        [Theory]
        [InlineData("Dev1/ctr1", "Dev1/ctr1", "Dev1/ctr3")]   // 분주기 == LED
        [InlineData("Dev1/ctr0", "Dev1/ctr1", "Dev1/ctr0")]   // 분주기 == Cam
        [InlineData("Dev1/ctr0", "Dev1/ctr3", "Dev1/ctr3")]   // LED == Cam
        [InlineData("Dev1/ctr0", "Dev1/CTR0", "Dev1/ctr3")]   // 대소문자만 다른 같은 카운터
        [InlineData("Dev1/ctr0", " Dev1/ctr0 ", "Dev1/ctr3")] // 공백만 다른 같은 카운터
        public void Validate_DuplicateCounter_Rejected(string divider, string led, string cam)
        {
            // 겹치면 DAQmx 는 두 번째 태스크에서 "리소스 사용 중" 으로 실패하는데,
            // 그 메시지만으로는 설정이 겹쳤다는 걸 알기 어렵다.
            var c = Cfg();
            c.DividerCounter = divider;
            c.LedCounter     = led;
            c.CamCounter     = cam;
            Assert.NotNull(c.Validate());
        }

        [Fact]
        public void Validate_EmptyCounter_Rejected()
        {
            var c = Cfg();
            c.LedCounter = "";
            Assert.NotNull(c.Validate());
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
    }
}
