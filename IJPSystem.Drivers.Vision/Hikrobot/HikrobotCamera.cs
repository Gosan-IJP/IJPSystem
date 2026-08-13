using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Models.Vision;
using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace IJPSystem.Drivers.Vision.Hikrobot
{
    /// <summary>
    /// Hikrobot(MVS) 카메라 한 대의 세션 — 장치/스트림/파라미터를 묶어 관리한다.
    ///
    /// <para>MvCameraControl 타입을 직접 다루는 <b>유일한</b> 클래스다
    /// (<see cref="HikrobotSdk"/> 의 지연 로딩 설명 참고).</para>
    ///
    /// <para>eBUS 쪽 <c>EbusCamera</c> 와 역할·구조를 일부러 맞췄다 — 두 벤더 드라이버가
    /// 다른 모양이면 나중에 한쪽만 고치고 다른 쪽을 잊는다.</para>
    /// </summary>
    internal sealed class HikrobotCamera : IDisposable
    {
        // GenICam 노드 후보. 하이크로봇은 최신 이름을 쓰지만 구형 펌웨어 대비 폴백을 둔다.
        private static readonly string[] ExposureNodeCandidates = { "ExposureTime", "ExposureTimeAbs" };
        private static readonly string[] GainNodeCandidates     = { "Gain", "GainRaw" };

        private readonly object _sync = new();

        private IDevice? _device;
        private string?  _exposureNode;
        private string?  _gainNode;

        public string CameraId { get; }
        public bool   IsOpen   { get; private set; }
        public int    Width    { get; private set; }
        public int    Height   { get; private set; }
        public string Detail   { get; private set; } = "";

        public HikrobotCamera(string cameraId) => CameraId = cameraId;

        // ── SDK 전역 ────────────────────────────────────────────────────────
        private static bool _sdkInitialized;
        private static readonly object _sdkSync = new();

        /// <summary>SDK 를 1회 초기화한다. 열거·연결 전에 반드시 선행돼야 한다.</summary>
        public static void InitializeSdk()
        {
            lock (_sdkSync)
            {
                if (_sdkInitialized) return;
                int rc = SDKSystem.Initialize();
                if (rc != 0) throw new InvalidOperationException($"SDKSystem.Initialize 실패 (rc=0x{rc:X})");
                _sdkInitialized = true;
                LoggerService.WriteToFile("INFO", $"[Hikrobot Vision] MVS SDK {SDKSystem.GetSDKVersion()} 초기화");
            }
        }

        public static void FinalizeSdk()
        {
            lock (_sdkSync)
            {
                if (!_sdkInitialized) return;
                try { SDKSystem.Finalize(); } catch { }
                _sdkInitialized = false;
            }
        }

        /// <summary>GigE 카메라를 열거한다.</summary>
        public static List<IDeviceInfo> Enumerate()
        {
            int rc = DeviceEnumerator.EnumDevices(DeviceTLayerType.MvGigEDevice, out List<IDeviceInfo> list);
            if (rc != 0)
            {
                LoggerService.WriteToFile("WARN", $"[Hikrobot Vision] EnumDevices 실패 (rc=0x{rc:X})");
                return new List<IDeviceInfo>();
            }
            return list ?? new List<IDeviceInfo>();
        }

        // ── 검색 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 설정과 일치하는 장치를 찾는다. 우선순위: MAC → 시리얼 → IP → UserDefinedName → 모델명.
        /// <para>eBUS 쪽과 동일한 규칙 — IP 는 링크로컬/DHCP 면 전원마다 바뀌므로 뒤로 둔다.
        /// 여러 대가 걸리거나 못 찾으면 null(임의로 아무 카메라나 잡지 않는다).</para>
        /// </summary>
        public static IDeviceInfo? Match(IReadOnlyList<IDeviceInfo> found, CameraDeviceInfo cfg)
        {
            IDeviceInfo? Pick(Func<IDeviceInfo, bool> pred)
            {
                var hits = found.Where(pred).ToList();
                return hits.Count == 1 ? hits[0] : null;
            }

            if (!string.IsNullOrWhiteSpace(cfg.MacAddress))
            {
                string want = NormalizeMac(cfg.MacAddress);
                var hit = Pick(d => MacOf(d) == want);
                if (hit != null) return hit;
            }

            if (!string.IsNullOrWhiteSpace(cfg.SerialNumber))
            {
                var hit = Pick(d => string.Equals(d.SerialNumber?.Trim(), cfg.SerialNumber.Trim(),
                                                  StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            if (!string.IsNullOrWhiteSpace(cfg.IpAddress))
            {
                var hit = Pick(d => IpOf(d) == cfg.IpAddress.Trim());
                if (hit != null) return hit;
            }

            if (!string.IsNullOrWhiteSpace(cfg.Name))
            {
                var hit = Pick(d => string.Equals(d.UserDefinedName?.Trim(), cfg.Name.Trim(),
                                                  StringComparison.OrdinalIgnoreCase))
                       ?? Pick(d => d.ModelName?.IndexOf(cfg.Name.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }

            return null;
        }

        /// <summary>MAC 을 구분자 없는 소문자 12자리로(예: <c>34bd2056235b</c>).</summary>
        private static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            var sb = new StringBuilder(12);
            foreach (char c in mac)
                if (Uri.IsHexDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>MVS 는 MAC 을 상위 2바이트/하위 4바이트 정수로 나눠 준다.</summary>
        private static string MacOf(IDeviceInfo d) =>
            d is IGigEDeviceInfo g
                ? $"{(g.MacAddrHigh & 0xFFFF):x4}{g.MacAddrLow:x8}"
                : "";

        private static string MacDisplay(IDeviceInfo d)
        {
            string m = MacOf(d);
            return m.Length == 12
                ? string.Join(":", Enumerable.Range(0, 6).Select(i => m.Substring(i * 2, 2)))
                : "";
        }

        /// <summary>CurrentIp 는 상위 바이트가 첫 옥텟인 32비트 정수다.</summary>
        private static string IpOf(IDeviceInfo d) =>
            d is IGigEDeviceInfo g
                ? $"{(g.CurrentIp >> 24) & 0xFF}.{(g.CurrentIp >> 16) & 0xFF}.{(g.CurrentIp >> 8) & 0xFF}.{g.CurrentIp & 0xFF}"
                : "";

        /// <summary>발견된 장치 요약(실장에서 config 에 옮겨 적기 위한 로그).</summary>
        public static string Describe(IDeviceInfo d) =>
            $"{d.ManufacturerName} {d.ModelName} · IP={IpOf(d)} · MAC={MacDisplay(d)} · SN={d.SerialNumber} " +
            $"· name='{d.UserDefinedName}' · FW={d.DeviceVersion}";

        // ── 열기 / 닫기 ─────────────────────────────────────────────────────
        public bool Open(IDeviceInfo info, CameraDeviceInfo cfg)
        {
            lock (_sync)
            {
                try
                {
                    _device = DeviceFactory.CreateDevice(info);
                    Check(_device.Open(), "Open");

                    // GigE 최적 패킷 크기. 실패해도 기본값으로 스트리밍은 되므로 경고만 남긴다.
                    TryOptimizePacketSize();

                    TrySetMono8();
                    ReadFrameSize();

                    Check(_device.StreamGrabber.StartGrabbing(), "StartGrabbing");

                    if (cfg.DefaultExposureMs > 0) SetExposureMs(cfg, cfg.DefaultExposureMs);
                    if (cfg.DefaultGain      > 0) SetGain(cfg, cfg.DefaultGain);

                    IsOpen = true;
                    Detail = Describe(info);
                    LoggerService.WriteToFile("INFO",
                        $"[Hikrobot Vision] {CameraId} 연결 — {Detail} · {Width}x{Height}");
                    return true;
                }
                catch (Exception ex)
                {
                    // GigE 는 제어권이 단독이다. MVS 나 다른 앱이 잡고 있으면 여기로 온다.
                    LoggerService.WriteToFile("ERROR",
                        $"[Hikrobot Vision] {CameraId} 연결 실패: {ex.GetType().Name}: {ex.Message}" +
                        " — 다른 프로그램(MVS 등)이 카메라를 점유 중인지 확인하세요.");
                    CloseCore();
                    return false;
                }
            }
        }

        public void Dispose() { lock (_sync) CloseCore(); }

        private void CloseCore()
        {
            // 역순 해제. 한 단계가 실패해도 나머지는 계속 정리해야 다음 연결이 가능하다.
            try { _device?.StreamGrabber?.StopGrabbing(); } catch { }
            try { _device?.Close(); }                      catch { }
            try { (_device as IDisposable)?.Dispose(); }   catch { }
            _device = null;
            IsOpen = false;
        }

        private static void Check(int rc, string what)
        {
            if (rc != 0) throw new InvalidOperationException($"{what} 실패 (rc=0x{rc:X})");
        }

        // ── 획득 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 다음 프레임을 받아 Mono8 버퍼로 변환한다. 실패(타임아웃 등)면 null —
        /// 라이브 폴링에서 매 프레임 예외가 나면 안 된다.
        /// </summary>
        public byte[]? Grab(uint timeoutMs)
        {
            lock (_sync)
            {
                if (!IsOpen || _device == null) return null;

                IFrameOut? frame = null;
                try
                {
                    int rc = _device.StreamGrabber.GetImageBuffer(timeoutMs, out frame);
                    if (rc != 0 || frame?.Image == null) return null;

                    var img = frame.Image;
                    int w = (int)img.Width, h = (int)img.Height;
                    if (w <= 0 || h <= 0) return null;

                    Width = w; Height = h;

                    var src = img.PixelData;
                    if (src == null || src.Length == 0) return null;

                    int pixels = w * h;
                    var mono = new byte[pixels];

                    // 포맷 열거값에 의존하지 않고 실제 바이트/픽셀로 판단한다
                    // (기종·펌웨어마다 PixelType 이름이 달라 분기가 쉽게 어긋난다).
                    int bytesPerPixel = src.Length / Math.Max(1, pixels);
                    if (bytesPerPixel <= 1)
                    {
                        Array.Copy(src, mono, Math.Min(src.Length, pixels));
                    }
                    else
                    {
                        // Mono10/12/16 → 상위 8비트만 취해 Mono8 로 낮춘다(분석은 Mono8 기준).
                        for (int i = 0; i < pixels && (i * 2 + 1) < src.Length; i++)
                            mono[i] = src[i * 2 + 1];
                    }
                    return mono;
                }
                catch (Exception ex)
                {
                    LoggerService.WriteToFile("WARN", $"[Hikrobot Vision] {CameraId} 프레임 수신 예외: {ex.Message}");
                    return null;
                }
                finally
                {
                    // 버퍼를 돌려주지 않으면 몇 프레임 뒤 큐가 말라 획득이 멈춘다.
                    if (frame != null) { try { _device?.StreamGrabber?.FreeImageBuffer(frame); } catch { } }
                }
            }
        }

        // ── GenICam 파라미터 ────────────────────────────────────────────────
        public void SetExposureMs(CameraDeviceInfo cfg, double ms)
        {
            string? node = ResolveNode(ref _exposureNode, cfg.ExposureNode, ExposureNodeCandidates);
            if (node != null) WriteNumeric(node, ms * 1000.0);   // GenICam ExposureTime 단위는 µs
        }

        public void SetGain(CameraDeviceInfo cfg, double gain)
        {
            string? node = ResolveNode(ref _gainNode, cfg.GainNode, GainNodeCandidates);
            if (node != null) WriteNumeric(node, gain);
        }

        public double GetExposureMs(CameraDeviceInfo cfg)
        {
            string? node = ResolveNode(ref _exposureNode, cfg.ExposureNode, ExposureNodeCandidates);
            return node == null ? 0.0 : ReadNumeric(node) / 1000.0;
        }

        public double GetGain(CameraDeviceInfo cfg)
        {
            string? node = ResolveNode(ref _gainNode, cfg.GainNode, GainNodeCandidates);
            return node == null ? 0.0 : ReadNumeric(node);
        }

        /// <summary>
        /// 하드웨어 트리거 모드 On/Off. 켜면 카메라는 <b>트리거가 올 때만</b> 프레임을 내보낸다.
        /// 끄면 자유 실행(라이브뷰)으로 돌아간다.
        ///
        /// <para><see cref="CameraDeviceInfo.TriggerSource"/> 가 비어 있으면 아무것도 하지 않는다 —
        /// Line 번호를 모르는 채 TriggerMode 만 켜면 오지 않는 트리거를 기다리며 화면이 멎는다.</para>
        ///
        /// <para>Selector 를 먼저 써야 이어지는 Mode/Source/Activation 이 그 selector 에 적용된다.</para>
        /// </summary>
        public void SetHardwareTrigger(CameraDeviceInfo cfg, bool on)
        {
            if (_device == null) return;

            if (string.IsNullOrWhiteSpace(cfg.TriggerSource))
            {
                if (on)
                    LoggerService.WriteToFile("WARN",
                        $"[Hikrobot Vision] {CameraId} 하드웨어 트리거 미설정 — VisionConfig 의 TriggerSource(예: Line0)를 " +
                        "채우기 전까지 자유 실행으로 촬영합니다(스트로브와 동기되지 않음).");
                return;
            }

            WriteEnum("TriggerSelector", Or(cfg.TriggerSelector, "FrameStart"));

            if (!on)
            {
                WriteEnum("TriggerMode", "Off");
                LoggerService.WriteToFile("INFO", $"[Hikrobot Vision] {CameraId} 하드웨어 트리거 해제 — 자유 실행");
                return;
            }

            WriteEnum("TriggerSource",     cfg.TriggerSource.Trim());
            WriteEnum("TriggerActivation", Or(cfg.TriggerActivation, "RisingEdge"));
            WriteEnum("TriggerMode",       "On");   // 마지막에 켠다 — 설정 도중의 프레임 유실 방지

            LoggerService.WriteToFile("INFO",
                $"[Hikrobot Vision] {CameraId} 하드웨어 트리거 ON — {Or(cfg.TriggerSelector, "FrameStart")} / " +
                $"{cfg.TriggerSource.Trim()} / {Or(cfg.TriggerActivation, "RisingEdge")}");
        }

        private static string Or(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        /// <summary>쓸 수 있는 노드명을 1회만 찾아 캐시한다. config 지정이 있으면 그것만 쓴다.</summary>
        private string? ResolveNode(ref string? cached, string configured, string[] candidates)
        {
            if (cached != null) return cached.Length == 0 ? null : cached;
            if (_device == null) return null;

            if (!string.IsNullOrWhiteSpace(configured))
            {
                cached = configured.Trim();
                return cached;
            }

            foreach (var name in candidates)
            {
                if (_device.Parameters.GetFloatValue(name, out _) != 0 &&
                    _device.Parameters.GetIntValue(name, out _)   != 0) continue;
                cached = name;
                LoggerService.WriteToFile("INFO", $"[Hikrobot Vision] {CameraId} GenICam 노드 확정: {name}");
                return cached;
            }

            cached = "";
            LoggerService.WriteToFile("WARN",
                $"[Hikrobot Vision] {CameraId} 노드를 찾지 못했습니다({string.Join("/", candidates)}) — " +
                "VisionConfig 의 ExposureNode/GainNode 로 지정하세요.");
            return null;
        }

        private void WriteNumeric(string node, double value)
        {
            if (_device!.Parameters.SetFloatValue(node, (float)value) == 0) return;
            if (_device.Parameters.SetIntValue(node, (long)Math.Round(value)) == 0) return;
            LoggerService.WriteToFile("WARN", $"[Hikrobot Vision] {CameraId} {node} 쓰기 실패(값={value:F1})");
        }

        private double ReadNumeric(string node)
        {
            if (_device!.Parameters.GetFloatValue(node, out IFloatValue f) == 0) return f.CurValue;
            if (_device.Parameters.GetIntValue(node, out IIntValue i)      == 0) return i.CurValue;
            return 0.0;
        }

        /// <summary>
        /// 열거형 노드 쓰기. 실패해도 예외를 올리지 않고 로그만 남긴다 — 기종이 지원하지 않는
        /// 항목 하나 때문에 트리거 설정 전체가 무너지면 안 된다. 다만 <b>조용히 넘기지는 않는다</b>:
        /// 트리거가 안 걸릴 때 어느 노드에서 어긋났는지가 유일한 단서다.
        /// (MVS 는 예외 대신 상태코드를 돌려준다 — 0 이 성공)
        /// </summary>
        private void WriteEnum(string node, string value)
        {
            int rc;
            try { rc = _device!.Parameters.SetEnumValueByString(node, value); }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN",
                    $"[Hikrobot Vision] {CameraId} {node}='{value}' 쓰기 예외: {ex.Message}");
                return;
            }

            if (rc != 0)
                LoggerService.WriteToFile("WARN",
                    $"[Hikrobot Vision] {CameraId} {node}='{value}' 쓰기 실패(0x{rc:X8})");
        }

        /// <summary>분석 파이프라인이 Mono8 기준이라 가능하면 Mono8 로 맞춘다(안 되면 Grab 에서 다운시프트).</summary>
        private void TrySetMono8()
        {
            try { _device!.Parameters.SetEnumValueByString("PixelFormat", "Mono8"); } catch { }
        }

        /// <summary>GigE 최적 패킷 크기 적용. 점보프레임이 켜져 있으면 대역폭·CPU 여유가 커진다.</summary>
        private void TryOptimizePacketSize()
        {
            try
            {
                if (_device is IGigEDevice gige &&
                    gige.GetOptimalPacketSize(out int size) == 0 && size > 0)
                {
                    _device.Parameters.SetIntValue("GevSCPSPacketSize", size);
                    LoggerService.WriteToFile("INFO", $"[Hikrobot Vision] {CameraId} 패킷사이즈 {size}B 적용");
                }
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN",
                    $"[Hikrobot Vision] {CameraId} 패킷사이즈 최적화 생략(기본값으로 진행): {ex.Message}");
            }
        }

        private void ReadFrameSize()
        {
            try
            {
                if (_device!.Parameters.GetIntValue("Width",  out IIntValue w) == 0) Width  = (int)w.CurValue;
                if (_device!.Parameters.GetIntValue("Height", out IIntValue h) == 0) Height = (int)h.CurValue;
            }
            catch { Width = 0; Height = 0; }
        }
    }
}
