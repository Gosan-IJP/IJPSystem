using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IJPSystem.Drivers.Vision;
using IJPSystem.Platform.Domain.Models.Vision;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 하드웨어 트리거 전환의 <b>동작</b> 검증 — 카메라도 DAQ 도 없이 확인할 수 있는 부분.
    ///
    /// <para>가상 드라이버는 트리거 모드를 실제로 흉내낸다(켜면 트리거 없이는 프레임을 안 준다).
    /// 그래서 여기서 잡히는 실패는 실장에서도 같은 실패다 — 특히 <b>켜 놓고 끄지 않는</b> 유형은
    /// 실장에서 "카메라가 죽었다" 로 보이기 때문에 원인 추적이 오래 걸린다.</para>
    ///
    /// <para>여기서 확인할 수 <b>없는</b> 것: GenICam 노드 이름이 실제 카메라에 있는지,
    /// TriggerSource 의 Line 번호가 배선과 맞는지, DAQ 가 실제로 펄스를 내는지.
    /// 그건 장비에서만 확인된다.</para>
    /// </summary>
    public class VirtualHardwareTriggerTests
    {
        private const string Cam = "CAM_DW";

        private static VirtualVisionDriver Driver(int timeoutMs = 200)
        {
            var d = new VirtualVisionDriver { TriggerGrabTimeoutMs = timeoutMs };
            d.Connect();
            d.Initialize(new List<CameraDeviceInfo>
            {
                new() { CameraId = Cam, Name = Cam, PixelWidth = 320, PixelHeight = 240 }
            });
            return d;
        }

        [Fact]
        public async Task 트리거_모드가_꺼져_있으면_자유_촬영된다()
        {
            var d = Driver();
            Assert.False(d.IsHardwareTriggerOn(Cam));

            var img = await d.CaptureAsync(Cam, saveToDisk: false);
            Assert.True(img.IsValid);
        }

        [Fact]
        public async Task 트리거_모드에서는_트리거가_와야_프레임이_나온다()
        {
            var d = Driver();
            d.SetHardwareTrigger(Cam, true);

            var capture = d.CaptureAsync(Cam, saveToDisk: false);

            // 트리거를 보내기 전에는 끝나지 않아야 한다.
            Assert.False(capture.IsCompleted);

            await d.SimulateHardwareTrigger(Cam);
            var img = await capture;

            Assert.True(img.IsValid);
        }

        [Fact]
        public async Task 트리거가_오지_않으면_타임아웃으로_무효_프레임이_된다()
        {
            // 실장 카메라와 같은 결말 — 영원히 멎지 않고 그랩 타임아웃으로 빠져나온다.
            var d = Driver(timeoutMs: 100);
            d.SetHardwareTrigger(Cam, true);

            var sw = Stopwatch.StartNew();
            var img = await d.CaptureAsync(Cam, saveToDisk: false);
            sw.Stop();

            Assert.False(img.IsValid);
            Assert.True(sw.ElapsedMilliseconds >= 90, $"너무 빨리 포기했다: {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task 트리거_모드를_끄면_다시_자유_촬영된다()
        {
            // ★ 이 시험이 지키는 것: 체인을 멈춘 뒤 카메라를 되돌리지 않으면 화면이 멎는다.
            var d = Driver();
            d.SetHardwareTrigger(Cam, true);
            d.SetHardwareTrigger(Cam, false);

            Assert.False(d.IsHardwareTriggerOn(Cam));
            Assert.True((await d.CaptureAsync(Cam, saveToDisk: false)).IsValid);
        }

        [Fact]
        public async Task 트리거_모드를_끄면_기다리던_촬영이_즉시_풀린다()
        {
            // 해제했는데 타임아웃까지 멎어 있으면 "해제가 안 먹는다" 로 보인다.
            var d = Driver(timeoutMs: 5000);
            d.SetHardwareTrigger(Cam, true);

            var capture = d.CaptureAsync(Cam, saveToDisk: false);
            d.SetHardwareTrigger(Cam, false);

            var done = await Task.WhenAny(capture, Task.Delay(1000));
            Assert.Same(capture, done);
        }

        [Fact]
        public async Task 라이브뷰와_측정이_같이_기다려도_둘_다_깨어난다()
        {
            // 대기자를 한 칸에 덮어쓰면 먼저 기다리던 쪽이 영영 안 깨어난다.
            var d = Driver(timeoutMs: 5000);
            d.SetHardwareTrigger(Cam, true);

            var live    = d.CaptureAsync(Cam, saveToDisk: false);
            var measure = d.WaitForHardwareTriggerAsync(Cam, CancellationToken.None);

            await d.SimulateHardwareTrigger(Cam);

            var both = Task.WhenAll(live, measure);
            Assert.Same(both, await Task.WhenAny(both, Task.Delay(2000)));   // 타임아웃이 아니라 둘 다 완료

            Assert.True((await live).IsValid);
            Assert.True((await measure).IsValid);
        }

        [Fact]
        public async Task 취소하면_트리거_대기가_풀린다()
        {
            var d = Driver(timeoutMs: 5000);
            using var cts = new CancellationTokenSource();

            var wait = d.WaitForHardwareTriggerAsync(Cam, cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => wait);
        }

        [Fact]
        public void 카메라별로_트리거_모드가_따로_간다()
        {
            // 드랍와처를 트리거로 바꿨다고 글라스뷰까지 멎으면 안 된다.
            var d = new VirtualVisionDriver();
            d.Connect();
            d.Initialize(new List<CameraDeviceInfo>
            {
                new() { CameraId = "CAM_DW", Name = "CAM_DW" },
                new() { CameraId = "CAM_GV", Name = "CAM_GV" }
            });

            d.SetHardwareTrigger("CAM_DW", true);

            Assert.True(d.IsHardwareTriggerOn("CAM_DW"));
            Assert.False(d.IsHardwareTriggerOn("CAM_GV"));
        }
    }
}
