using IJPSystem.Platform.Domain.Models.Motion;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 리밋을 화면 좌표로 옮기는 규칙.
    ///
    /// <para>
    /// 리밋의 뜻은 "그 방향으로 더 못 간다"이다. 따라서 <b>(−)리밋이 켜지면 (+)조그로 빠져나올 수
    /// 있어야</b> 한다. 좌표계를 뒤집은 축에서 이 대응이 깨지면, 화면이 탈출 방향을 반대로
    /// 알려주게 되고 작업자는 리밋에 더 처박는다(T축, 2026-08-12).
    /// </para>
    /// </summary>
    public class AxisLimitDisplayTests
    {
        private static AxisDeviceInfo Axis(bool invert, bool swapWiring) => new()
        {
            AxisNo = "T",
            InvertDirection  = invert,
            SwapLimitSensors = swapWiring,
        };

        [Fact]
        public void 보통_축은_하드웨어_그대로다()
        {
            Assert.False(Axis(invert: false, swapWiring: false).SwapLimitDisplay);
        }

        [Fact]
        public void 방향을_뒤집으면_리밋도_따라_뒤집힌다()
        {
            // 좌표를 미러링하면 하드웨어 −EL 이 붙은 기구 끝이 화면에서는 (+) 끝이 된다.
            // 이걸 안 뒤집으면 화면의 "(−)리밋"에서 (+)조그가 막힌다 — 탈출 방향이 반대로 보인다.
            Assert.True(Axis(invert: true, swapWiring: false).SwapLimitDisplay);
        }

        [Fact]
        public void 배선이_반대인_축도_뒤집는다()
        {
            Assert.True(Axis(invert: false, swapWiring: true).SwapLimitDisplay);
        }

        [Fact]
        public void 방향반전과_배선반전이_겹치면_상쇄된다()
        {
            // 두 번 뒤집으면 제자리다. 예전에는 이 조합을 손으로 맞추게 해 뒀는데,
            // 사람이 "방향 뒤집었으니 리밋도 켜자"고 넣으면 오히려 틀리는 구조였다.
            Assert.False(Axis(invert: true, swapWiring: true).SwapLimitDisplay);
        }
    }
}
