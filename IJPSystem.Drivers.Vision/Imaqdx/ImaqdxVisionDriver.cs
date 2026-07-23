using IJPSystem.Platform.Common.Utilities;
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
        // (구 소프트 트리거 대기열은 제거 — 이제 IMAQdx 링버퍼에서 직접 트리거 프레임을 꺼낸다)

        public bool   IsConnected   { get; private set; }
        public string ImageSavePath { get; set; } = @"C:\Logs\Vision";

        // GenICam 속성 경로(카메라별로 다를 수 있음 — 실장비 기준으로 조정).
        private const string AttrExposure = "CameraAttributes::AcquisitionControl::ExposureTime::Value"; // us
        private const string AttrGain     = "CameraAttributes::AnalogControl::Gain::Value";

        /// <summary>연결 가능한 IMAQdx 카메라 이름 목록을 열거한다(연결된 것만).</summary>
        public List<string> EnumerateCameras(bool connectedOnly = true) =>
            EnumerateCameraInfos(connectedOnly)
                .Select(c => string.IsNullOrEmpty(c.InterfaceName) ? c.ModelName : c.InterfaceName)
                .ToList();

        /// <summary>IMAQdx 카메라 상세 열거(InterfaceName/Model/Serial). 실패 시 빈 목록.</summary>
        private static List<ImaqdxCameraInformation> EnumerateCameraInfos(bool connectedOnly)
        {
            var infos = new List<ImaqdxCameraInformation>();
            try
            {
                uint count = 0;
                ImaqdxInterop.IMAQdxEnumerateCameras(null, ref count, connectedOnly);   // 1차: 개수 조회
                if (count == 0) return infos;

                var arr = new ImaqdxCameraInformation[count];
                if (ImaqdxInterop.IMAQdxEnumerateCameras(arr, ref count, connectedOnly) != ImaqdxInterop.Success)
                    return infos;

                for (int i = 0; i < count && i < arr.Length; i++) infos.Add(arr[i]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IMAQdx Vision] Enumerate failed: {ex.Message}");   // DLL 미설치 등
            }
            return infos;
        }

        /// <summary>
        /// 장비에 실제 보이는 IMAQdx 카메라 목록을 로그로 남긴다 — 실장 시
        /// VisionConfig.json 의 Name(=InterfaceName, 보통 cam0/cam1…)을 맞추는 근거.
        /// </summary>
        private static void LogAvailableCameras()
        {
            var connected = EnumerateCameraInfos(connectedOnly: true);
            if (connected.Count == 0)
            {
                // 케이블/전원/서브넷 문제와 이름 불일치를 구분하기 위해 비연결 캐시까지 조회한다.
                var all = EnumerateCameraInfos(connectedOnly: false);
                LoggerService.WriteToFile("WARN",
                    all.Count == 0
                        ? "[IMAQdx Vision] 감지된 카메라 없음 — NI MAX 에서 카메라 인식 여부(케이블/전원/서브넷/방화벽) 확인 필요"
                        : "[IMAQdx Vision] 연결된 카메라 없음(비연결 캐시 " + all.Count + "대: " +
                          string.Join(", ", all.Select(c => $"{c.InterfaceName}({c.ModelName})")) +
                          ") — 케이블/전원/링크 확인 필요");
                return;
            }

            foreach (var c in connected)
                LoggerService.WriteToFile("INFO",
                    $"[IMAQdx Vision] 감지된 카메라: \"{c.InterfaceName}\" — {c.VendorName} {c.ModelName} " +
                    $"(S/N {c.SerialNumberHi:X}{c.SerialNumberLo:X8}) ← VisionConfig.json 의 Name 에 이 값을 사용");
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
            LoggerService.WriteToFile(_sessions.Count > 0 ? "INFO" : "WARN",
                $"[IMAQdx Vision] Init: {_sessions.Count}/{_configMap.Count} 카메라 연결");

            // 하나라도 못 열었으면 장비에 실제 보이는 이름을 로그로 남겨 설정 수정 근거를 준다.
            if (_sessions.Count < _configMap.Count) LogAvailableCameras();
        }

        /// <summary>IMAQdx 카메라 Open + Configure Grab. 실패(미설치/미연결) 시 false.</summary>
        private bool TryOpen(CameraDeviceInfo cfg)
        {
            string camName = string.IsNullOrEmpty(cfg.Name) ? cfg.CameraId : cfg.Name; // IMAQdx 카메라 이름
            try
            {
                uint err = ImaqdxInterop.IMAQdxOpenCamera(camName, ImaqdxCameraControlMode.Controller, out uint session);
                if (err != ImaqdxInterop.Success)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] OpenCamera 실패 ({cfg.CameraId}, Name=\"{camName}\") → {ImaqdxInterop.ErrorText(err)}");
                    return false;
                }

                // 이전 시스템(LabVIEW 트리거 체인)에서 카메라에 TriggerMode=On 이 저장돼 있으면
                // 자유촬영 Grab 이 트리거를 기다리다 전부 Timeout 난다(실장 2026-07-23 CAM_DW 0xBFF6901B).
                // 열 때 트리거 OFF 를 명시해 자유 촬영을 보장한다.
                TrySetTriggerOff(cfg.CameraId, session);

                // 픽셀 포맷 Mono8 강제 시도(분석 파이프라인이 Mono8 전제). 경로가 다르면 실패해도
                // 계속한다 — 아래 실측 조회가 버퍼 크기를 실제 포맷에 맞춰 잡아준다.
                uint pfErr = ImaqdxInterop.IMAQdxSetAttributeString(session, ImaqdxInterop.AttrPixelFormat,
                    ImaqdxAttributeType.String, "Mono8");
                if (pfErr != ImaqdxInterop.Success)
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] {cfg.CameraId}: PixelFormat=Mono8 설정 실패 → {ImaqdxInterop.ErrorText(pfErr)}");

                // ★ 실측 프레임 크기 조회 — 설정값(cfg)으로 버퍼를 잡으면 실제 프레임이 더 클 때
                //   GetImageData 가 관리 버퍼 밖을 덮어써 프로세스가 즉사한다(실장 2026-07-23 크래시).
                QueryFrameInfo(cfg, session);

                uint cfgErr = ImaqdxInterop.IMAQdxConfigureGrab(session);
                if (cfgErr != ImaqdxInterop.Success)
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] ConfigureGrab 경고 ({cfg.CameraId}) → {ImaqdxInterop.ErrorText(cfgErr)}");

                _sessions[cfg.CameraId] = session;
                LoggerService.WriteToFile("INFO", $"[IMAQdx Vision] 카메라 열기 성공: {cfg.CameraId} ← \"{camName}\"");
                return true;
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN",
                    $"[IMAQdx Vision] OpenCamera 예외 ({cfg.CameraId}, Name=\"{camName}\"): {ex.Message}");
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
            // 트리거 획득 중인 세션은 먼저 정지·해제한다(획득 중 Close 는 실패할 수 있다).
            foreach (var id in _triggeredSessions.ToList())
                if (_sessions.TryGetValue(id, out uint ts)) StopTriggeredAcquisition(id, ts);

            foreach (var s in _sessions.Values)
            {
                try { ImaqdxInterop.IMAQdxCloseCamera(s); } catch { /* 해제 오류 무시 */ }
            }
            _sessions.Clear();
            _frameInfo.Clear();
            IsConnected = false;
        }

        // ── 2. 상태 조회 ────────────────────────────────────────────
        public CameraStatus GetStatus(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s : new CameraStatus { CameraId = cameraId };

        public List<CameraStatus> GetAllStatus() =>
            _statusMap.Values.OrderBy(s => s.CameraId).ToList();

        // ── 3. 촬영 ─────────────────────────────────────────────────
        public async Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true)
        {
            if (!_sessions.TryGetValue(cameraId, out uint session) ||
                !_configMap.TryGetValue(cameraId, out var cfg))
                return VisionImage.Invalid(cameraId);

            var status = _statusMap[cameraId];
            status.IsCapturing = true;
            try
            {
                var now = DateTime.Now;
                var fi  = GetFrameInfo(cfg);   // 실측 크기 — 설정값과 다르면 실측이 이긴다
                var (path, buffer) = await Task.Run(() => Grab(session, cfg, fi, now, saveToDisk));

                status.LastCaptureTime = now;
                status.TotalCaptureCount++;

                return new VisionImage
                {
                    CameraId     = cameraId,
                    CaptureTime  = now,
                    Width        = fi.Width,
                    Height       = fi.Height,
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
        private (string? path, byte[]? buffer) Grab(uint session, CameraDeviceInfo cfg, FrameInfo fi,
                                                    DateTime ts, bool saveToDisk)
        {
            int w = fi.Width, h = fi.Height;
            if (w <= 0 || h <= 0) return (null, null);

            // 실측 크기(payload 포함 최대치)로 수신 — 작게 잡으면 네이티브가 버퍼 밖을 덮어쓴다.
            var raw = new byte[RawBufferSize(fi)];
            uint err = ImaqdxInterop.IMAQdxGetImageData(session, raw, (uint)raw.Length,
                ImaqdxBufferNumberMode.Last, 0, out _);
            if (err != ImaqdxInterop.Success)
            {
                LogGrabError(cfg.CameraId, err, w, h);
                return (null, null);
            }
            _lastGrabErr = 0;   // 정상 복귀 — 다음 오류는 다시 즉시 로그
            var buffer = ToMono8(raw, fi);

            // 라이브 미리보기(saveToDisk=false)는 파일을 남기지 않는다 — 프레임마다 BMP 가 쌓이는 것을 막는다.
            if (!saveToDisk) return (null, buffer);

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

        /// <summary>세션의 실측 프레임 크기(설정 파일이 아니라 카메라가 말해주는 값).</summary>
        private sealed record FrameInfo(int Width, int Height, int BytesPerPixel, uint PayloadSize);
        private readonly Dictionary<string, FrameInfo> _frameInfo = new();

        /// <summary>
        /// 실측 Width/Height/BytesPerPixel/PayloadSize 를 조회해 저장한다. 조회 실패 시 설정값으로
        /// 폴백하되, 설정과 실측이 다르면 경고를 남긴다 — VisionConfig 를 실측값으로 맞출 것.
        /// </summary>
        private void QueryFrameInfo(CameraDeviceInfo cfg, uint session)
        {
            uint w = 0, h = 0, bpp = 0, payload = 0;
            try
            {
                ImaqdxInterop.IMAQdxGetAttributeU32(session, ImaqdxInterop.AttrWidth,         ImaqdxAttributeType.U32, out w);
                ImaqdxInterop.IMAQdxGetAttributeU32(session, ImaqdxInterop.AttrHeight,        ImaqdxAttributeType.U32, out h);
                ImaqdxInterop.IMAQdxGetAttributeU32(session, ImaqdxInterop.AttrBytesPerPixel, ImaqdxAttributeType.U32, out bpp);
                ImaqdxInterop.IMAQdxGetAttributeU32(session, ImaqdxInterop.AttrPayloadSize,   ImaqdxAttributeType.U32, out payload);
            }
            catch { /* 아래 폴백 */ }

            var fi = new FrameInfo(
                w   > 0 ? (int)w   : cfg.PixelWidth,
                h   > 0 ? (int)h   : cfg.PixelHeight,
                bpp > 0 ? (int)bpp : 1,
                payload);
            _frameInfo[cfg.CameraId] = fi;

            LoggerService.WriteToFile("INFO",
                $"[IMAQdx Vision] {cfg.CameraId}: 실측 {fi.Width}x{fi.Height}, {fi.BytesPerPixel} B/px, " +
                $"payload={fi.PayloadSize}");
            if (fi.Width != cfg.PixelWidth || fi.Height != cfg.PixelHeight)
                LoggerService.WriteToFile("WARN",
                    $"[IMAQdx Vision] {cfg.CameraId}: VisionConfig({cfg.PixelWidth}x{cfg.PixelHeight})와 실측이 다름 " +
                    "— VisionConfig.json 을 실측값으로 수정 권장");
            if (fi.BytesPerPixel != 1)
                LoggerService.WriteToFile("WARN",
                    $"[IMAQdx Vision] {cfg.CameraId}: Mono8 이 아님({fi.BytesPerPixel} B/px) — 표시/분석 품질 저하. " +
                    "NI MAX 에서 PixelFormat 을 Mono8 로 저장할 것");
        }

        private FrameInfo GetFrameInfo(CameraDeviceInfo cfg) =>
            _frameInfo.TryGetValue(cfg.CameraId, out var fi)
                ? fi
                : new FrameInfo(cfg.PixelWidth, cfg.PixelHeight, 1, 0);

        /// <summary>수신 버퍼 크기 — payload 와 W×H×B/px 중 큰 쪽(어느 쪽이 틀려도 오버런 방지).</summary>
        private static int RawBufferSize(FrameInfo fi) =>
            (int)Math.Max(fi.PayloadSize, (uint)(fi.Width * fi.Height * fi.BytesPerPixel));

        /// <summary>
        /// 수신 원본을 표시/분석용 Mono8 로 변환. 1 B/px 면 그대로(필요 시 절단),
        /// 다중 바이트(Mono10/12/16 등)면 리틀엔디언 상위 바이트를 취한다(안전 우선 폴백).
        /// </summary>
        private static byte[] ToMono8(byte[] raw, FrameInfo fi)
        {
            int n = fi.Width * fi.Height;
            if (fi.BytesPerPixel <= 1)
            {
                if (raw.Length == n) return raw;
                var exact = new byte[n];
                Array.Copy(raw, exact, Math.Min(n, raw.Length));
                return exact;
            }
            var mono = new byte[n];
            int bpp = fi.BytesPerPixel;
            for (int i = 0; i < n && (i + 1) * bpp <= raw.Length; i++)
                mono[i] = raw[i * bpp + bpp - 1];   // little-endian MSB
            return mono;
        }

        /// <summary>
        /// 카메라의 하드웨어 트리거를 끈다(자유 촬영 준비). Selector 를 먼저 지정해야
        /// Mode 가 올바른 트리거(FrameStart)에 적용된다. 경로가 카메라와 달라 실패하면
        /// 로그만 남기고 계속한다 — 이 경우 NI MAX 에서 실제 경로 확인 필요.
        /// </summary>
        private void TrySetTriggerOff(string cameraId, uint session)
        {
            var t = TriggerConfig;
            try
            {
                ImaqdxInterop.IMAQdxSetAttributeString(session, t.SelectorPath,
                    ImaqdxAttributeType.String, t.SelectorValue);
                uint err = ImaqdxInterop.IMAQdxSetAttributeString(session, t.ModePath,
                    ImaqdxAttributeType.String, "Off");
                if (err == ImaqdxInterop.Success)
                    LoggerService.WriteToFile("INFO",
                        $"[IMAQdx Vision] {cameraId}: TriggerMode=Off — 자유 촬영 준비");
                else
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] {cameraId}: TriggerMode Off 실패 → {ImaqdxInterop.ErrorText(err)} " +
                        $"— \"{t.ModePath}\" 경로를 NI MAX 로 확인");
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN",
                    $"[IMAQdx Vision] {cameraId}: TriggerMode Off 시도 예외: {ex.Message}");
            }
        }

        // 촬영 실패는 라이브(5fps)에서 반복되므로 그대로 로그하면 스팸이 된다 —
        // 오류 코드가 바뀔 때 즉시, 같은 코드는 25회마다 한 번 남긴다.
        private uint _lastGrabErr;
        private int  _grabErrCount;

        private void LogGrabError(string cameraId, uint err, int w, int h)
        {
            if (err != _lastGrabErr) { _lastGrabErr = err; _grabErrCount = 0; }
            if (_grabErrCount++ % 25 == 0)
                LoggerService.WriteToFile("WARN",
                    $"[IMAQdx Vision] GetImageData 실패 ({cameraId}, 실측 {w}x{h}): " +
                    $"{ImaqdxInterop.ErrorText(err)} (누적 {_grabErrCount}회)");
        }

        /// <summary>카메라별 하드웨어 트리거 설정. 미지정이면 기본값(GenICam 표준 명칭 추정).</summary>
        public ImaqdxTriggerConfig TriggerConfig { get; set; } = new();

        // 트리거 모드로 획득이 시작된 세션. 중복 Configure/Start 를 막는다.
        private readonly HashSet<string> _triggeredSessions = new();

        /// <summary>
        /// 하드웨어 트리거 1회를 기다려 그 프레임을 반환한다.
        /// 최초 호출 시 카메라를 트리거 모드로 전환하고 연속 획득을 시작한 뒤,
        /// 이후에는 링버퍼에서 <b>다음 새 프레임</b>(BufferNumberMode.Next)을 하나씩 꺼낸다.
        ///
        /// Next 를 쓰는 이유: Last 는 새 트리거가 없어도 직전 버퍼를 즉시 돌려주므로
        /// 같은 프레임을 중복 반환한다 — 트리거당 1장이 필요한 드랍와쳐엔 맞지 않는다.
        /// </summary>
        public async Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            if (!_sessions.TryGetValue(cameraId, out uint session) ||
                !_configMap.TryGetValue(cameraId, out var cfg))
                return VisionImage.Invalid(cameraId);

            if (!EnsureTriggeredAcquisition(cameraId, session))
                return VisionImage.Invalid(cameraId);

            var fi = GetFrameInfo(cfg);   // 실측 크기 — 작게 잡으면 네이티브 버퍼 오버런
            int w = fi.Width, h = fi.Height;
            if (w <= 0 || h <= 0) return VisionImage.Invalid(cameraId);

            var status = _statusMap[cameraId];
            var raw = new byte[RawBufferSize(fi)];

            // 타임아웃은 "트리거가 아직 안 왔다"는 정상 상태다(세션 유효 → 재시도 가능).
            // 다만 카메라 단선 같은 진짜 오류도 같은 경로로 오므로, 연속 실패가 쌓이면 포기한다.
            const int maxConsecutiveFailures = 3;
            int failures = 0;

            while (!ct.IsCancellationRequested)
            {
                uint err = await Task.Run(() => ImaqdxInterop.IMAQdxGetImageData(
                    session, raw, (uint)raw.Length,
                    ImaqdxBufferNumberMode.Next, 0, out _), ct).ConfigureAwait(false);

                if (err == ImaqdxInterop.Success)
                {
                    var now = DateTime.Now;
                    status.LastCaptureTime = now;
                    status.TotalCaptureCount++;
                    return new VisionImage
                    {
                        CameraId     = cameraId,
                        CaptureTime  = now,
                        Width        = w,
                        Height       = h,
                        IsValid      = true,
                        FilePath     = null,          // 트리거 촬영은 파일로 남기지 않는다(초당 수~수십 장)
                        PixelData    = ToMono8(raw, fi),
                        BitsPerPixel = 8,
                    };
                }

                if (++failures >= maxConsecutiveFailures)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] 트리거 대기 실패 {failures}회 ({cameraId}): {ImaqdxInterop.ErrorText(err)}");
                    return VisionImage.Invalid(cameraId);
                }
            }

            ct.ThrowIfCancellationRequested();
            return VisionImage.Invalid(cameraId);
        }

        /// <summary>
        /// 카메라를 하드웨어 트리거 모드로 전환하고 연속 획득을 시작한다(세션당 1회).
        /// ※ TriggerSelector 를 <b>가장 먼저</b> 설정해야 한다 — GenICam 셀렉터라 뒤따르는
        ///   Mode/Source/Activation 의 적용 대상을 결정한다.
        /// </summary>
        private bool EnsureTriggeredAcquisition(string cameraId, uint session)
        {
            if (_triggeredSessions.Contains(cameraId)) return true;

            var t = TriggerConfig;
            try
            {
                if (t.Enabled)
                {
                    // 경로가 틀리면 여기서 실패한다 — 어떤 경로였는지 로그에 남겨야 고칠 수 있다.
                    if (!SetEnum(session, t.SelectorPath,   t.SelectorValue))   return false;
                    if (!SetEnum(session, t.ModePath,       t.ModeValue))       return false;
                    if (!SetEnum(session, t.SourcePath,     t.SourceValue))     return false;
                    if (!SetEnum(session, t.ActivationPath, t.ActivationValue)) return false;
                }

                ImaqdxInterop.IMAQdxSetAttributeU32(session, ImaqdxInterop.AttrTimeout,
                    ImaqdxAttributeType.U32, t.TimeoutMs);

                // ConfigureGrab(초기 Open 시 설정됨)과 배타적이므로 해제 후 연속 획득으로 전환한다.
                ImaqdxInterop.IMAQdxUnconfigureAcquisition(session);

                uint err = ImaqdxInterop.IMAQdxConfigureAcquisition(session, continuous: 1, bufferCount: t.BufferCount);
                if (err != ImaqdxInterop.Success)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] ConfigureAcquisition 실패 ({cameraId}): {ImaqdxInterop.ErrorText(err)}");
                    return false;
                }

                err = ImaqdxInterop.IMAQdxStartAcquisition(session);
                if (err != ImaqdxInterop.Success)
                {
                    LoggerService.WriteToFile("WARN",
                        $"[IMAQdx Vision] StartAcquisition 실패 ({cameraId}): {ImaqdxInterop.ErrorText(err)}");
                    return false;
                }

                _triggeredSessions.Add(cameraId);
                return true;
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN", $"[IMAQdx Vision] 트리거 모드 전환 실패 ({cameraId}): {ex.Message}");
                return false;   // DLL 미설치 등 — graceful
            }
        }

        /// <summary>GenICam 열거 속성을 문자열로 설정. 실패 시 경로를 로그에 남긴다.</summary>
        private static bool SetEnum(uint session, string path, string value)
        {
            uint err = ImaqdxInterop.IMAQdxSetAttributeString(session, path, ImaqdxAttributeType.String, value);
            if (err == ImaqdxInterop.Success) return true;

            // 카메라마다 CameraAttributes:: 하위 카테고리명이 달라 경로가 안 맞는 경우가 흔하다.
            // NI MAX 의 Camera Attributes 트리에서 실제 경로를 확인해 설정 파일을 고칠 것.
            LoggerService.WriteToFile("WARN", $"[IMAQdx Vision] 속성 설정 실패: \"{path}\" = \"{value}\" → " +
                            $"{ImaqdxInterop.ErrorText(err)} (NI MAX 에서 실제 경로 확인 필요)");
            return false;
        }

        /// <summary>트리거 모드 해제 — 획득 정지 후 자유 촬영 구성으로 되돌린다.</summary>
        private void StopTriggeredAcquisition(string cameraId, uint session)
        {
            if (!_triggeredSessions.Remove(cameraId)) return;
            try
            {
                ImaqdxInterop.IMAQdxStopAcquisition(session);
                ImaqdxInterop.IMAQdxUnconfigureAcquisition(session);
                if (TriggerConfig.Enabled)
                    ImaqdxInterop.IMAQdxSetAttributeString(session, TriggerConfig.ModePath,
                        ImaqdxAttributeType.String, "Off");
                ImaqdxInterop.IMAQdxConfigureGrab(session);   // 자유 촬영 복귀
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IMAQdx Vision] 트리거 모드 해제 실패 ({cameraId}): {ex.Message}");
            }
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
                try { ImaqdxInterop.IMAQdxSetAttributeF64(session, AttrExposure, ImaqdxAttributeType.F64, ms * 1000.0); } // ms→us
                catch (Exception ex) { Debug.WriteLine($"[IMAQdx Vision] SetExposure failed: {ex.Message}"); }
            }
        }

        public void SetGain(string cameraId, double gain)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.Gain = gain;
            if (_sessions.TryGetValue(cameraId, out uint session))
            {
                try { ImaqdxInterop.IMAQdxSetAttributeF64(session, AttrGain, ImaqdxAttributeType.F64, gain); }
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
