using System.Linq;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 가상 헤드 상태.
    ///
    /// <para>여기서 지키려는 것은 값의 정확함이 아니라 <b>가짜라는 표시가 절대 빠지지 않는 것</b>이다.
    /// 표시가 빠지면 화면만 보고 실물이 붙었다고 믿게 된다.</para>
    /// </summary>
    public class VirtualMeteorStatusTests
    {
        [Fact]
        public void 언제나_가상이라고_표시한다()
        {
            var v = new VirtualMeteorStatusMonitor();

            foreach (string s in v.Scenarios)
            {
                v.Scenario = s;
                Assert.True(v.Poll().IsSimulated, $"[{s}] 에서 표시가 빠졌다");
            }
        }

        [Fact]
        public void 설명에도_가상이라고_적는다()
        {
            // 상태바 툴팁에 그대로 나가는 문자열이다.
            var v = new VirtualMeteorStatusMonitor();

            Assert.Contains("가상", v.Poll().Detail);
        }

        [Fact]
        public void 상황_넷을_고를_수_있다()
            => Assert.Equal(4, new VirtualMeteorStatusMonitor().Scenarios.Count);

        [Fact]
        public void 모르는_상황은_정상으로_되돌린다()
        {
            var v = new VirtualMeteorStatusMonitor { Scenario = "없는상황" };

            Assert.Equal(VirtualMeteorStatusMonitor.Normal, v.Scenario);
        }

        [Fact]
        public void 카운터가_폴링마다_움직인다()
        {
            // 값이 멈춰 있으면 화면이 갱신되는지 여전히 알 수 없다.
            var v = new VirtualMeteorStatusMonitor();

            int first = v.Poll().Pccs[0].EncoderCount;
            int later = v.Poll().Pccs[0].EncoderCount;

            Assert.True(later > first, "인코더가 늘지 않는다");
        }

        [Fact]
        public void 정상에서는_폴트가_없다()
        {
            var s = new VirtualMeteorStatusMonitor { Scenario = VirtualMeteorStatusMonitor.Normal }.Poll();

            Assert.True(s.Connected);
            Assert.All(s.Pccs, p => Assert.False(p.HasFault));
            Assert.All(s.Pccs, p => Assert.False(p.DataTransferError));
        }

        [Fact]
        public void 폴트_상황은_풀어_쓸_수_있는_값을_준다()
        {
            // 화면이 숫자를 글로 바꿔 보여 주는 경로까지 확인하려는 것이다.
            var s = new VirtualMeteorStatusMonitor { Scenario = VirtualMeteorStatusMonitor.Fault }.Poll();
            var pcc = Assert.Single(s.Pccs);

            Assert.True(pcc.HasFault);
            Assert.NotEmpty(PccFaultDecoder.Decode(pcc.FaultRegister));
            Assert.Contains(s.Hdcs, h => h.State.Contains("FAULT"));
        }

        [Fact]
        public void 전송오류_상황은_그_비트만_켠다()
        {
            var s = new VirtualMeteorStatusMonitor { Scenario = VirtualMeteorStatusMonitor.TransferError }.Poll();
            var pcc = Assert.Single(s.Pccs);

            Assert.True(pcc.DataTransferError);
            Assert.False(pcc.HasFault);
        }

        [Fact]
        public void 미부착_상황은_주소가_없다()
        {
            // DHCP 로 주소를 못 받았을 때 화면이 어떻게 보이는지 확인하는 용도다.
            var s = new VirtualMeteorStatusMonitor { Scenario = VirtualMeteorStatusMonitor.NotAttached }.Poll();

            Assert.False(s.Connected);
            Assert.Equal(0, s.PccsAttached);
            Assert.Empty(s.PccsPresent);
            Assert.Empty(s.Pccs);
            Assert.Empty(s.Hdcs);
        }

        [Fact]
        public void 헤드_선택을_확인할_만큼_준다()
        {
            // 헤드가 하나뿐이면 HDC 선택 콤보가 동작하는지 확인할 수 없다.
            var s = new VirtualMeteorStatusMonitor().Poll();

            Assert.True(s.Hdcs.Count >= 2);
            Assert.All(s.Hdcs, h => Assert.Equal(1, h.PccNumber));
        }

        [Fact]
        public void 실물은_고를_상황이_없다()
        {
            // 실물 화면에 상황 콤보가 뜨면 값이 조작 가능한 것처럼 보인다.
            using var real = new MeteorStatusMonitor();

            Assert.Empty(real.Scenarios);
        }
    }
}
