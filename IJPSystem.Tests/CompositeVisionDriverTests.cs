using IJPSystem.Drivers.Vision;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 카메라별 드라이버 다중화 검증 — 9호기는 드랍와처(JAI/eBUS)와 글라스뷰(하이크로봇)가
    /// 서로 다른 드라이버를 써야 한다.
    ///
    /// <para>여기서 막으려는 사고는 <b>조용한 오라우팅</b>이다. 라우팅이 틀리면 예외 없이
    /// 엉뚱한 카메라 영상이 반환되어, 글라스뷰 화면에 드랍와처 영상이 뜨는 식으로 나타난다.
    /// 화면만 봐서는 원인을 찾기 어렵다.</para>
    /// </summary>
    public class CompositeVisionDriverTests
    {
        /// <summary>어느 카메라가 자기에게 왔는지만 기록하는 최소 드라이버.</summary>
        private sealed class FakeDriver : IVisionDriver
        {
            public string Key { get; }
            public List<string> Assigned { get; } = new();
            public List<string> Captured { get; } = new();
            public bool Connected { get; set; } = true;

            public FakeDriver(string key) => Key = key;

            public bool IsConnected => Connected;
            public bool Connect() => true;
            public void Disconnect() => Connected = false;

            public void Initialize(List<CameraDeviceInfo> configs) =>
                Assigned.AddRange(configs.Select(c => c.CameraId));

            public CameraStatus GetStatus(string cameraId) =>
                new() { CameraId = cameraId, Name = Key, IsConnected = Connected };

            public List<CameraStatus> GetAllStatus() =>
                Assigned.Select(GetStatus).ToList();

            public Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true)
            {
                Captured.Add(cameraId);
                // FilePath 에 드라이버 키를 실어 어느 드라이버가 처리했는지 확인한다.
                return Task.FromResult(new VisionImage { CameraId = cameraId, IsValid = true, FilePath = Key });
            }

            public Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct) =>
                CaptureAsync(cameraId, false);

            /// <summary>트리거 전환이 어느 카메라로 갔는지 — 라우팅 검증용.</summary>
            public List<(string CameraId, bool On)> TriggerCalls { get; } = new();
            public void SetHardwareTrigger(string cameraId, bool on) => TriggerCalls.Add((cameraId, on));

            public Task<InspectionResult> InspectAsync(string cameraId, VisionImage image) =>
                Task.FromResult(InspectionResult.Pass(cameraId, 0));

            public Task<InspectionResult> CaptureAndInspectAsync(string cameraId) =>
                Task.FromResult(InspectionResult.Pass(cameraId, 0));

            public void SetLight(string cameraId, bool on) { }
            public void SetLightIntensity(string cameraId, int intensity) { }

            public double Exposure { get; private set; }
            public void SetExposure(string cameraId, double ms) => Exposure = ms;
            public void SetGain(string cameraId, double gain) { }
            public double GetExposure(string cameraId) => Exposure;
            public double GetGain(string cameraId) => 0;
        }

        private static CameraDeviceInfo Cam(string id, string driver = "") =>
            new() { CameraId = id, Name = id, Driver = driver };

        /// <summary>키별로 드라이버를 만들되, 같은 키는 인스턴스를 재사용해 실제 구성과 맞춘다.</summary>
        private static (CompositeVisionDriver Driver, Dictionary<string, FakeDriver> Fakes) Build(
            string globalKey, params CameraDeviceInfo[] cams)
        {
            var fakes = new Dictionary<string, FakeDriver>();
            var composite = new CompositeVisionDriver(globalKey, key =>
            {
                if (!fakes.TryGetValue(key, out var f)) fakes[key] = f = new FakeDriver(key);
                return f;
            });
            composite.Initialize(cams.ToList());
            return (composite, fakes);
        }

        [Fact]
        public void 카메라별_Driver_지정이_전역설정보다_우선한다()
        {
            var (drv, fakes) = Build("ebus", Cam("CAM_DW"), Cam("CAM_GV", "hikrobot"));

            Assert.Equal(new[] { "CAM_DW" }, fakes["ebus"].Assigned);
            Assert.Equal(new[] { "CAM_GV" }, fakes["hikrobot"].Assigned);
            _ = drv;
        }

        [Fact]
        public async Task 촬영이_담당_드라이버로_라우팅된다()
        {
            var (drv, fakes) = Build("ebus", Cam("CAM_DW"), Cam("CAM_GV", "hikrobot"));

            // FilePath 에 실린 키로 어느 드라이버가 처리했는지 확인한다.
            Assert.Equal("ebus",     (await drv.CaptureAsync("CAM_DW")).FilePath);
            Assert.Equal("hikrobot", (await drv.CaptureAsync("CAM_GV")).FilePath);

            Assert.Equal(new[] { "CAM_DW" }, fakes["ebus"].Captured);
            Assert.Equal(new[] { "CAM_GV" }, fakes["hikrobot"].Captured);
        }

        [Fact]
        public void 트리거_전환이_담당_드라이버로만_간다()
        {
            // 드랍와처를 트리거 모드로 바꿀 때 글라스뷰까지 같이 바뀌면 글라스뷰 화면이 멎는다.
            var (drv, fakes) = Build("ebus", Cam("CAM_DW"), Cam("CAM_GV", "hikrobot"));

            drv.SetHardwareTrigger("CAM_DW", true);
            drv.SetHardwareTrigger("CAM_DW", false);

            Assert.Equal(new[] { ("CAM_DW", true), ("CAM_DW", false) }, fakes["ebus"].TriggerCalls);
            Assert.Empty(fakes["hikrobot"].TriggerCalls);
        }

        [Fact]
        public void 같은_드라이버를_쓰는_카메라는_인스턴스를_공유한다()
        {
            // 카메라마다 인스턴스를 만들면 같은 네트워크를 중복 열거하고 장치 점유가 충돌한다.
            var (_, fakes) = Build("ebus", Cam("CAM_DW"), Cam("CAM_GV"), Cam("CAM_03", "hikrobot"));

            Assert.Equal(2, fakes.Count);
            Assert.Equal(new[] { "CAM_DW", "CAM_GV" }, fakes["ebus"].Assigned);
        }

        [Fact]
        public async Task 모르는_카메라는_예외없이_무효이미지를_돌려준다()
        {
            var (drv, _) = Build("ebus", Cam("CAM_DW"));

            var img = await drv.CaptureAsync("없는카메라");
            Assert.False(img.IsValid);
            Assert.Equal(0.0, drv.GetExposure("없는카메라"));
        }

        [Fact]
        public void 상태목록은_설정_순서를_유지한다()
        {
            // 하위 드라이버별로 모아 내면 화면 카메라 목록 순서가 뒤바뀐다.
            var (drv, _) = Build("ebus", Cam("CAM_GV", "hikrobot"), Cam("CAM_DW"), Cam("CAM_03", "hikrobot"));

            Assert.Equal(new[] { "CAM_GV", "CAM_DW", "CAM_03" },
                         drv.GetAllStatus().Select(s => s.CameraId));
        }

        [Fact]
        public void 하나라도_연결되면_IsConnected_는_참이다()
        {
            var (drv, fakes) = Build("ebus", Cam("CAM_DW"), Cam("CAM_GV", "hikrobot"));

            fakes["ebus"].Connected = false;
            Assert.True(drv.IsConnected);          // hikrobot 은 살아 있음

            fakes["hikrobot"].Connected = false;
            Assert.False(drv.IsConnected);
        }

        [Fact]
        public void 파라미터_설정이_해당_카메라의_드라이버에만_적용된다()
        {
            var (drv, fakes) = Build("ebus", Cam("CAM_DW"), Cam("CAM_GV", "hikrobot"));

            drv.SetExposure("CAM_DW", 0.5);
            Assert.Equal(0.5, fakes["ebus"].Exposure);
            Assert.Equal(0.0, fakes["hikrobot"].Exposure);
        }
    }
}
