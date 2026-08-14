using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 트리거 상태 표시 판정 — 장비 없이 확인 가능한 부분.
    ///
    /// <para>이 판정이 틀리면 화면이 <b>거짓말</b>을 한다. 특히 위험한 두 방향:
    /// ① 안 되는데 초록불 → 그 상태로 잰 속도값을 믿게 된다.
    /// ② 되는데 빨간불 → 멀쩡한 장비를 뜯게 되고, 몇 번 반복되면 표시를 아예 안 보게 된다.</para>
    /// </summary>
    public class TriggerHealthTests
    {
        // ── RateMeter ─────────────────────────────────────────────────────────
        // 시계를 주입해 실제 시간을 기다리지 않고 검사한다(틱 = ms).
        private const double Ms = 1000;

        [Fact]
        public void RateMeter_CountsWithinWindow()
        {
            var m = new RateMeter(Ms) { WindowSeconds = 2.0 };
            for (int i = 0; i < 20; i++) m.Mark(i * 100);   // 100ms 간격 = 10Hz

            Assert.Equal(10.0, m.RateHz(1900), precision: 6);
            Assert.Equal(20, m.Total);
        }

        [Fact]
        public void RateMeter_DropsMarksOlderThanWindow()
        {
            var m = new RateMeter(Ms) { WindowSeconds = 2.0 };
            for (int i = 0; i < 20; i++) m.Mark(i * 100);

            // 창이 지나면 옛 표본은 빠져야 한다 — 안 빠지면 이미 멈춘 트리거가 계속 정상으로 보인다.
            Assert.Equal(0.0, m.RateHz(10_000));
            Assert.Equal(20, m.Total);      // 누적은 남는다
        }

        [Fact]
        public void RateMeter_SingleMark_RateIsZeroButNotStale()
        {
            // 한 장만으로는 주파수를 낼 수 없다. 그렇다고 끊긴 것도 아니다 — 둘을 섞으면 안 된다.
            var m = new RateMeter(Ms);
            m.Mark(0);
            Assert.Equal(0.0, m.RateHz(100));
            Assert.Equal(0.1, m.SecondsSinceLast(100), precision: 6);
        }

        [Fact]
        public void RateMeter_NoMarks_IsInfinitelyStale()
        {
            var m = new RateMeter(Ms);
            Assert.Equal(double.PositiveInfinity, m.SecondsSinceLast(5000));
        }

        [Fact]
        public void RateMeter_Reset_ClearsWindowAndTotal()
        {
            // 기동할 때 비우지 않으면 지난 회차의 프레임이 남아 방금 켠 체인이 정상으로 보인다.
            var m = new RateMeter(Ms);
            for (int i = 0; i < 10; i++) m.Mark(i * 100);
            m.Reset();

            Assert.Equal(0, m.Total);
            Assert.Equal(0.0, m.RateHz(1000));
            Assert.Equal(double.PositiveInfinity, m.SecondsSinceLast(1000));
        }

        // ── 프레임 판정 ───────────────────────────────────────────────────────
        [Fact]
        public void Frame_ChainStopped_IsIdleNotFail()
        {
            // 안 돌리는 중인데 빨간불이면 표시등이 의미를 잃는다.
            Assert.Equal(TriggerLamp.Idle,
                TriggerHealth.Frame(chainRunning: false, receiving: true, 0, 10, double.PositiveInfinity));
        }

        [Fact]
        public void Frame_NotReceiving_IsIdle()
        {
            // 라이브도 측정도 꺼져 있으면 프레임이 안 오는 것이 정상이다.
            // 여기서 빨간불을 켜면 "라이브를 껐다"는 이유로 고장 신고가 올라온다.
            Assert.Equal(TriggerLamp.Idle,
                TriggerHealth.Frame(chainRunning: true, receiving: false, 0, 10, double.PositiveInfinity));
        }

        [Fact]
        public void Frame_Stale_IsFail()
        {
            Assert.Equal(TriggerLamp.Fail,
                TriggerHealth.Frame(true, true, 0, 10, TriggerHealth.StaleSeconds + 0.1));
        }

        [Theory]
        [InlineData(10.0, 10.0)]     // 정확
        [InlineData(9.0, 10.0)]      // -10%
        [InlineData(12.0, 10.0)]     // +20%
        public void Frame_WithinTolerance_IsOk(double measured, double expected)
            => Assert.Equal(TriggerLamp.Ok, TriggerHealth.Frame(true, true, measured, expected, 0.2));

        [Theory]
        [InlineData(5.0, 10.0)]      // 절반만 온다 = 프레임 누락
        [InlineData(20.0, 10.0)]     // 두 배 = 분주비 설정이 화면과 다르다
        public void Frame_OutsideTolerance_IsWarn(double measured, double expected)
            => Assert.Equal(TriggerLamp.Warn, TriggerHealth.Frame(true, true, measured, expected, 0.2));

        [Fact]
        public void Frame_HalfRate_IsWarnNotOk()
        {
            // ★"오는가"만 보면 통과해 버리는 경우 — 트리거는 살아 있지만 절반이 누락 중이고,
            //   이 상태로 잰 속도값은 믿을 수 없다. 초록불이면 그걸 모른 채 쓰게 된다.
            Assert.Equal(TriggerLamp.Warn, TriggerHealth.Frame(true, true, 5.0, 10.0, 0.1));
        }

        // ── 조명 판정 ─────────────────────────────────────────────────────────
        [Fact]
        public void Light_ReadFailure_IsFail()
        {
            // 읽기 실패 = 조명 상태를 "모른다". 모르는 것을 정상으로 표시하면 안 된다.
            Assert.Equal(TriggerLamp.Fail, TriggerHealth.Light(null, 2));
        }

        [Fact]
        public void Light_Off_IsFail()
            => Assert.Equal(TriggerLamp.Fail, TriggerHealth.Light(0, 2));

        [Fact]
        public void Light_WrongMode_IsWarn()
        {
            // Continuous 로 켜져 있으면 불은 들어오지만 동기가 아니라 액적이 흐른다 —
            // 눈으로는 "조명 켜짐"이라 정상으로 보이는 유형이다.
            Assert.Equal(TriggerLamp.Warn, TriggerHealth.Light(1, 2));
        }

        [Fact]
        public void Light_ExpectedMode_IsOk()
            => Assert.Equal(TriggerLamp.Ok, TriggerHealth.Light(2, 2));

        [Fact]
        public void Light_GlassViewExpectsContinuous()
        {
            // 기준 모드는 카메라마다 다르다(글라스뷰는 육안 조명이라 Continuous).
            Assert.Equal(TriggerLamp.Ok,   TriggerHealth.Light(1, 1));
            Assert.Equal(TriggerLamp.Warn, TriggerHealth.Light(2, 1));
        }

        // ── 조합 진단 ─────────────────────────────────────────────────────────
        [Fact]
        public void Diagnose_Idle_SaysNothing()
        {
            // 안 돌리는 중에 진단문이 떠 있으면 상시 경고가 되어 아무도 안 읽는다.
            Assert.Null(TriggerHealth.Diagnose(TriggerLamp.Idle, TriggerLamp.Idle, TriggerLamp.Idle));
        }

        [Fact]
        public void Diagnose_AllOk_SaysNothing()
            => Assert.Null(TriggerHealth.Diagnose(TriggerLamp.Ok, TriggerLamp.Ok, TriggerLamp.Ok));

        [Fact]
        public void Diagnose_ChainFailed_PointsAtCountersAndDriver()
        {
            string? d = TriggerHealth.Diagnose(TriggerLamp.Fail, TriggerLamp.Idle, TriggerLamp.Idle);
            Assert.NotNull(d);
            Assert.Contains("NI-DAQmx", d!);
        }

        [Fact]
        public void Diagnose_LightOkButNoFrame_PointsAtCamera()
        {
            // ★이 표시가 있는 이유. 조명이 번쩍이니 트리거가 나가고 있다고 믿고
            //   엉뚱한 데를 뒤지게 되는 경우다 — 실제 원인은 카메라 쪽이다.
            string? d = TriggerHealth.Diagnose(TriggerLamp.Ok, TriggerLamp.Ok, TriggerLamp.Fail);
            Assert.NotNull(d);
            Assert.Contains("TriggerSource", d!);
            Assert.Contains("광절연", d!);
        }

        [Fact]
        public void Diagnose_LightFailure_TakesPrecedenceOverFrame()
        {
            // 조명이 죽어 있으면 프레임이 없는 것은 당연한 결과다 — 원인을 가리켜야지
            // 증상을 가리키면 안 된다. 카메라부터 뒤지게 만들면 안 되는 자리다.
            string? d = TriggerHealth.Diagnose(TriggerLamp.Ok, TriggerLamp.Fail, TriggerLamp.Fail);
            Assert.NotNull(d);
            Assert.Contains("iCore", d!);
            Assert.DoesNotContain("TriggerSource", d!);
        }

        [Fact]
        public void Diagnose_FrameRateMismatch_MentionsDropping()
        {
            string? d = TriggerHealth.Diagnose(TriggerLamp.Ok, TriggerLamp.Ok, TriggerLamp.Warn);
            Assert.NotNull(d);
            Assert.Contains("누락", d!);
        }
    }
}
