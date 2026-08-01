using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Vision.Ebus
{
    /// <summary>
    /// Pleora eBUS SDK 기반 Vision 드라이버 (SDK 모드) — <b>스켈레톤</b>.
    ///
    /// 목적: 9호기는 <b>JAI(드랍와처) + 하이크로봇(글라스뷰) 혼합</b>이라, NI-IMAQdx 대신
    ///   <b>벤더 중립 GigE Vision(Pleora eBUS)</b> 로 두 카메라를 한 드라이버로 획득한다.
    ///
    /// 선택 방법(동일 코드/바이너리 유지): AppConfig.json 의 <c>DriverMode.Vision</c> 로 기기별 선택.
    ///   · 0호기 = "Imaqdx"  → ImaqdxVisionDriver (niimaqdx.dll)
    ///   · 9호기 = "Ebus"    → 이 드라이버 (eBUS x86 DLL)
    ///   두 드라이버 모두 같은 DLL 에 컴파일되며, 네이티브 P/Invoke 는 지연로드라
    ///   서로의 런타임 DLL 이 없어도 무해(선택된 드라이버만 자기 네이티브를 로드).
    ///
    /// ★현재 상태: 연결/설정/상태/파라미터 캐시는 실제로 동작하지만, <b>실제 프레임 획득과
    ///   GenICam 파라미터 쓰기는 미구현(아래 TODO(eBUS))</b>. eBUS SDK 미설치/미연동이어도
    ///   앱이 죽지 않도록 graceful(미연결) 로 동작한다(ImaqdxVisionDriver 와 동일 정책).
    ///
    /// ── 구현 체크리스트 (eBUS SDK 확보 후 채울 것) ──────────────────────────────
    ///   1. 참조/DLL: eBUS SDK <b>x86</b>(앱이 32비트). 관리(.NET) 래퍼가 .NET8 로드 안 되면
    ///      네이티브 C API 를 직접 P/Invoke(현 ImaqdxInterop.cs 와 동일 패턴)로 뺄 것.
    ///   2. 열거/연결: PvSystem/PvDeviceFinder 로 카메라 찾기 → config(IpAddress/SerialNumber/Name)
    ///      매칭 → PvDevice.CreateAndConnect → PvStream 열기 → PvPipeline 시작.
    ///   3. 파라미터: GenICam 노드로 ExposureTime(µs)/Gain/PixelFormat 읽기·쓰기.
    ///   4. 하드웨어 트리거: TriggerSelector=FrameStart, TriggerMode=On, TriggerSource=Line0,
    ///      TriggerActivation=RisingEdge (경로/값은 JAI·하이크로봇 GenICam 트리 실측). ImaqdxTriggerConfig 참고.
    ///   5. 획득: PvPipeline 에서 PvBuffer→PvImage 취득 → PixelData(Mono8/BGR) 복사 → VisionImage 구성.
    ///      라이브(saveToDisk=false)는 디스크 저장 금지(프레임 폭주 주의 — Virtual/Imaqdx 주석 참고).
    /// ────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class EbusVisionDriver : IVisionDriver
    {
        private readonly Dictionary<string, CameraStatus>     _statusMap = new();
        private readonly Dictionary<string, CameraDeviceInfo> _configMap = new();
        // TODO(eBUS): CameraId → (PvDevice, PvStream, PvPipeline, GenParameterArray) 핸들 매핑.

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
                    IsConnected    = false,               // 실제 eBUS 연결 성공 시 true 로
                    ExposureMs     = cfg.DefaultExposureMs,
                    Gain           = cfg.DefaultGain,
                    LightIntensity = 128,
                };
            }

            Connect();
            LoggerService.WriteToFile("INFO",
                $"[eBUS Vision] Init — {_statusMap.Count} camera(s) 설정 로드 (연결={IsConnected}).");
        }

        public bool Connect()
        {
            try
            {
                // TODO(eBUS): 실제 카메라 열거/연결. 성공한 카메라만 _statusMap[id].IsConnected=true + 핸들 저장.
                bool anyConnected = TryConnectEbus();
                IsConnected = anyConnected;
                if (!anyConnected)
                    LoggerService.WriteToFile("WARN",
                        "[eBUS Vision] eBUS SDK 미연동/카메라 미연결 — graceful(미연결)로 동작(스켈레톤).");
            }
            catch (DllNotFoundException ex)
            {
                IsConnected = false;
                LoggerService.WriteToFile("WARN",
                    $"[eBUS Vision] eBUS 런타임 DLL 없음 — 미연결로 동작: {ex.Message}");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                LoggerService.WriteToFile("WARN",
                    $"[eBUS Vision] 연결 실패 — 미연결로 동작: {ex.GetType().Name}: {ex.Message}");
            }
            return true;   // graceful — 앱은 계속 진행(화면은 미연결 표시)
        }

        // TODO(eBUS): eBUS 열거/연결 구현. 현재 스켈레톤이라 항상 false(미연결).
        private bool TryConnectEbus() => false;

        public void Disconnect()
        {
            // TODO(eBUS): pipeline stop → stream close → device disconnect → 핸들 해제.
            IsConnected = false;
        }

        // ── 2. 상태 조회 ────────────────────────────────────────────────────
        public CameraStatus GetStatus(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s : new CameraStatus { CameraId = cameraId };

        public List<CameraStatus> GetAllStatus() =>
            _statusMap.Values.OrderBy(s => s.CameraId).ToList();

        // ── 3. 촬영 ─────────────────────────────────────────────────────────
        public Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true)
        {
            // TODO(eBUS): PvPipeline 에서 PvBuffer→PvImage 취득 → PixelData 복사 → VisionImage 구성.
            //   saveToDisk=false(라이브)면 디스크 저장 금지.
            return Task.FromResult(VisionImage.Invalid(cameraId));
        }

        public Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct)
        {
            // TODO(eBUS): TriggerMode=On/Source=LineN 구성 후 파이프라인에서 트리거 프레임 대기.
            return Task.FromResult(VisionImage.Invalid(cameraId));
        }

        // ── 4. 검사 ─────────────────────────────────────────────────────────
        // 액적 분석(OpenCvSharp)은 드라이버가 아니라 DropWatcher 디바이스 계층에서 수행한다.
        // 여기서는 인터페이스 계약 유지용(유효 이미지 없으면 실패).
        public Task<InspectionResult> InspectAsync(string cameraId, VisionImage image)
        {
            if (image == null || !image.IsValid)
                return Task.FromResult(InspectionResult.Fail(cameraId, "VIS_001", "Invalid image"));
            return Task.FromResult(InspectionResult.Pass(cameraId, 0));
        }

        public async Task<InspectionResult> CaptureAndInspectAsync(string cameraId)
        {
            var image = await CaptureAsync(cameraId);
            return await InspectAsync(cameraId, image);
        }

        // ── 5. 조명 제어 ────────────────────────────────────────────────────
        public void SetLight(string cameraId, bool on)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.IsLightOn = on;
            // TODO(eBUS): 조명 컨트롤러/디지털 출력 제어(카메라 GPIO 또는 별도 라이트 컨트롤러).
        }

        public void SetLightIntensity(string cameraId, int intensity)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.LightIntensity = Math.Clamp(intensity, 0, 255);
            // TODO(eBUS): 조명 세기 제어.
        }

        // ── 6. 카메라 파라미터 ──────────────────────────────────────────────
        public void SetExposure(string cameraId, double ms)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.ExposureMs = ms;
            // TODO(eBUS): GenICam ExposureTime(µs = ms*1000) 쓰기.
        }

        public void SetGain(string cameraId, double gain)
        {
            if (_statusMap.TryGetValue(cameraId, out var s)) s.Gain = gain;
            // TODO(eBUS): GenICam Gain 쓰기.
        }

        public double GetExposure(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s.ExposureMs : 0.0;

        public double GetGain(string cameraId) =>
            _statusMap.TryGetValue(cameraId, out var s) ? s.Gain : 0.0;
    }
}
