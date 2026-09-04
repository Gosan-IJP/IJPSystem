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

        // 하드웨어 트리거 시뮬레이션 (CameraId → 대기자들). 라이브뷰와 측정이 동시에 기다릴 수 있어
        // 카메라당 여러 대기자를 받는다 — 한 칸짜리로 두면 먼저 기다리던 쪽이 영영 안 깨어난다.
        private readonly Dictionary<string, List<TaskCompletionSource<VisionImage>>> _triggerWaiters = new();
        private readonly HashSet<string> _hwTriggerOn = new();
        private readonly object _triggerSync = new();

        /// <summary>
        /// 트리거 모드에서 프레임 하나를 기다리는 한도[ms]. 실장 드라이버의 그랩 타임아웃과 같은 값 —
        /// 넘기면 실장과 똑같이 Invalid 를 돌려준다(카메라가 죽은 게 아니라 트리거가 안 온 것).
        /// </summary>
        public int TriggerGrabTimeoutMs { get; set; } = 1000;

        public bool   IsConnected   { get; private set; } = false;
        public string ImageSavePath { get; set; } = @"C:\Logs\Vision";

        // ── 가상 드랍와쳐 합성 프레임 ─────────────────────────────────────────
        // 크기·위치를 전부 <b>µm 로</b> 정하고 프레임 폭으로 픽셀 환산한다.
        //   화면 비율(예전의 height/40)로 잡으면 해상도에 따라 액적이 터무니없이 커진다 —
        //   2856×2848 프레임에서 반경 71px = 지름 97µm 가 되어 배경억제 커널(81px)보다 커졌고,
        //   그러면 닫힘 연산이 그 구멍을 못 메워 배경에도 액적이 남는다. 결과가 대비 3 —
        //   즉 액적이 통째로 사라져 2점 측정이 노이즈만 짝짓고 있었다(2026-08-10).

        /// <summary>
        /// 가상 드랍와쳐 합성 프레임의 노즐 수. <b>0 이면 피치로 화면을 채운다</b>(실장과 같은 모습).
        /// 실측 Raw 샘플(Config/Samples/DropWatcher_Raw.png)과 맞춰 보려면 15 로 둔다.
        /// </summary>
        public int VirtualNozzleCount { get; set; } = 0;

        /// <summary>
        /// 광학 시야 가로[µm]. µm/px = 이 값 ÷ 프레임 폭 — 검출기(DropWatcherProcessor)와 같은 규칙이라
        /// 해상도를 바꿔도 합성 프레임과 검출이 같은 스케일을 본다.
        /// (GOX-8105M-PGE + VS-TCH4-65 4.0X = 1.9564mm)
        /// </summary>
        public double VirtualFieldOfViewXUm { get; set; } = 1956.4;

        /// <summary>합성 액적의 지름[µm]. 실장 실측이 26~35µm 대다.</summary>
        public double VirtualDropDiameterUm { get; set; } = 30.0;

        /// <summary>합성 노즐 피치[µm]. 헤드 사양값(S800 = 84.7µm).</summary>
        public double VirtualNozzlePitchUm { get; set; } = 84.7;

        /// <summary>토출 속도[m/s] = µm/µs. 2점 측정이 되돌려 줘야 하는 정답값이다.</summary>
        public double VirtualDropVelocityMps { get; set; } = 5.0;

        /// <summary>
        /// 트리거에서 실제 토출까지의 시간[µs]. 낙하거리 = 속도 × (지연 − 이 값).
        ///
        /// <para>
        /// 왜 필요한가: 스트로브 지연은 <b>트리거 체인 시작</b> 기준이지 토출 순간 기준이 아니다.
        /// 이 값이 0 이면 화면 기본 지연(890µs)에서 5m/s 액적이 4450µm 아래 — 프레임 밖이라
        /// 아무것도 안 보인다. 770 은 890/920µs 에서 낙하거리가 600/750µm 가 되어
        /// 측정창(130~910µm) 한가운데 들어오도록 잡은 값이다.
        /// </para>
        /// </summary>
        public double VirtualFireDelayUs { get; set; } = 770.0;

        /// <summary>
        /// 가상 스트로브 지연[µs]. 지연을 바꾸며 두 번 찍는 2점 측정(<c>DropVelocitySequence</c>)이
        /// 가상 모드에서도 실제처럼 ΔY 를 만들게 한다.
        /// 0 이면 기준 지연(<see cref="VirtualNominalDelayUs"/>)에서 찍은 것으로 본다 — Live View 경로.
        /// </summary>
        public double VirtualStrobeDelayUs { get; set; } = 0;

        /// <summary>Live View 처럼 지연이 지정되지 않았을 때 쓸 기준 지연[µs].</summary>
        public double VirtualNominalDelayUs { get; set; } = 890.0;

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
            lock (_triggerSync)
            {
                foreach (var list in _triggerWaiters.Values)
                    foreach (var tcs in list) tcs.TrySetCanceled();
                _triggerWaiters.Clear();
                _hwTriggerOn.Clear();
            }
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

        /// <summary>
        /// 촬영. <b>카메라가 트리거 모드면 트리거가 올 때까지 프레임을 내주지 않는다</b> —
        /// 실장 카메라와 같다. 이 시늉을 내는 이유는 트리거 모드를 켜 놓고 끄지 않았을 때
        /// 생기는 "화면이 멎는" 실패가 <b>가상 모드에서도 그대로 재현</b>돼야 하기 때문이다.
        /// 무해하게 넘겨 버리면 실장에 올린 뒤에야 드러난다.
        ///
        /// <para>기다리는 한도는 <see cref="TriggerGrabTimeoutMs"/> — 실장 드라이버의
        /// 그랩 타임아웃(1초)과 같은 값이고, 넘기면 실장과 똑같이 Invalid 를 돌려준다.</para>
        /// </summary>
        public async Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true, int timeoutMs = 0)
        {
            if (!_statusMap.ContainsKey(cameraId))
                return VisionImage.Invalid(cameraId);

            if (IsHardwareTriggerOn(cameraId))
            {
                var waiter = RegisterWaiter(cameraId);
                var done   = await Task.WhenAny(waiter.Task, Task.Delay(TriggerGrabTimeoutMs));
                if (done != waiter.Task)
                {
                    RemoveWaiter(cameraId, waiter);
                    Debug.WriteLine($"[Virtual Vision] 트리거 대기 타임아웃: {cameraId} " +
                                    $"({TriggerGrabTimeoutMs}ms) — 트리거 체인이 돌고 있는지 확인하세요.");
                    return VisionImage.Invalid(cameraId);
                }
                return await waiter.Task;
            }

            return await CaptureCore(cameraId, saveToDisk);
        }

        /// <summary>트리거 모드와 무관하게 프레임을 만든다 — 트리거가 도착했을 때 쓰는 실제 촬영부.</summary>
        private async Task<VisionImage> CaptureCore(string cameraId, bool saveToDisk)
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
                pixels = GenerateDropletMono8(imgW, imgH);
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

            // 프레임마다 찍지 않는다. 라이브 뷰가 초당 수십 장을 잡으므로 한 줄씩 남기면
            // 출력창이 이것만으로 가득 차고(디버깅 중 정작 볼 줄이 밀려 사라진다),
            // 출력창 쓰기 자체가 느려 가상 모드가 실제보다 굼떠 보인다.
            // 첫 장과 이후 100장마다만 남긴다 — "돌고 있다"는 확인에는 그것으로 충분하다.
            if (status.TotalCaptureCount == 1 || status.TotalCaptureCount % 100 == 0)
                Debug.WriteLine($"[Virtual Vision] Captured: {cameraId} #{status.TotalCaptureCount}  → {image.FilePath}");
            return image;
        }

        /// <summary>
        /// 트리거 모드 전환. 가상이라도 <b>상태를 실제로 들고 있는다</b> — 이래야
        /// "켜 놓고 안 끄면 화면이 멎는다" 는 실장의 실패가 가상에서도 재현된다.
        /// </summary>
        public void SetHardwareTrigger(string cameraId, bool on)
        {
            lock (_triggerSync)
            {
                if (on) _hwTriggerOn.Add(cameraId);
                else    _hwTriggerOn.Remove(cameraId);
            }

            // 끌 때는 기다리던 쪽을 풀어 준다. 안 그러면 해제한 뒤에도 타임아웃까지 멎어 있다.
            if (!on) ReleaseWaiters(cameraId);

            Debug.WriteLine($"[Virtual Vision] HW trigger {(on ? "ON" : "OFF")}: {cameraId}");
        }

        /// <summary>이 카메라가 지금 트리거 모드인지 — 검증·테스트용.</summary>
        public bool IsHardwareTriggerOn(string cameraId)
        {
            lock (_triggerSync) return _hwTriggerOn.Contains(cameraId);
        }

        public async Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            if (!_statusMap.ContainsKey(cameraId))
                return VisionImage.Invalid(cameraId);

            var tcs = RegisterWaiter(cameraId);
            using var reg = ct.Register(() => tcs.TrySetCanceled());

            Debug.WriteLine($"[Virtual Vision] Waiting HW trigger: {cameraId}");
            return await tcs.Task;
        }

        /// <summary>
        /// 외부에서 하드웨어 트리거를 시뮬레이션할 때 호출합니다
        /// (<c>VirtualTriggerChain</c> 이 분주 주기마다 부른다).
        /// </summary>
        public async Task SimulateHardwareTrigger(string cameraId)
        {
            // 기다리는 쪽이 없으면 프레임을 만들 이유가 없다 — 만들어 봐야 버려진다.
            if (!HasWaiter(cameraId)) return;

            // saveToDisk:false — 트리거 체인이 초당 수~수십 회 호출한다. 기본값(true)으로 두면
            // 프레임마다 BMP 가 쌓여 디스크가 찬다(예전 CAM_DW 폭주와 같은 경로).
            // CaptureCore 를 직접 부른다 — CaptureAsync 로 가면 트리거 대기에 자기가 걸린다.
            var image = await CaptureCore(cameraId, saveToDisk: false);

            foreach (var tcs in TakeWaiters(cameraId)) tcs.TrySetResult(image);
        }

        // ── 트리거 대기자 관리 ────────────────────────────────────────────────
        // 대기자가 하나뿐이라고 가정하면 안 된다: 라이브뷰 타이머와 측정이 동시에 기다릴 수 있고,
        // 예전처럼 Dictionary 한 칸에 덮어쓰면 먼저 기다리던 쪽이 영영 안 깨어난다.

        private TaskCompletionSource<VisionImage> RegisterWaiter(string cameraId)
        {
            var tcs = new TaskCompletionSource<VisionImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_triggerSync)
            {
                if (!_triggerWaiters.TryGetValue(cameraId, out var list))
                    _triggerWaiters[cameraId] = list = new List<TaskCompletionSource<VisionImage>>();
                list.Add(tcs);
            }
            return tcs;
        }

        private void RemoveWaiter(string cameraId, TaskCompletionSource<VisionImage> tcs)
        {
            lock (_triggerSync)
                if (_triggerWaiters.TryGetValue(cameraId, out var list)) list.Remove(tcs);
        }

        private bool HasWaiter(string cameraId)
        {
            lock (_triggerSync)
                return _triggerWaiters.TryGetValue(cameraId, out var list) && list.Count > 0;
        }

        private List<TaskCompletionSource<VisionImage>> TakeWaiters(string cameraId)
        {
            lock (_triggerSync)
            {
                if (!_triggerWaiters.TryGetValue(cameraId, out var list)) return new();
                _triggerWaiters.Remove(cameraId);
                return list;
            }
        }

        /// <summary>트리거 모드를 껐을 때 기다리던 쪽을 즉시 풀어 준다(프레임 없이 무효로).</summary>
        private void ReleaseWaiters(string cameraId)
        {
            foreach (var tcs in TakeWaiters(cameraId))
                tcs.TrySetResult(VisionImage.Invalid(cameraId));
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
        ///
        /// ※ 크기·위치는 전부 µm 로 정하고 프레임 폭으로 환산한다 — 이유는 위 Virtual* 속성 주석 참고.
        /// </summary>
        private byte[] GenerateDropletMono8(int width, int height)
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

            // µm → px. 검출기와 같은 규칙(시야 ÷ 프레임 폭)이라 해상도가 달라도 서로 어긋나지 않는다.
            double upp = VirtualFieldOfViewXUm / Math.Max(1, width);
            if (upp <= 0) return buf;

            double pitchPx = Math.Max(4.0, VirtualNozzlePitchUm / upp);
            double rPx     = Math.Max(2.0, VirtualDropDiameterUm / 2.0 / upp);

            // 노즐 수를 안 정했으면 피치로 화면을 채운다 — 실장 카메라가 헤드의 한 구간을 보는 모습.
            // 창 중심 규약(originX = pitch/2)은 검출기 기본값과 같게 맞춘다.
            double originX = pitchPx / 2.0;
            int n = VirtualNozzleCount > 0
                  ? VirtualNozzleCount
                  : Math.Max(1, (int)((width - originX) / pitchPx) + 1);

            // 낙하거리 = 속도 × 비행시간. 지연은 트리거 기준이라 토출까지의 시간을 빼야 한다.
            // 등속 가정(공기저항·중력 무시) — 2점 측정이 되돌려 주는 속도가 설정값과 같아야 검증이 된다.
            double delayUs  = VirtualStrobeDelayUs > 0 ? VirtualStrobeDelayUs : VirtualNominalDelayUs;
            double flightUs = delayUs - VirtualFireDelayUs;
            if (flightUs <= 0) return buf;              // 아직 토출 전 — 액적이 없는 것이 맞다

            double fallUm = VirtualDropVelocityMps * flightUs;   // m/s × µs = µm
            double baseY  = fallUm / upp;

            for (int i = 0; i < n; i++)
            {
                double cx = originX + i * pitchPx;
                if (cx - rPx > width) break;

                // 노즐별 속도 편차 → 낙하거리 편차. 결정적(sin)이라 프레임 간 고정이고,
                // 낙하거리의 5% 로 두어 측정에서 편차가 보이되 창을 벗어나지는 않게 한다.
                double cy = baseY * (1.0 + 0.05 * Math.Sin(i * 1.7));
                if (cy - rPx < 0 || cy + rPx > height - 1) continue;   // 프레임 밖은 안 그린다

                FillDisk(buf, width, height, (int)Math.Round(cx), (int)Math.Round(cy),
                         (int)Math.Round(rPx), drop);
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
