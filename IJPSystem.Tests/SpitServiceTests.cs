using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 헤드 토출을 장비에 하나로 모은 것에 대한 고정.
    ///
    /// <para>
    /// 예전에는 스핏 버튼이 네 화면에 있으면서 드랍와쳐만 실제 <see cref="ISpit"/> 을 돌리고,
    /// 패턴 인쇄·P&amp;ID·웨이브폼은 자기 플래그만 뒤집었다. 헤드는 하나인데 화면마다 "지금
    /// 토출 중"의 뜻이 달라, 한 화면에서 켜 놓고 다른 화면에서 껐다고 볼 수 있었다.
    /// </para>
    /// </summary>
    [Collection(nameof(SpitServiceTests))]   // 정적 상태를 만지므로 병렬 실행 금지
    public class SpitServiceTests : IDisposable
    {
        public SpitServiceTests() => SpitService.Reset();
        public void Dispose() => SpitService.Reset();

        private static SpitSettings Settings(int nozzles = 3, double hz = 1000) => new()
        {
            Nozzles     = Enumerable.Range(1, nozzles).ToList(),
            FrequencyHz = hz,
        };

        [Fact]
        public void 설정이_없으면_가상_헤드를_쓴다()
        {
            Assert.False(SpitService.IsRealHead);
            Assert.IsType<VirtualSpit>(SpitService.Current);
        }

        [Fact]
        public void 어댑터는_한_번만_만들어진다()
        {
            // 화면마다 새로 만들면 헤드가 여러 개인 것처럼 굴게 된다.
            Assert.Same(SpitService.Current, SpitService.Current);
        }

        [Fact]
        public void 시작하면_상태가_공유된다()
        {
            Assert.False(SpitService.IsSpitting);

            Assert.True(SpitService.TryStart(Settings(), out string? reason));
            Assert.Null(reason);

            // 다른 화면이 같은 값을 본다 — 이것이 통합의 목적이다.
            Assert.True(SpitService.IsSpitting);
            Assert.True(SpitService.Current.IsSpitting);
        }

        [Fact]
        public async Task 정지하면_실제_idle_까지_확인한다()
        {
            SpitService.TryStart(Settings(), out _);

            Assert.True(await SpitService.StopAsync());
            Assert.False(SpitService.IsSpitting);
        }

        [Theory]
        [InlineData(0, 1000, "노즐")]      // 노즐 미선택
        [InlineData(3, 0,    "Frequency")] // 주파수 0
        [InlineData(3, -1,   "Frequency")]
        public void 잘못된_입력은_시작하지_않고_사유를_준다(int nozzles, double hz, string expect)
        {
            Assert.False(SpitService.TryStart(Settings(nozzles, hz), out string? reason));

            Assert.NotNull(reason);
            Assert.Contains(expect, reason);
            Assert.False(SpitService.IsSpitting);   // 실패했는데 켜져 있으면 안 된다
        }

        [Fact]
        public async Task 상태가_바뀌면_알린다()
        {
            int count = 0;
            void Handler() => count++;

            SpitService.StateChanged += Handler;
            try
            {
                SpitService.TryStart(Settings(), out _);
                Assert.Equal(1, count);

                await SpitService.StopAsync();
                Assert.Equal(2, count);
            }
            finally { SpitService.StateChanged -= Handler; }
        }

        [Fact]
        public void 범위_밖_노즐은_로그로_알린다()
        {
            var logs = new List<string>();
            void Handler(string m) => logs.Add(m);

            SpitService.Log += Handler;
            try
            {
                // 헤드 범위를 한참 넘는 번호 — 조용히 버려지면 번호 기준(0/1 시작) 오류를 못 찾는다.
                var s = new SpitSettings { Nozzles = new[] { 1, 999_999 }, FrequencyHz = 1000 };
                Assert.True(SpitService.TryStart(s, out _));

                Assert.Contains(logs, m => m.Contains("999999"));
                Assert.Contains(999_999, SpitService.Current.IgnoredNozzles);
            }
            finally { SpitService.Log -= Handler; }
        }

        /// <summary>범위 밖 노즐은 인터페이스로 노출된다 — 화면이 구현 타입을 캐스팅하지 않아도 된다.</summary>
        [Fact]
        public void 무시된_노즐은_ISpit_으로_읽는다()
        {
            ISpit spit = SpitService.Current;
            Assert.Empty(spit.IgnoredNozzles);

            SpitService.TryStart(new SpitSettings { Nozzles = new[] { 1, 2 }, FrequencyHz = 1000 }, out _);
            Assert.Empty(spit.IgnoredNozzles);
        }

        [Fact]
        public void Override_로_바꾼_뒤_Reset_하면_설정으로_돌아온다()
        {
            var fake = new VirtualSpit();
            SpitService.Override(fake);
            Assert.Same(fake, SpitService.Current);

            SpitService.Reset();
            Assert.NotSame(fake, SpitService.Current);
        }
    }
}
