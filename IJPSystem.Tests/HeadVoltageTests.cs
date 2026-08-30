using System;
using System.Linq;
using IJPSystem.Platform.Domain.Models.Printing;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 헤드 전압 보정 셈. 하드웨어가 없어도 여기서 다 잡힌다 —
    /// 배율을 잘못 계산하면 헤드에 과전압이 걸리므로 경계값을 특히 본다.
    /// </summary>
    public class HeadVoltageScaleTests
    {
        [Theory]
        [InlineData(  0.0, 1.00)]
        [InlineData( 25.0, 1.25)]
        [InlineData(-25.0, 0.75)]
        [InlineData( 10.0, 1.10)]
        public void 보정률은_파형배율로_바뀐다(double percent, double expected)
            => Assert.Equal(expected, HeadVoltageScale.ToCoefficient(percent), 6);

        [Theory]
        [InlineData( 500.0,  25.0)]
        [InlineData(-500.0, -25.0)]
        public void 화면_범위_밖은_잘린다(double percent, double expected)
            => Assert.Equal(expected, HeadVoltageScale.ClampPercent(percent), 6);

        [Fact]
        public void 배율은_메테오_허용범위를_벗어나지_않는다()
        {
            // 화면 범위를 나중에 넓혀도 헤드로는 [0.5, 1.5] 밖이 넘어가지 않아야 한다.
            foreach (double p in new[] { -1000.0, -25.0, 0.0, 25.0, 1000.0 })
            {
                double c = HeadVoltageScale.ToCoefficient(p);
                Assert.InRange(c, HeadVoltageScale.MinCoefficient, HeadVoltageScale.MaxCoefficient);
            }
        }

        [Fact]
        public void 램프는_목표에서_정확히_끝난다()
        {
            var path = HeadVoltageScale.Ramp(0, 12, 5);
            Assert.Equal(new[] { 5.0, 10.0, 12.0 }, path);
        }

        [Fact]
        public void 램프는_내려갈_때도_목표를_지나치지_않는다()
        {
            var path = HeadVoltageScale.Ramp(10, -10, 6);
            Assert.Equal(-10.0, path[^1], 6);
            Assert.All(path, v => Assert.InRange(v, -10.0, 10.0));
        }

        [Fact]
        public void 걸음이_0이면_한번에_간다()
            => Assert.Equal(new[] { 20.0 }, HeadVoltageScale.Ramp(0, 20, 0));

        [Fact]
        public void 같은_값이면_한_걸음만_낸다()
            => Assert.Equal(new[] { 7.0 }, HeadVoltageScale.Ramp(7, 7, 5));

        [Fact]
        public void 램프_중간값도_범위_안이다()
        {
            // 시작점이 범위 밖이어도 첫 걸음부터 안쪽이어야 한다.
            var path = HeadVoltageScale.Ramp(999, -999, 7);
            Assert.All(path, v => Assert.InRange(v, HeadVoltageScale.MinPercent, HeadVoltageScale.MaxPercent));
        }
    }

    /// <summary>가상 경로 — 값을 기억만 하고 보내지 않는다.</summary>
    public class VirtualHeadVoltageTests
    {
        [Fact]
        public void 가상은_항상_걸_수_있고_값을_기억한다()
        {
            var v = new Platform.Infrastructure.Devices.PrintHead.VirtualHeadVoltage();
            Assert.True(v.IsAvailable);
            Assert.Null(v.NotReadyReason);

            v.Apply(12.5);
            Assert.Equal(12.5, v.AppliedPercent, 6);
        }

        [Fact]
        public void 가상도_범위_밖은_잘린다()
        {
            var v = new Platform.Infrastructure.Devices.PrintHead.VirtualHeadVoltage();
            v.Apply(80);
            Assert.Equal(25.0, v.AppliedPercent, 6);
        }
    }

    /// <summary>실물 경로 — 헤드가 없으면 조용히 넘어가지 않고 이유를 말한다.</summary>
    public class MeteorHeadVoltageTests
    {
        [Fact]
        public void 헤드가_설정되지_않았으면_걸_수_없다()
        {
            var hv = new Platform.Infrastructure.Devices.PrintHead.MeteorHeadVoltage(status: null);
            Assert.False(hv.IsAvailable);
            Assert.NotNull(hv.NotReadyReason);
        }

        [Fact]
        public void 못_걸면_던진다_조용히_넘어가지_않는다()
        {
            var hv = new Platform.Infrastructure.Devices.PrintHead.MeteorHeadVoltage(status: null);
            var ex = Assert.Throws<InvalidOperationException>(() => hv.Apply(10));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.Equal(0.0, hv.AppliedPercent);   // 실패했으면 걸린 것으로 남지 않는다
        }
    }
}
