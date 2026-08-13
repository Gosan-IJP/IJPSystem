using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Vision.Hikrobot
{
    /// <summary>
    /// Hikrobot MVS SDK 기반 Vision 드라이버 — 9호기 글라스뷰(MV-CU013-80GM).
    ///
    /// <para><b>왜 필요한가</b> — eBUS for JAI 무료 라이선스는 JAI 카메라만 지원해서, 하이크로봇은
    /// 화면 중앙에 평가판 워터마크가 찍히고 5fps 로 떨어졌다(2026-08-04 실장 확인).
    /// 벤더 SDK 로 직접 잡으면 워터마크 없이 38fps 가 나온다(MVS 실측).</para>
    ///
    /// <para>드랍와처(JAI)는 계속 eBUS 를 쓰고, 이 드라이버는 글라스뷰만 담당한다.
    /// 카메라별 배정은 VisionConfig 의 <c>Driver</c> 와 <see cref="CompositeVisionDriver"/> 가 처리한다.</para>
    ///
    /// <para><b>미설치 PC 에서도 안전</b> — MVS 가 없으면 어셈블리 로드를 시도하지 않고 미연결로 동작한다.
    /// MvCameraControl 타입은 <see cref="HikrobotCamera"/> 안에만 있고, 이 클래스는 카메라 핸들을
    /// <c>object</c> 로 들어 JIT 시점의 타입 로드를 피한다.</para>
    /// </summary>
    public class HikrobotVisionDriver : IVisionDriver
    {
        private readonly Dictionary<string, CameraStatus>     _statusMap = new();
        private readonly Dictionary<string, CameraDeviceInfo> _configMap = new();

        // HikrobotCamera(=MvCameraControl 참조) 를 object 로 보관 — 미설치 PC 에서 타입 로드를 막는다.
        private readonly Dictionary<string, object> _cameras = new();

        // 반복 경고 억제용(카메라당 1회). Disconnect 시 초기화.
        private readonly HashSet<string> _missingLogged = new();

        /// <summary>프레임 대기 한도.</summary>
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
                $"[Hikrobot Vision] Init — 설정 {_statusMap.Count}대 / 연결 {_cameras.Count}대.");
        }

        public bool Connect()
        {
            try
            {
                if (!HikrobotSdk.IsInstalled)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[Hikrobot Vision] MVS SDK 미설치 — 미연결로 동작합니다. {HikrobotSdk.DiagnosticPaths}");
                    IsConnected = false;
                    return true;
                }

                HikrobotSdk.EnsureLoaded();
                OpenAll();
                // 이번에 새로 연 대수가 아니라 "지금 열려 있는 대수"로 판정한다 —
                // Connect() 는 Initialize() 와 PulseMachine.Initialize() 에서 두 번 불린다.
                IsConnected = _cameras.Count > 0;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                LoggerService.WriteToFile("ERROR",
                    $"[Hikrobot Vision] 초기화 실패 — 미연결로 동작: {ex.GetType().Name}: {ex.Message}");
            }
            return true;   // graceful — 앱은 계속 진행(화면은 미연결 표시)
        }

        /// <summary>
        /// 열거해 설정된 카메라를 연다. 반환값 = 이번에 새로 연 대수.
        /// MvCameraControl 타입을 쓰므로 이 메서드 JIT 시 어셈블리가 로드된다(해석기 등록 이후여야 함).
        /// </summary>
        private int OpenAll()
        {
            // 이미 연 카메라는 건너뛴다 — GigE 는 단독 제어권이라 재연결하면 우리 자신에게 거부당한다.
            var pending = _configMap.Where(kv => !_cameras.ContainsKey(kv.Key))
                                    .Select(kv => (Id: kv.Key, Cfg: kv.Value))
                                    .ToList();
            if (pending.Count == 0) return 0;

            HikrobotCamera.InitializeSdk();
            var found = HikrobotCamera.Enumerate();

            if (found.Count == 0)
            {
                LoggerService.WriteToFile("WARN",
                    "[Hikrobot Vision] 네트워크에서 GigE 카메라를 찾지 못했습니다 — " +
                    "MVS 로 검출되는지, 카메라 NIC 이 같은 서브넷인지 확인하세요.");
                return 0;
            }

            // 발견된 장치를 전부 남긴다 — 실장에서 MAC/IP 를 VisionConfig 로 옮겨 적기 위함.
            foreach (var d in found)
                LoggerService.WriteToFile("INFO", $"[Hikrobot Vision] 발견: {HikrobotCamera.Describe(d)}");

            int opened = 0;
            foreach (var (id, cfg) in pending)
            {
                var info = HikrobotCamera.Match(found, cfg);
                if (info == null)
                {
                    if (_missingLogged.Add(id))
                        LoggerService.WriteToFile("WARN",
                            $"[Hikrobot Vision] {id}({cfg.Name}) 일치 카메라 없음 — " +
                            $"찾는 값: MAC='{cfg.MacAddress}' SN='{cfg.SerialNumber}' IP='{cfg.IpAddress}'. " +
                            "위 '발견:' 로그의 값을 VisionConfig.json 에 옮겨 적으세요.");
                    continue;
                }

                var cam = new HikrobotCamera(id);
                if (!cam.Open(info, cfg)) { cam.Dispose(); continue; }

                _cameras[id] = cam;
                _statusMap[id].IsConnected = true;
                if (cam.Width > 0 && cam.Height > 0) WarnIfSizeMismatch(cfg, cam.Width, cam.Height);
                opened++;
            }
            return opened;
        }

        /// <summary>실측 해상도와 설정이 다르면 경고 — µm/px 스케일이 틀어지는 원인이 된다.</summary>
        private static void WarnIfSizeMismatch(CameraDeviceInfo cfg, int w, int h)
        {
            if (cfg.PixelWidth == w && cfg.PixelHeight == h) return;
            LoggerService.WriteToFile("WARN",
                $"[Hikrobot Vision] {cfg.CameraId} 해상도 불일치 — 설정 {cfg.PixelWidth}x{cfg.PixelHeight}, " +
                $"실측 {w}x{h}. VisionConfig 를 실측값으로 맞추세요.");
        }

        public void Disconnect()
        {
            foreach (var cam in _cameras.Values.OfType<IDisposable>()) { try { cam.Dispose(); } catch { } }
            _cameras.Clear();
            foreach (var s in _statusMap.Values) s.IsConnected = false;
            IsConnected = false;
            _missingLogged.Clear();

            // 카메라를 모두 닫은 뒤에만 SDK 를 내린다.
            try { HikrobotCamera.FinalizeSdk(); } catch { }
        }

        private HikrobotCamera? Cam(string cameraId) =>
            _cameras.TryGetValue(cameraId, out var o) ? o as HikrobotCamera : null;

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
                LoggerService.WriteToFile("WARN", $"[Hikrobot Vision] {cfg.CameraId} 이미지 저장 실패: {ex.Message}");
                return null;   // 저장 실패해도 버퍼는 유효 → 분석은 계속
            }
        }

        public void SetHardwareTrigger(string cameraId, bool on)
        {
            if (_configMap.TryGetValue(cameraId, out var cfg)) Cam(cameraId)?.SetHardwareTrigger(cfg, on);
        }

        /// <summary>
        /// 트리거 동기 프레임 하나. 카메라가 트리거 모드면 스트림이 트리거가 올 때까지 프레임을
        /// 내주지 않으므로, 여기서 하는 "다음 프레임 대기"가 곧 트리거 대기다.
        /// 횟수가 아니라 취소 토큰으로 끊는다(분주비가 크면 프레임 간격이 길어 금방 포기하면 안 된다).
        /// </summary>
        public async Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            if (Cam(cameraId) == null) return VisionImage.Invalid(cameraId);

            while (!ct.IsCancellationRequested)
            {
                var img = await CaptureAsync(cameraId, saveToDisk: false).ConfigureAwait(false);
                if (img.IsValid) return img;
            }
            return VisionImage.Invalid(cameraId);
        }

        // ── 4. 검사 ─────────────────────────────────────────────────────────
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
        // 조명은 카메라가 아니라 별도 컨트롤러가 담당한다. 여기서는 상태만 반영.
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
