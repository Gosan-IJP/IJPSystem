using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Vision
{
    /// <summary>
    /// 카메라마다 서로 다른 드라이버를 붙일 수 있게 해주는 다중화 드라이버.
    ///
    /// <para><b>왜 필요한가</b> — 9호기는 벤더가 섞여 있다. 드랍와처(JAI GOX-8105M-PGE)는
    /// eBUS 로 정상 동작하지만, 글라스뷰(하이크로봇 MV-CU013-80GM)는 eBUS for JAI 라이선스
    /// 대상이 아니라 화면 중앙에 평가판 워터마크가 찍힌다(2026-08-04 실장 확인).
    /// 전역 <c>DriverMode.Vision</c> 하나로는 이 조합을 표현할 수 없다.</para>
    ///
    /// <para>카메라별 드라이버는 <see cref="CameraDeviceInfo.Driver"/> 로 지정하고, 비어 있으면
    /// 전역 설정을 따른다. 같은 드라이버를 쓰는 카메라들은 <b>인스턴스 하나를 공유</b>한다 —
    /// eBUS·IMAQdx 모두 내부에서 카메라 목록을 통째로 들고 열거하므로, 카메라마다 인스턴스를
    /// 만들면 같은 네트워크를 중복 열거하고 서로의 장치를 점유하려다 충돌한다.</para>
    ///
    /// <para>드라이버가 한 종류뿐이면 이 클래스를 쓰지 않는다(App 이 내부 드라이버를 그대로 반환).
    /// 0호기처럼 단일 벤더 장비의 동작 경로를 바꾸지 않기 위함이다.</para>
    /// </summary>
    public class CompositeVisionDriver : IVisionDriver
    {
        private readonly string _defaultKey;
        private readonly Func<string, IVisionDriver> _factory;

        // 드라이버 키(ebus/imaqdx/…) → 인스턴스. 같은 키의 카메라는 인스턴스를 공유한다.
        private readonly Dictionary<string, IVisionDriver> _byKey = new(StringComparer.OrdinalIgnoreCase);
        // CameraId → 담당 드라이버. 카메라 단위 호출은 전부 이걸로 라우팅한다.
        private readonly Dictionary<string, IVisionDriver> _byCamera = new();
        // 설정 순서 유지용 — GetAllStatus 가 VisionConfig 순서를 그대로 돌려주게 한다.
        private readonly List<string> _cameraOrder = new();

        private string _imageSavePath = @"C:\Logs\Vision";

        /// <param name="defaultDriverKey">카메라에 Driver 지정이 없을 때 쓸 전역 키(소문자).</param>
        /// <param name="factory">드라이버 키로 인스턴스를 만드는 함수(App 이 제공).</param>
        public CompositeVisionDriver(string defaultDriverKey, Func<string, IVisionDriver> factory)
        {
            _defaultKey = string.IsNullOrWhiteSpace(defaultDriverKey) ? "virtual" : defaultDriverKey;
            _factory    = factory;
        }

        /// <summary>카메라가 쓸 드라이버 키. 지정이 없으면 전역 키.</summary>
        public static string ResolveKey(CameraDeviceInfo cfg, string defaultKey) =>
            string.IsNullOrWhiteSpace(cfg.Driver) ? defaultKey : cfg.Driver.Trim().ToLowerInvariant();

        public string ImageSavePath
        {
            get => _imageSavePath;
            set
            {
                _imageSavePath = value;
                foreach (var d in _byKey.Values) TrySetImageSavePath(d, value);
            }
        }

        // IVisionDriver 에 ImageSavePath 가 없어 구현체별 속성으로 반영한다(있으면).
        private static void TrySetImageSavePath(IVisionDriver driver, string path)
        {
            var prop = driver.GetType().GetProperty("ImageSavePath");
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                try { prop.SetValue(driver, path); } catch { }
        }

        // ── 1. 연결 / 초기화 ────────────────────────────────────────────────
        public void Initialize(List<CameraDeviceInfo> configs)
        {
            if (configs == null) return;

            _byKey.Clear();
            _byCamera.Clear();
            _cameraOrder.Clear();

            foreach (var group in configs.Where(c => !string.IsNullOrEmpty(c.CameraId))
                                         .GroupBy(c => ResolveKey(c, _defaultKey)))
            {
                IVisionDriver driver;
                try
                {
                    driver = _factory(group.Key);
                }
                catch (Exception ex)
                {
                    // 드라이버 하나가 못 서도 나머지 카메라는 살려야 한다.
                    LoggerService.WriteToFile("ERROR",
                        $"[VISION] '{group.Key}' 드라이버 생성 실패 — 해당 카메라 미연결: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                TrySetImageSavePath(driver, _imageSavePath);
                _byKey[group.Key] = driver;

                var list = group.ToList();
                foreach (var c in list) _byCamera[c.CameraId] = driver;

                LoggerService.WriteToFile("INFO",
                    $"[VISION] '{group.Key}' 드라이버 → {string.Join(", ", list.Select(c => c.CameraId))}");

                try { driver.Initialize(list); }
                catch (Exception ex)
                {
                    LoggerService.WriteToFile("ERROR",
                        $"[VISION] '{group.Key}' 초기화 실패: {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var c in configs) if (!string.IsNullOrEmpty(c.CameraId)) _cameraOrder.Add(c.CameraId);
        }

        public bool Connect()
        {
            foreach (var d in _byKey.Values)
            {
                try { d.Connect(); }
                catch (Exception ex)
                {
                    LoggerService.WriteToFile("WARN", $"[VISION] 하위 드라이버 Connect 실패: {ex.Message}");
                }
            }
            return true;   // graceful — 개별 연결 여부는 IsConnected / 카메라 상태로 판단
        }

        public void Disconnect()
        {
            foreach (var d in _byKey.Values)
            {
                try { d.Disconnect(); } catch { }
            }
        }

        /// <summary>하나라도 연결돼 있으면 true. 카메라별 실제 상태는 <see cref="GetAllStatus"/> 로 본다.</summary>
        public bool IsConnected => _byKey.Values.Any(d => d.IsConnected);

        // ── 2. 상태 조회 ────────────────────────────────────────────────────
        public CameraStatus GetStatus(string cameraId) =>
            Route(cameraId)?.GetStatus(cameraId) ?? new CameraStatus { CameraId = cameraId };

        public List<CameraStatus> GetAllStatus()
        {
            // 설정 순서를 유지한다 — 하위 드라이버별로 모아 내면 화면 목록 순서가 뒤바뀐다.
            var merged = new Dictionary<string, CameraStatus>();
            foreach (var d in _byKey.Values)
                foreach (var s in d.GetAllStatus())
                    merged[s.CameraId] = s;

            var ordered = _cameraOrder.Where(merged.ContainsKey).Select(id => merged[id]).ToList();
            // 설정에 없던 카메라(드라이버가 추가로 보고한 것)는 뒤에 붙인다.
            ordered.AddRange(merged.Values.Where(s => !_cameraOrder.Contains(s.CameraId)));
            return ordered;
        }

        private IVisionDriver? Route(string cameraId) =>
            !string.IsNullOrEmpty(cameraId) && _byCamera.TryGetValue(cameraId, out var d) ? d : null;

        // ── 3. 촬영 ─────────────────────────────────────────────────────────
        public Task<VisionImage> CaptureAsync(string cameraId, bool saveToDisk = true, int timeoutMs = 0) =>
            Route(cameraId)?.CaptureAsync(cameraId, saveToDisk, timeoutMs)
            ?? Task.FromResult(VisionImage.Invalid(cameraId));

        public Task<VisionImage> WaitForHardwareTriggerAsync(string cameraId, CancellationToken ct) =>
            Route(cameraId)?.WaitForHardwareTriggerAsync(cameraId, ct)
            ?? Task.FromResult(VisionImage.Invalid(cameraId));

        public void SetHardwareTrigger(string cameraId, bool on) =>
            Route(cameraId)?.SetHardwareTrigger(cameraId, on);

        // ── 4. 검사 ─────────────────────────────────────────────────────────
        public Task<InspectionResult> InspectAsync(string cameraId, VisionImage image) =>
            Route(cameraId)?.InspectAsync(cameraId, image)
            ?? Task.FromResult(InspectionResult.Fail(cameraId, "VIS_000", "드라이버 없음"));

        public Task<InspectionResult> CaptureAndInspectAsync(string cameraId) =>
            Route(cameraId)?.CaptureAndInspectAsync(cameraId)
            ?? Task.FromResult(InspectionResult.Fail(cameraId, "VIS_000", "드라이버 없음"));

        // ── 5. 조명 제어 ────────────────────────────────────────────────────
        public void SetLight(string cameraId, bool on) => Route(cameraId)?.SetLight(cameraId, on);

        public void SetLightIntensity(string cameraId, int intensity) =>
            Route(cameraId)?.SetLightIntensity(cameraId, intensity);

        // ── 6. 카메라 파라미터 ──────────────────────────────────────────────
        public void SetExposure(string cameraId, double ms) => Route(cameraId)?.SetExposure(cameraId, ms);
        public void SetGain(string cameraId, double gain)   => Route(cameraId)?.SetGain(cameraId, gain);

        public double GetExposure(string cameraId) => Route(cameraId)?.GetExposure(cameraId) ?? 0.0;
        public double GetGain(string cameraId)     => Route(cameraId)?.GetGain(cameraId)     ?? 0.0;
    }
}
