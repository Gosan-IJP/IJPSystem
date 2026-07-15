using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Vision.Imaqdx
{
    /// <summary>
    /// NI-IMAQdx 실카메라 Vision 드라이버(스켈레톤).
    /// LabVIEW "Vision Acquisition Server / IMAQdx" 계열. VirtualVisionDriver 와 동일한
    /// IVisionDriver 계약을 따르되, 촬영/파라미터는 niimaqdx.dll 호출로 대체한다.
    /// 검사 알고리즘은 별도(Vision Processor)로 연결 대상 — 여기서는 미구현 표시.
    /// DLL 미설치/카메라 미연결 시에도 앱이 죽지 않도록 graceful 하게 처리한다.
    /// </summary>
    public class ImaqdxVisionDriver : IVisionDriver
    {
        private readonly Dictionary<string, uint>             _sessions   = new();  // CameraId → IMAQdx 세션
        private readonly Dictionary<string, CameraStatus>     _statusMap  = new();
        private readonly Dictionary<string, CameraDeviceInfo> _configMap  = new();
        private readonly Dictionary<string, TaskCompletionSource<VisionImage>> _triggerWaiters = new();

        public bool   IsConnected   { get; private set; }
        public string ImageSavePath { get; set; } = @"C:\Logs\Vision";

        // GenICam 속성 경로(카메라별로 다를 수 있음 — 실장비 기준으로 조정).
        private const string AttrExposure = "CameraAttributes::AcquisitionControl::ExposureTime::Value"; // us
        private const string AttrGain     = "CameraAttributes::AnalogControl::Gain::Value";

        /// <summary>연결 가능한 IMAQdx 카메라 이름 목록을 열거한다(연결된 것만).</summary>
        public List<string> EnumerateCameras(bool connectedOnly = true)
        {
            var names = new List<string>();
            try
            {
                uint count = 0;
                ImaqdxInterop.IMAQdxEnumerateCameras(null, ref count, connectedOnly);   // 1차: 개수 조회
                if (count == 0) return names;

                var arr = new ImaqdxCameraInformation[count];
                if (ImaqdxInterop.IMAQdxEnumerateCameras(arr, ref count, connectedOnly) != ImaqdxInterop.Success)
                    return names;

                for (int i = 0; i < count && i < arr.Length; i++)
                    names.Add(string.IsNullOrEmpty(arr[i].InterfaceName) ? arr[i].ModelName : arr[i].InterfaceName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IMAQdx Vision] Enumerate failed: {ex.Message}");   // DLL 미설치 등
            }
            return names;
        }

        // ── 1. 연결 / 초기화 ────────────────────────────────────────
        public void Initialize(List<CameraDeviceInfo> configs)
        {
            if (configs == null) return;

            _statusMap.Clear();
            _configMap.Clear();

            foreach (var cfg in configs)
            {
                if (string.IsNullOrEmpty(cfg.CameraId)) continue;

                _configMap[cfg.CameraId] = cfg;
                bool opened = TryOpen(cfg);
                _statusMap[cfg.CameraId] = new CameraStatus
                {
                    CameraId       = cfg.CameraId,
                    Name           = cfg.Name,
                    DisplayName    = cfg.DisplayName,
                    ShowInMonitor  = cfg.ShowInMonitor,
                    IsConnected    = opened,
                    ExposureMs     = cfg.DefaultExposureMs,
                    Gain           = cfg.DefaultGain,
                    LightIntensity = 128,
                };
            }

            Connect();
            Debug.WriteLine($"[IMAQdx Vision] Init: {_sessions.Count}/{_configMap.Count} camera(s) opened.");
        }

        /// <summary>IMAQdx 카메라 Open + Configure Grab. 실패(미설치/미연결) 시 false.</summary>
        private bool TryOpen(CameraDeviceInfo cfg)
        {
            try
            {
                string camName = string.IsNullOrEmpty(cfg.Name) ? cfg.CameraId : cfg.Name; // IMAQdx 카메라 이름
                uint err = ImaqdxInterop.IMAQdxOpenCamera(camName, ImaqdxCameraControlMode.Controller, out uint session);
                if (err != ImaqdxInterop.Success) return false;

                ImaqdxInterop.IMAQdxConfigureGrab(session);
                _sessions[cfg.CameraId] = session;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IMAQdx Vision] Open failed ({cfg.CameraId}): {ex.Message}");
                return false;   // DLL 미설치/카메라 미연결 — graceful
            }
        }

        public bool Connect()
        {
            IsConnected = _sessions.Count > 0;
            return IsConnected;
        }

        public void Disconnect()
        {
            foreach (var s in _sessions.Values)
            {
                try { ImaqdxInterop.IMAQdxCloseCamera(s); } catch { /* 해제 오류 무시 */ }
            }
            _sessions.Clear();

            foreach (var tcs in _triggerWaiters.Values) tcs.TrySetCanceled();
            _triggerWaiters.Clear();

            IsConnected = false;
        }

        // ── 2. 상태 조회 ────────────────────────────────────────────
        public CameraStatus GetStatus(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s : new CameraStatus { CameraId = cameraId };

        public List<CameraStatus> GetAllStatus() =>
            _statusMap.Values.OrderBy(s => s.CameraId).ToList();

        // ── 3. 촬영 ─────────────────────────────────────────────────
        public async Task<VisionImage> CaptureAsync(string cameraId)
        {
            if (!_sessions.TryGetValue(cameraId, out uint session) ||
                !_configMap.TryGetValue(cameraId, out var cfg))
                return VisionImage.Invalid(cameraId);

            var status = _statusMap[cameraId];
            status.IsCapturing = true;
            try
            {
                var now = DateTime.Now;
                var (path, buffer) = await Task.Run(() => Grab(session, cfg, now));

                status.LastCaptureTime = now;
                status.TotalCaptureCount++;

                return new VisionImage
                {
                    CameraId     = cameraId,
                    CaptureTime  = now,
                    Width        = cfg.PixelWidth,
                    Height       = cfg.PixelHeight,
                    IsValid      = buffer != null,   // 버퍼가 있으면 유효(파일 저장 실패와 무관하게 분석 가능)
                    FilePath     = path,
                    PixelData    = buffer,           // 분석(OpenCV)용 원본 Mono8 버퍼
                    BitsPerPixel = 8,
                };
            }
            finally { status.IsCapturing = false; }
        }

        /// <summary>
        /// IMAQdx 최신 버퍼를 읽어 (분석용) 원본 버퍼를 확보하고, 부가로 BMP 파일로도 저장한다.
        /// 파일 저장은 로깅/디버깅용이라 실패해도 버퍼는 그대로 반환한다. (Mono8 가정 — 실 포맷에 맞게 조정)
        /// </summary>
        private (string? path, byte[]? buffer) Grab(uint session, CameraDeviceInfo cfg, DateTime ts)
        {
            int w = cfg.PixelWidth, h = cfg.PixelHeight;
            if (w <= 0 || h <= 0) return (null, null);

            // TODO: 픽셀 포맷(Mono8/Mono16/RGB) 확인 후 버퍼 크기·변환 조정.
            var buffer = new byte[w * h];
            uint err = ImaqdxInterop.IMAQdxGetImageData(session, buffer, (uint)buffer.Length,
                ImaqdxBufferNumberMode.Last, 0, out _);
            if (err != ImaqdxInterop.Success) return (null, null);

            string? path = null;
            try
            {
                string folder = Path.Combine(ImageSavePath, cfg.CameraId, ts.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"{ts:HHmmss_fff}.bmp");
                SaveMono8Bmp(file, buffer, w, h);
                path = file;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IMAQdx Vision] Save failed: {ex.Message}");   // 저장 실패해도 분석은 계속
            }
            return (path, buffer);
        }

        public Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            // TODO: IMAQdx Listener 모드 + 하드웨어 트리거(라인) 대기.
            //       스켈레톤은 소프트 트리거(SimulateHardwareTrigger)로 완료되는 대기로 대체.
            if (!_sessions.ContainsKey(cameraId))
                return Task.FromResult(VisionImage.Invalid(cameraId));

            var tcs = new TaskCompletionSource<VisionImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _triggerWaiters[cameraId] = tcs;
            ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        }

        // ── 4. 검사 ─────────────────────────────────────────────────
        public Task<InspectionResult> InspectAsync(string cameraId, VisionImage image)
        {
            // TODO: 실제 검사 알고리즘(Vision Processor) 연결. 현재는 미구현 표시.
            if (!image.IsValid)
                return Task.FromResult(InspectionResult.Fail(cameraId, "VIS_001", "Invalid image"));

            return Task.FromResult(
                InspectionResult.Fail(cameraId, "VIS_000", "검사 알고리즘 미구현 (IMAQdx 드라이버)"));
        }

        public async Task<InspectionResult> CaptureAndInspectAsync(string cameraId)
        {
            var image = await CaptureAsync(cameraId);
            return await InspectAsync(cameraId, image);
        }

        // ── 5. 조명 제어 ────────────────────────────────────────────
        public void SetLight(string cameraId, bool on)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.IsLightOn = on;
            // TODO: 조명 컨트롤러(IO/시리얼) 연동.
        }

        public void SetLightIntensity(string cameraId, int intensity)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.LightIntensity = Math.Clamp(intensity, 0, 255);
            // TODO: 조명 밝기 명령 전송.
        }

        // ── 6. 카메라 파라미터 ──────────────────────────────────────
        public void SetExposure(string cameraId, double ms)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.ExposureMs = ms;
            if (_sessions.TryGetValue(cameraId, out uint session))
            {
                try { ImaqdxInterop.IMAQdxSetAttribute(session, AttrExposure, ImaqdxAttributeType.F64, ms * 1000.0); } // ms→us
                catch (Exception ex) { Debug.WriteLine($"[IMAQdx Vision] SetExposure failed: {ex.Message}"); }
            }
        }

        public void SetGain(string cameraId, double gain)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.Gain = gain;
            if (_sessions.TryGetValue(cameraId, out uint session))
            {
                try { ImaqdxInterop.IMAQdxSetAttribute(session, AttrGain, ImaqdxAttributeType.F64, gain); }
                catch (Exception ex) { Debug.WriteLine($"[IMAQdx Vision] SetGain failed: {ex.Message}"); }
            }
        }

        public double GetExposure(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s.ExposureMs : 0.0;

        public double GetGain(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s.Gain : 0.0;

        // ── 7. 8-bit 그레이스케일 BMP 저장 (Mono8 버퍼) ─────────────
        private static void SaveMono8Bmp(string path, byte[] mono, int w, int h)
        {
            int rowStride   = ((w + 3) / 4) * 4;    // 4바이트 정렬
            int pixelBytes  = rowStride * h;
            int paletteBytes = 256 * 4;
            int offset      = 14 + 40 + paletteBytes;
            int fileSize    = offset + pixelBytes;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // File Header
            bw.Write((byte)'B'); bw.Write((byte)'M');
            bw.Write(fileSize); bw.Write(0); bw.Write(offset);

            // DIB Header (BITMAPINFOHEADER)
            bw.Write(40); bw.Write(w); bw.Write(-h);   // 음수 높이 = top-down
            bw.Write((short)1); bw.Write((short)8);    // 8bpp
            bw.Write(0); bw.Write(pixelBytes);
            bw.Write(2835); bw.Write(2835);            // 72 DPI
            bw.Write(256); bw.Write(0);                // 팔레트 256색

            // Grayscale 팔레트
            for (int i = 0; i < 256; i++) { bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)0); }

            // Pixel Data
            var row = new byte[rowStride];
            for (int y = 0; y < h; y++)
            {
                int src = y * w;
                for (int x = 0; x < w; x++) row[x] = (src + x < mono.Length) ? mono[src + x] : (byte)0;
                for (int x = w; x < rowStride; x++) row[x] = 0;
                bw.Write(row);
            }
        }
    }
}
