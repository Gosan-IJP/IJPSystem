using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Vision
{
    /// <summary>
    /// 실제 카메라 없이 동작하는 가상 Vision 드라이버.
    /// IVisionDriver를 구현하며 Motion/IO 가상 드라이버와 동일한 패턴을 따릅니다.
    /// </summary>
    public class VirtualVisionDriver : IVisionDriver
    {
        private readonly Dictionary<string, CameraStatus>      _statusMap  = new();
        private readonly Dictionary<string, CameraDeviceInfo>  _configMap  = new();
        private readonly Dictionary<string, int>               _captureSeq = new();  // 카메라별 캡처 순번(액적 낙하 시뮬레이션용)
        private readonly Random _rng = new();

        // 하드웨어 트리거 시뮬레이션용 (CameraId → TCS)
        private readonly Dictionary<string, TaskCompletionSource<VisionImage>> _triggerWaiters = new();

        public bool   IsConnected   { get; private set; } = false;
        public string ImageSavePath { get; set; } = @"C:\Logs\Vision";

        /// <summary>
        /// 가상 드랍와쳐 합성 프레임의 노즐 수. 실측 Raw 샘플(Config/Samples/DropWatcher_Raw.png)이
        /// 15개라 기본값을 맞춰 둔다 — 가상/실측 결과를 바로 비교할 수 있어야 한다.
        /// </summary>
        public int VirtualNozzleCount { get; set; } = 15;

        // ────────────────────────────────────────────────
        // 1. 연결 / 초기화
        // ────────────────────────────────────────────────

        public bool Connect()
        {
            IsConnected = true;
            Debug.WriteLine("[Virtual Vision] Connected.");
            return true;
        }

        public void Disconnect()
        {
            IsConnected = false;
            foreach (var tcs in _triggerWaiters.Values)
                tcs.TrySetCanceled();
            _triggerWaiters.Clear();
            Debug.WriteLine("[Virtual Vision] Disconnected.");
        }

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
                    IsConnected    = true,
                    ExposureMs     = cfg.DefaultExposureMs,
                    Gain           = cfg.DefaultGain,
                    LightIntensity = 128,
                };
            }

            Connect();
            Debug.WriteLine($"[Virtual Vision] Init Complete: {_statusMap.Count} camera(s).");
        }

        // ────────────────────────────────────────────────
        // 2. 상태 조회
        // ────────────────────────────────────────────────

        public CameraStatus GetStatus(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s : new CameraStatus { CameraId = cameraId };

        public List<CameraStatus> GetAllStatus() =>
            _statusMap.Values.OrderBy(s => s.CameraId).ToList();

        // ────────────────────────────────────────────────
        // 3. 촬영
        // ────────────────────────────────────────────────

        public async Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true)
        {
            if (!_statusMap.TryGetValue(cameraId, out var status))
                return VisionImage.Invalid(cameraId);

            status.IsCapturing = true;

            // 노출 시간만큼 대기 시뮬레이션 (최소 20ms)
            int delayMs = Math.Max(20, (int)status.ExposureMs + _rng.Next(5, 20));
            await Task.Delay(delayMs);

            var cfg   = _configMap[cameraId];
            var now   = DateTime.Now;

            // 캡처 순번 증가(위상 스윕에서 액적이 아래로 낙하하는 것을 시뮬레이션).
            int seq = _captureSeq.TryGetValue(cameraId, out var s0) ? s0 + 1 : 0;
            _captureSeq[cameraId] = seq;

            // 드랍와쳐 카메라는 OpenCV 액적분석을 실제로 검증할 수 있도록 합성 액적(Mono8)을 생성한다.
            // 그 외 카메라는 기존 24bit 가짜 이미지(파일)만 사용(검사도 가상 InspectAsync 랜덤 경로).
            bool isDropWatcher = cameraId.IndexOf("DW", StringComparison.OrdinalIgnoreCase) >= 0;
            byte[] pixels;
            string? filePath = null;
            int imgW, imgH, bpp;
            if (isDropWatcher)
            {
                imgW = cfg.PixelWidth  > 0 ? cfg.PixelWidth  : 1280;
                imgH = cfg.PixelHeight > 0 ? cfg.PixelHeight : 512;
                bpp  = 8;
                pixels = GenerateDropletMono8(imgW, imgH, seq);
                // saveToDisk=false(라이브 미리보기)면 파일을 남기지 않는다 — PixelData 로 화면 표시/분석 모두 가능.
                if (saveToDisk) filePath = SaveMono8Image(cameraId, now, pixels, imgW, imgH);
            }
            else
            {
                imgW = cfg.PixelWidth  > 0 ? cfg.PixelWidth  : 640;
                imgH = cfg.PixelHeight > 0 ? cfg.PixelHeight : 480;
                bpp  = 24;
                pixels = GenerateFakeBgr24(imgW, imgH);
                if (saveToDisk) filePath = SaveFakeImage(cameraId, now, pixels, imgW, imgH);
            }

            var image = new VisionImage
            {
                CameraId     = cameraId,
                CaptureTime  = now,
                Width        = imgW,
                Height       = imgH,
                IsValid      = true,
                FilePath     = filePath,
                PixelData    = pixels,
                BitsPerPixel = bpp,
            };

            status.IsCapturing      = false;
            status.LastCaptureTime  = image.CaptureTime;
            status.TotalCaptureCount++;

            Debug.WriteLine($"[Virtual Vision] Captured: {cameraId} #{status.TotalCaptureCount}  → {image.FilePath}");
            return image;
        }

        public async Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            if (!_statusMap.ContainsKey(cameraId))
                return VisionImage.Invalid(cameraId);

            var tcs = new TaskCompletionSource<VisionImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _triggerWaiters[cameraId] = tcs;

            ct.Register(() => tcs.TrySetCanceled());

            Debug.WriteLine($"[Virtual Vision] Waiting HW trigger: {cameraId}");
            return await tcs.Task;
        }

        /// <summary>
        /// 외부에서 하드웨어 트리거를 시뮬레이션할 때 호출합니다.
        /// </summary>
        public async Task SimulateHardwareTrigger(string cameraId)
        {
            if (!_triggerWaiters.TryGetValue(cameraId, out var tcs)) return;

            var image = await CaptureAsync(cameraId);
            tcs.TrySetResult(image);
            _triggerWaiters.Remove(cameraId);
        }

        // ────────────────────────────────────────────────
        // 4. 검사
        // ────────────────────────────────────────────────

        public async Task<InspectionResult> InspectAsync(string cameraId, VisionImage image)
        {
            if (!image.IsValid)
                return InspectionResult.Fail(cameraId, "VIS_001", "Invalid image");

            // 검사 처리 시간 시뮬레이션 (30~80ms)
            await Task.Delay(_rng.Next(30, 80));

            double failRate = _configMap.TryGetValue(cameraId, out var cfg) ? cfg.VirtualFailRate : 0.05;
            bool   isPass   = _rng.NextDouble() >= failRate;
            double score    = isPass
                ? 85.0 + _rng.NextDouble() * 15.0   // 85~100
                : 10.0 + _rng.NextDouble() * 40.0;  // 10~50

            InspectionResult result;
            if (isPass)
            {
                result = InspectionResult.Pass(cameraId, Math.Round(score, 1));
            }
            else
            {
                string[] ngCodes = { "VIS_101", "VIS_102", "VIS_103" };
                string[] ngDescs = { "잉크 번짐 감지", "인쇄 누락 감지", "위치 오차 초과" };
                int idx  = _rng.Next(ngCodes.Length);
                int defects = _rng.Next(1, 5);
                result = InspectionResult.Fail(cameraId, ngCodes[idx], ngDescs[idx], Math.Round(score, 1), defects);
            }

            result.Image = image;

            if (_statusMap.TryGetValue(cameraId, out var status))
                status.LastResult = result;

            Debug.WriteLine($"[Virtual Vision] Inspect {cameraId}: {(result.IsPass ? "PASS" : $"NG [{result.NgCode}]")} Score={result.Score}");
            return result;
        }

        public async Task<InspectionResult> CaptureAndInspectAsync(string cameraId)
        {
            var image = await CaptureAsync(cameraId);
            return await InspectAsync(cameraId, image);
        }

        // ────────────────────────────────────────────────
        // 5. 조명 제어
        // ────────────────────────────────────────────────

        public void SetLight(string cameraId, bool on)
        {
            if (!_statusMap.TryGetValue(cameraId, out var status)) return;
            status.IsLightOn = on;
            Debug.WriteLine($"[Virtual Vision] Light {cameraId}: {(on ? "ON" : "OFF")}");
        }

        public void SetLightIntensity(string cameraId, int intensity)
        {
            if (!_statusMap.TryGetValue(cameraId, out var status)) return;
            status.LightIntensity = Math.Clamp(intensity, 0, 255);
            Debug.WriteLine($"[Virtual Vision] Light intensity {cameraId}: {status.LightIntensity}");
        }

        // ────────────────────────────────────────────────
        // 6. 카메라 파라미터
        // ────────────────────────────────────────────────

        public void SetExposure(string cameraId, double ms)
        {
            if (!_statusMap.TryGetValue(cameraId, out var status)) return;
            status.ExposureMs = ms;
            Debug.WriteLine($"[Virtual Vision] Exposure {cameraId}: {ms}ms");
        }

        public void SetGain(string cameraId, double gain)
        {
            if (!_statusMap.TryGetValue(cameraId, out var status)) return;
            status.Gain = gain;
            Debug.WriteLine($"[Virtual Vision] Gain {cameraId}: {gain}");
        }

        public double GetExposure(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s.ExposureMs : 0.0;

        public double GetGain(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s.Gain : 0.0;

        // ────────────────────────────────────────────────
        // 7. 가상 이미지 파일 생성 (24-bit BMP)
        // ────────────────────────────────────────────────

        private string? SaveFakeImage(string cameraId, DateTime timestamp, byte[] bgr24, int w, int h)
        {
            try
            {
                string folder = Path.Combine(ImageSavePath, cameraId,
                                             timestamp.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);

                string fileName = $"{timestamp:HHmmss_fff}.bmp";
                string filePath = Path.Combine(folder, fileName);

                SaveBgr24Bmp(filePath, bgr24, w, h);
                return filePath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Virtual Vision] Image save failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 드랍와쳐 합성 프레임(Mono8) — 실측 Raw 와 같은 구조로 만든다:
        /// 밝은 배경 위에 <b>노즐 피치로 가로로 늘어선 액적들</b>이 거의 같은 높이(낙하거리)에 찍힌 정지 화면.
        /// (스트로브가 액적을 얼어붙게 하므로 실제로도 프레임마다 움직이지 않는다)
        ///
        /// ※ 액적 위치는 <b>프레임마다 고정</b>이어야 한다. 노즐별 편차도 seq/난수가 아니라
        ///   결정적 함수로 준다 — 매 프레임 흔들리면 Live View 에서 화면이 출렁이는 것처럼 보인다.
        ///   (배경 노이즈만 프레임마다 변해 '라이브' 느낌을 준다)
        /// </summary>
        private byte[] GenerateDropletMono8(int width, int height, int seq)
        {
            const byte bg   = 205;   // 밝은 배경(백라이트) — 중앙 기준
            const byte drop = 35;    // 어두운 액적(실루엣)
            var buf = new byte[width * height];

            // 배경: 중앙이 밝고 가장자리로 갈수록 어두운 조명 그라디언트 + 약한 노이즈.
            // 실측 Raw 와 같은 구조로 만들어야 BlackHat 배경억제 경로가 가상모드에서도 실제처럼 검증된다.
            // (노이즈만 프레임마다 변해 '라이브' 느낌을 준다 — 액적 위치는 고정)
            double cxF = width / 2.0, cyF = height / 2.0;
            for (int y = 0; y < height; y++)
            {
                double ny = (y - cyF) / cyF;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double nx  = (x - cxF) / cxF;
                    double vig = 1.0 - 0.42 * nx * nx - 0.22 * ny * ny;   // 가장자리 감광
                    buf[row + x] = (byte)Math.Clamp(bg * vig + _rng.Next(-8, 8), 0, 255);
                }
            }

            int n     = VirtualNozzleCount;              // 노즐 수 — 실측 샘플과 맞춘다
            int pitch = width / (n + 1);                 // 노즐 피치
            int r     = Math.Max(5, height / 40);        // 액적 반경
            int baseY = (int)(height * 0.62);            // 기준 낙하거리(노즐면=상단 가정)

            for (int i = 0; i < n; i++)
            {
                int cx = pitch * (i + 1);
                // 노즐별 속도 편차 → 낙하거리 편차. 결정적(sin)이라 프레임 간 고정.
                int cy = baseY + (int)(Math.Sin(i * 1.7) * r * 0.9);
                FillDisk(buf, width, height, cx, cy, r, drop);
            }
            return buf;
        }

        /// <summary>Mono8 버퍼를 8-bit 그레이스케일 BMP 로 저장(화면 표시용). 실패 시 null.</summary>
        private string? SaveMono8Image(string cameraId, DateTime ts, byte[] mono, int w, int h)
        {
            try
            {
                string folder = Path.Combine(ImageSavePath, cameraId, ts.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"{ts:HHmmss_fff}.bmp");

                int rowStride    = ((w + 3) / 4) * 4;   // 4바이트 정렬
                int pixelBytes   = rowStride * h;
                int paletteBytes = 256 * 4;
                int offset       = 14 + 40 + paletteBytes;
                int fileSize     = offset + pixelBytes;

                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs);
                bw.Write((byte)'B'); bw.Write((byte)'M');
                bw.Write(fileSize); bw.Write(0); bw.Write(offset);
                bw.Write(40); bw.Write(w); bw.Write(-h);      // top-down
                bw.Write((short)1); bw.Write((short)8);       // 8bpp
                bw.Write(0); bw.Write(pixelBytes);
                bw.Write(2835); bw.Write(2835);
                bw.Write(256); bw.Write(0);
                for (int i = 0; i < 256; i++) { bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)0); }
                var row = new byte[rowStride];
                for (int y = 0; y < h; y++)
                {
                    int src = y * w;
                    for (int x = 0; x < w; x++) row[x] = mono[src + x];
                    for (int x = w; x < rowStride; x++) row[x] = 0;
                    bw.Write(row);
                }
                return file;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Virtual Vision] Mono8 save failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Mono8 버퍼에 (cx,cy) 중심 반경 r 의 어두운 원(액적)을 그린다.</summary>
        private static void FillDisk(byte[] buf, int w, int h, int cx, int cy, int r, byte value)
        {
            int r2 = r * r;
            int y0 = Math.Max(0, cy - r), y1 = Math.Min(h - 1, cy + r);
            int x0 = Math.Max(0, cx - r), x1 = Math.Min(w - 1, cx + r);
            for (int y = y0; y <= y1; y++)
            {
                int dy = y - cy;
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy * dy <= r2) buf[row + x] = value;
                }
            }
        }

        /// <summary>
        /// 일반 카메라용 가짜 프레임(BGR24, 패딩 없는 w*h*3 버퍼)을 만든다.
        /// 파일 저장과 분리해 둔다 — 라이브 미리보기는 이 버퍼만 쓰고 디스크에 쓰지 않는다.
        /// </summary>
        private byte[] GenerateFakeBgr24(int width, int height)
        {
            var buf = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    // 좌→우 수평 그라디언트 + 랜덤 노이즈 (카메라 이미지 시뮬레이션)
                    int   base_  = 40 + x * 170 / width;
                    int   noise  = _rng.Next(-18, 18);
                    byte  gray   = (byte)Math.Clamp(base_ + noise, 0, 255);

                    // 중앙부에 밝은 노즐 패턴 점 격자 (NJI 특성 반영)
                    bool isNozzle = (x % 40 < 4) && (y % 40 < 4)
                                    && x > 80 && x < 560 && y > 80 && y < 400;
                    if (isNozzle) gray = (byte)Math.Clamp(gray + 120, 0, 255);

                    buf[row + x * 3 + 0] = gray;                               // B
                    buf[row + x * 3 + 1] = gray;                               // G
                    buf[row + x * 3 + 2] = (byte)Math.Clamp(gray + 15, 0, 255); // R
                }
            }
            return buf;
        }

        /// <summary>BGR24 버퍼(패딩 없음)를 24-bit BMP 로 저장한다(행 4바이트 정렬은 여기서 처리).</summary>
        private static void SaveBgr24Bmp(string filePath, byte[] bgr24, int width, int height)
        {
            int rowStride    = ((width * 3 + 3) / 4) * 4;  // 4바이트 정렬
            int pixelBytes   = rowStride * height;
            int fileSize     = 54 + pixelBytes;

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // ── BMP File Header (14 bytes) ──
            bw.Write((byte)'B'); bw.Write((byte)'M');
            bw.Write(fileSize);
            bw.Write(0);    // reserved
            bw.Write(54);   // pixel data offset

            // ── DIB Header / BITMAPINFOHEADER (40 bytes) ──
            bw.Write(40);           // header size
            bw.Write(width);
            bw.Write(-height);      // negative = top-down scanline
            bw.Write((short)1);     // color planes
            bw.Write((short)24);    // bits per pixel
            bw.Write(0);            // compression: none
            bw.Write(pixelBytes);
            bw.Write(2835); bw.Write(2835); // 72 DPI
            bw.Write(0); bw.Write(0);       // color table

            // ── Pixel Data ──
            var row = new byte[rowStride];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(bgr24, y * width * 3, row, 0, width * 3);
                bw.Write(row);
            }
        }
    }
}
