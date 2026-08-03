using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Vision.Ebus
{
    /// <summary>
    /// Pleora eBUS SDK 기반 Vision 드라이버 — 9호기(JAI 드랍와처 + 하이크로봇 글라스뷰).
    ///
    /// <para>NI-IMAQdx 대신 <b>벤더 중립 GigE Vision</b> 으로 두 카메라를 한 드라이버에서 잡는다.
    /// 기기별 선택은 AppConfig.json 의 <c>DriverMode.Vision</c>: 0호기="Imaqdx", 9호기="Ebus".</para>
    ///
    /// <para><b>미설치 PC 에서도 안전</b> — eBUS SDK 가 없으면 어셈블리 로드 자체를 시도하지 않고
    /// 미연결로 동작한다(앱은 계속 뜬다). PvDotNet 타입은 <see cref="EbusCamera"/> 안에만 있고,
    /// 이 클래스는 카메라 핸들을 <c>object</c> 로 들어 JIT 시점의 타입 로드를 피한다.</para>
    ///
    /// <para><b>라이선스</b> — eBUS for JAI 무료판은 JAI 카메라만 정식 지원한다. 타사 카메라는
    /// 열거·연결은 되지만 스트림에 워터마크가 찍히거나 실패한다. 이 드라이버는 발견 시점의
    /// <c>IsLicenseValid</c> 와 첫 프레임의 <c>HasWatermark</c> 를 모두 로그로 남겨 조기에 드러나게 한다.</para>
    /// </summary>
    public class EbusVisionDriver : IVisionDriver
    {
        private readonly Dictionary<string, CameraStatus>     _statusMap = new();
        private readonly Dictionary<string, CameraDeviceInfo> _configMap = new();

        // EbusCamera(=PvDotNet 참조) 를 object 로 보관 — eBUS 미설치 PC 에서 타입 로드를 막기 위함.
        private readonly Dictionary<string, object> _cameras = new();

        /// <summary>프레임 대기 한도. 스트로브 동기 촬영도 이 안에는 들어온다.</summary>
        private const uint GrabTimeoutMs = 1000;

        public bool   IsConnected   { get; private set; }
        public string ImageSavePath { get; set; } = @"C:\Logs\Vision";

        // ── 1. 연결 / 초기화 ────────────────────────────────────────────────
        public void Initialize(List<CameraDeviceInfo> configs)
        {
            if (configs == null) return;

            _statusMap.Clear();
            _configMap.Clear();

            foreach (var cfg in configs)
            {
                if (string.IsNullOrEmpty(cfg.CameraId)) continue;

                _configMap[cfg.CameraId] = cfg;
                _statusMap[cfg.CameraId] = new CameraStatus
                {
                    CameraId       = cfg.CameraId,
                    Name           = cfg.Name,
                    DisplayName    = cfg.DisplayName,
                    ShowInMonitor  = cfg.ShowInMonitor,
                    IsConnected    = false,
                    ExposureMs     = cfg.DefaultExposureMs,
                    Gain           = cfg.DefaultGain,
                    LightIntensity = 128,
                };
            }

            Connect();
            LoggerService.WriteToFile("INFO",
                $"[eBUS Vision] Init — 설정 {_statusMap.Count}대 / 연결 {_cameras.Count}대.");
        }

        public bool Connect()
        {
            try
            {
                if (!EbusSdk.IsInstalled)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[eBUS Vision] eBUS SDK 미설치({EbusSdk.InstallDir}) — 미연결로 동작합니다. " +
                        "64비트 eBUS SDK for JAI 를 설치하세요(x86 런타임이 함께 설치됩니다).");
                    IsConnected = false;
                    return true;
                }

                EbusSdk.EnsureResolver();
                IsConnected = OpenAll() > 0;
            }
            catch (Exception ex)
            {
                // BadImageFormatException(비트수), FileNotFoundException(네이티브 DLL) 등을 여기서 흡수.
                IsConnected = false;
                LoggerService.WriteToFile("ERROR",
                    $"[eBUS Vision] 초기화 실패 — 미연결로 동작: {ex.GetType().Name}: {ex.Message}");
            }
            return true;   // graceful — 앱은 계속 진행(화면은 미연결 표시)
        }

        /// <summary>
        /// 네트워크를 열거해 설정된 카메라를 모두 연다. 반환값 = 실제로 연 대수.
        /// PvDotNet 타입을 쓰므로 이 메서드가 JIT 될 때 어셈블리가 로드된다(해석기 등록 이후여야 함).
        /// </summary>
        private int OpenAll()
        {
            using var system = new PvDotNet.PvSystem();
            system.Find();

            // 발견된 장치를 전부 로그로 남긴다 — 실장에서 MAC/IP 를 VisionConfig 에 옮겨 적기 위함.
            var found = new List<PvDotNet.PvDeviceInfo>();
            for (uint i = 0; i < system.InterfaceCount; i++)
            {
                var itf = system.GetInterface(i);
                for (uint d = 0; d < itf.DeviceCount; d++) found.Add(itf.GetDeviceInfo(d));
            }

            if (found.Count == 0)
            {
                LoggerService.WriteToFile("WARN",
                    "[eBUS Vision] 네트워크에서 GigE Vision 카메라를 찾지 못했습니다 — " +
                    "eBUS Player 로 검출되는지, 카메라 NIC 에 필터 드라이버가 적용됐는지 확인하세요.");
                return 0;
            }

            foreach (var d in found)
                LoggerService.WriteToFile("INFO", $"[eBUS Vision] 발견: {EbusCamera.Describe(d)}");

            int opened = 0;
            foreach (var (id, cfg) in _configMap.Select(kv => (kv.Key, kv.Value)))
            {
                var info = EbusCamera.Match(found, cfg);
                if (info == null)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[eBUS Vision] {id}({cfg.Name}) 일치 카메라 없음 — " +
                        $"찾는 값: MAC='{cfg.MacAddress}' SN='{cfg.SerialNumber}' IP='{cfg.IpAddress}'. " +
                        "위 '발견:' 로그의 값을 VisionConfig.json 에 옮겨 적으세요.");
                    continue;
                }

                if (!info.IsLicenseValid)
                    LoggerService.WriteToFile("WARN",
                        $"[eBUS Vision] {id} 라이선스 무효 — 스트림에 워터마크가 찍히거나 실패할 수 있습니다: {info.LicenseMessage}");

                var cam = new EbusCamera(id);
                if (!cam.Open(info, cfg)) { cam.Dispose(); continue; }

                _cameras[id] = cam;
                var st = _statusMap[id];
                st.IsConnected = true;
                if (cam.Width > 0 && cam.Height > 0) WarnIfSizeMismatch(cfg, cam.Width, cam.Height);
                opened++;
            }
            return opened;
        }

        /// <summary>실측 해상도와 VisionConfig 가 다르면 경고 — µm/px 스케일이 틀어지는 원인이 된다.</summary>
        private static void WarnIfSizeMismatch(CameraDeviceInfo cfg, int w, int h)
        {
            if (cfg.PixelWidth == w && cfg.PixelHeight == h) return;
            LoggerService.WriteToFile("WARN",
                $"[eBUS Vision] {cfg.CameraId} 해상도 불일치 — 설정 {cfg.PixelWidth}x{cfg.PixelHeight}, " +
                $"실측 {w}x{h}. VisionConfig 를 실측값으로 맞추고, 드랍와처는 MicronsPerPixel 을 재교정하세요.");
        }

        public void Disconnect()
        {
            foreach (var cam in _cameras.Values.OfType<IDisposable>()) { try { cam.Dispose(); } catch { } }
            _cameras.Clear();
            foreach (var s in _statusMap.Values) s.IsConnected = false;
            IsConnected = false;
        }

        private EbusCamera? Cam(string cameraId) =>
            _cameras.TryGetValue(cameraId, out var o) ? o as EbusCamera : null;

        // ── 2. 상태 조회 ────────────────────────────────────────────────────
        public CameraStatus GetStatus(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s : new CameraStatus { CameraId = cameraId };

        public List<CameraStatus> GetAllStatus() =>
            _statusMap.Values.OrderBy(s => s.CameraId).ToList();

        // ── 3. 촬영 ─────────────────────────────────────────────────────────
        public async Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true)
        {
            var cam = Cam(cameraId);
            if (cam == null || !_configMap.TryGetValue(cameraId, out var cfg))
                return VisionImage.Invalid(cameraId);

            var status = _statusMap[cameraId];
            status.IsCapturing = true;
            try
            {
                var now = DateTime.Now;
                var (mono, w, h) = await Task.Run(() =>
                {
                    var buf = cam.Grab(GrabTimeoutMs);
                    return (buf, cam.Width, cam.Height);
                }).ConfigureAwait(false);

                if (mono == null) return VisionImage.Invalid(cameraId);

                WarnWatermarkOnce(cameraId, cam);
                status.LastCaptureTime = now;
                status.TotalCaptureCount++;

                return new VisionImage
                {
                    CameraId     = cameraId,
                    CaptureTime  = now,
                    Width        = w,
                    Height       = h,
                    IsValid      = true,
                    FilePath     = saveToDisk ? SaveBmp(cfg, mono, w, h, now) : null,
                    PixelData    = mono,
                    BitsPerPixel = 8,
                };
            }
            finally { status.IsCapturing = false; }
        }

        private readonly HashSet<string> _watermarkLogged = new();

        /// <summary>워터마크는 카메라당 1회만 로그 — 라이브 폴링에서 매 프레임 남으면 로그가 폭주한다.</summary>
        private void WarnWatermarkOnce(string cameraId, EbusCamera cam)
        {
            if (!cam.HasWatermark || !_watermarkLogged.Add(cameraId)) return;
            LoggerService.WriteToFile("ERROR",
                $"[eBUS Vision] {cameraId} 프레임에 워터마크가 있습니다 — eBUS for JAI 라이선스가 이 카메라를 " +
                "지원하지 않습니다(타사 카메라). 해당 카메라는 IMAQdx 로 돌리거나 eBUS 정식 라이선스가 필요합니다.");
        }

        /// <summary>라이브(saveToDisk=false)는 저장하지 않는다 — 프레임마다 BMP 가 쌓이면 디스크가 금방 찬다.</summary>
        private string? SaveBmp(CameraDeviceInfo cfg, byte[] mono, int w, int h, DateTime ts)
        {
            try
            {
                string folder = Path.Combine(ImageSavePath, cfg.CameraId, ts.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"{ts:HHmmss_fff}.bmp");
                Mono8Bmp.Save(file, mono, w, h);
                return file;
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN", $"[eBUS Vision] {cfg.CameraId} 이미지 저장 실패: {ex.Message}");
                return null;   // 저장 실패해도 버퍼는 유효 → 분석은 계속
            }
        }

        public async Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            // TODO(eBUS): TriggerSelector=FrameStart / TriggerMode=On / TriggerSource=LineN 구성.
            //   노드 경로·Line 번호는 JAI·하이크로봇 GenICam 트리를 실측해야 확정된다(ImaqdxTriggerConfig 참고).
            //   지금은 연속 획득 파이프라인에서 다음 프레임을 기다린다 — 스트로브가 프레임에 맞춰 발광하면
            //   실사용상 동일하게 동작한다.
            var cam = Cam(cameraId);
            if (cam == null) return VisionImage.Invalid(cameraId);

            for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
            {
                var img = await CaptureAsync(cameraId, saveToDisk: false).ConfigureAwait(false);
                if (img.IsValid) return img;
            }
            return VisionImage.Invalid(cameraId);
        }

        // ── 4. 검사 ─────────────────────────────────────────────────────────
        // 액적 분석(OpenCvSharp)은 드라이버가 아니라 DropWatcher 디바이스 계층에서 수행한다.
        public Task<InspectionResult> InspectAsync(string cameraId, VisionImage image)
        {
            if (image == null || !image.IsValid)
                return Task.FromResult(InspectionResult.Fail(cameraId, "VIS_001", "Invalid image"));
            return Task.FromResult(InspectionResult.Pass(cameraId, 0));
        }

        public async Task<InspectionResult> CaptureAndInspectAsync(string cameraId)
        {
            var image = await CaptureAsync(cameraId).ConfigureAwait(false);
            return await InspectAsync(cameraId, image).ConfigureAwait(false);
        }

        // ── 5. 조명 제어 ────────────────────────────────────────────────────
        // 조명은 카메라가 아니라 스트로브 컨트롤러(TriggerChain)가 담당한다. 여기서는 상태만 반영.
        public void SetLight(string cameraId, bool on)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.IsLightOn = on;
        }

        public void SetLightIntensity(string cameraId, int intensity)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.LightIntensity = Math.Clamp(intensity, 0, 255);
        }

        // ── 6. 카메라 파라미터 ──────────────────────────────────────────────
        public void SetExposure(string cameraId, double ms)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.ExposureMs = ms;
            if (_configMap.TryGetValue(cameraId, out var cfg)) Cam(cameraId)?.SetExposureMs(cfg, ms);
        }

        public void SetGain(string cameraId, double gain)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.Gain = gain;
            if (_configMap.TryGetValue(cameraId, out var cfg)) Cam(cameraId)?.SetGain(cfg, gain);
        }

        public double GetExposure(string cameraId)
        {
            var cam = Cam(cameraId);
            if (cam != null && _configMap.TryGetValue(cameraId, out var cfg))
            {
                double ms = cam.GetExposureMs(cfg);
                if (ms > 0) return ms;
            }
            return _statusMap.TryGetValue(cameraId, out var s) ? s.ExposureMs : 0.0;
        }

        public double GetGain(string cameraId)
        {
            var cam = Cam(cameraId);
            if (cam != null && _configMap.TryGetValue(cameraId, out var cfg))
            {
                double g = cam.GetGain(cfg);
                if (g > 0) return g;
            }
            return _statusMap.TryGetValue(cameraId, out var s) ? s.Gain : 0.0;
        }
    }
}
