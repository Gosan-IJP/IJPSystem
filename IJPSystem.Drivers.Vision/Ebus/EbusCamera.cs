using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Models.Vision;
using PvDotNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace IJPSystem.Drivers.Vision.Ebus
{
    /// <summary>
    /// eBUS(GigE Vision) 카메라 한 대의 세션 — 장치/스트림/파이프라인/GenICam 파라미터를 묶어 관리한다.
    ///
    /// <para>PvDotNet 타입을 직접 다루는 <b>유일한</b> 클래스다. eBUS 미설치 PC 에서 어셈블리 로드가
    /// 일어나지 않도록, 드라이버는 이 타입을 필드로도 들지 않고 <c>object</c> 로 들고 다닌다
    /// (<see cref="EbusSdk"/> 주석의 지연 로딩 설명 참고).</para>
    /// </summary>
    internal sealed class EbusCamera : IDisposable
    {
        // 노출/게인 GenICam 노드 후보 — 기종마다 이름이 다르다.
        // JAI/최신 GenICam: ExposureTime(µs, float) / Gain(float)
        // 구형·일부 하이크로봇: ExposureTimeAbs / GainRaw(정수)
        private static readonly string[] ExposureNodeCandidates = { "ExposureTime", "ExposureTimeAbs", "ExposureTimeRaw" };
        private static readonly string[] GainNodeCandidates     = { "Gain", "GainAbs", "GainRaw" };

        private const uint PipelineBufferCount = 16;

        private readonly object _sync = new();

        private PvDevice?            _device;
        private PvStream?            _stream;
        private PvPipeline?          _pipeline;
        private PvGenParameterArray? _params;

        private string? _exposureNode;
        private string? _gainNode;

        public string CameraId { get; }
        public bool   IsOpen   { get; private set; }
        public int    Width    { get; private set; }
        public int    Height   { get; private set; }

        /// <summary>마지막으로 받은 프레임에 워터마크가 있었는지 — eBUS 라이선스 미보유 카메라 판별.</summary>
        public bool HasWatermark { get; private set; }

        /// <summary>화면/로그용 한 줄 요약(벤더·모델·연결 상태 등).</summary>
        public string Detail { get; private set; } = "";

        public EbusCamera(string cameraId) => CameraId = cameraId;

        // ── 검색 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 설정과 일치하는 장치를 찾는다. 우선순위: MAC → 시리얼 → IP → UserDefinedName → 모델명.
        /// <para>IP 를 뒤로 둔 이유: DHCP/링크로컬(169.254.x.x)이면 전원마다 바뀌어 신뢰할 수 없다.</para>
        /// <para>여러 대가 걸리거나 하나도 못 찾으면 null — <b>임의로 아무 카메라나 잡지 않는다</b>
        /// (2대 구성에서 오결선되면 글라스뷰 영상이 드랍와처로 들어오는 식의 조용한 오동작이 된다).</para>
        /// </summary>
        public static PvDeviceInfo? Match(IReadOnlyList<PvDeviceInfo> found, CameraDeviceInfo cfg)
        {
            PvDeviceInfo? Pick(Func<PvDeviceInfo, bool> pred)
            {
                var hits = found.Where(pred).ToList();
                return hits.Count == 1 ? hits[0] : null;
            }

            if (!string.IsNullOrWhiteSpace(cfg.MacAddress))
            {
                string want = NormalizeMac(cfg.MacAddress);
                var hit = Pick(d => d is PvDeviceInfoGEV g && NormalizeMac(g.MACAddress) == want);
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
                var hit = Pick(d => d is PvDeviceInfoGEV g &&
                                    string.Equals(g.IPAddress?.Trim(), cfg.IpAddress.Trim(), StringComparison.Ordinal));
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

        /// <summary>구분자·대소문자를 없앤 MAC(예: <c>000cdf011db4</c>).</summary>
        private static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            var sb = new StringBuilder(12);
            foreach (char c in mac)
                if (Uri.IsHexDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>발견된 장치 한 대를 사람이 읽을 수 있게 요약(실장에서 config 에 옮겨 적기 위한 로그).</summary>
        public static string Describe(PvDeviceInfo d)
        {
            string ip = "", mac = "", ipCfg = "";
            if (d is PvDeviceInfoGEV g)
            {
                ip    = g.IPAddress ?? "";
                mac   = g.MACAddress ?? "";
                ipCfg = g.IPConfigCurrentString ?? "";
            }
            string lic = d.IsLicenseValid ? "" : $" ★라이선스무효({d.LicenseMessage})";
            return $"{d.VendorName} {d.ModelName} · IP={ip}({ipCfg}) · MAC={mac} · SN={d.SerialNumber} " +
                   $"· name='{d.UserDefinedName}'{lic}";
        }

        // ── 열기 / 닫기 ─────────────────────────────────────────────────────
        public bool Open(PvDeviceInfo info, CameraDeviceInfo cfg)
        {
            lock (_sync)
            {
                try
                {
                    _device = PvDevice.CreateAndConnect(info);

                    // GigE: 점보프레임까지 포함해 링크가 감당하는 최대 패킷으로 협상.
                    // 실패해도 기본 패킷(1500)으로 스트리밍은 되므로 치명적이지 않다.
                    if (_device is PvDeviceGEV gev)
                    {
                        try { gev.NegotiatePacketSize(); }
                        catch (Exception ex)
                        {
                            LoggerService.WriteToFile("WARN",
                                $"[eBUS Vision] {CameraId} 패킷사이즈 협상 실패(기본값으로 진행): {ex.Message}");
                        }
                    }

                    _stream = PvStream.CreateAndOpen(info);

                    // 카메라에게 "이 PC 의 이 포트로 보내라"고 알려준다. 이걸 빠뜨리면 연결은 되는데
                    // 프레임이 한 장도 안 들어온다(가장 흔한 무증상 실패).
                    if (_device is PvDeviceGEV g2 && _stream is PvStreamGEV s2)
                        g2.SetStreamDestination(s2.LocalIPAddress, s2.LocalPort);

                    _params = _device.Parameters;
                    TrySetMono8();
                    ReadFrameSize();

                    _pipeline = new PvPipeline(_stream)
                    {
                        BufferSize  = _device.PayloadSize,
                        BufferCount = PipelineBufferCount,
                    };
                    _pipeline.Start();

                    _device.StreamEnable();
                    Execute("AcquisitionStart");

                    // 설정된 초기 노출/게인 적용(노드명 탐색도 여기서 1회 끝난다)
                    if (cfg.DefaultExposureMs > 0) SetExposureMs(cfg, cfg.DefaultExposureMs);
                    if (cfg.DefaultGain      > 0) SetGain(cfg, cfg.DefaultGain);

                    IsOpen = true;
                    Detail = Describe(info);
                    LoggerService.WriteToFile("INFO",
                        $"[eBUS Vision] {CameraId} 연결 — {Detail} · {Width}x{Height} · payload={_device.PayloadSize}B");
                    return true;
                }
                catch (Exception ex)
                {
                    // GigE Vision 은 제어권이 단독이다. 다른 프로세스가 잡고 있으면 "Access denied" 가 온다.
                    // 원문만 남기면 현장에서 네트워크 문제로 오인하기 쉬워 조치를 함께 적는다.
                    bool inUse = ex.Message.IndexOf("Access denied", StringComparison.OrdinalIgnoreCase) >= 0
                              || ex.Message.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0;
                    string hint = inUse
                        // MVS 도 같이 적는다 — 이 PC 의 MVS 는 Hikrobot 뿐 아니라 JAI(DWC)까지
                        // 목록에 띄우고 열 수 있다(2026-09-03 11호기 확인). GVC 용 도구라 방심하기 쉽다.
                        ? " — 다른 프로그램이 카메라를 점유 중입니다. eBUS Player 나 MVS 클라이언트를 닫거나 " +
                          "Disconnect 하세요(비정상 종료 직후라면 카메라 하트비트가 끊길 때까지 최대 30초 기다렸다 재시도)."
                        : "";
                    LoggerService.WriteToFile("ERROR",
                        $"[eBUS Vision] {CameraId} 연결 실패: {ex.GetType().Name}: {ex.Message}{hint}");
                    CloseCore();
                    return false;
                }
            }
        }

        public void Dispose() { lock (_sync) CloseCore(); }

        private void CloseCore()
        {
            // 역순 해제. 어느 단계에서 실패해도 나머지는 계속 정리해야 다음 연결이 가능하다.
            try { if (_params != null) Execute("AcquisitionStop"); } catch { }
            try { _device?.StreamDisable(); }                        catch { }
            try { _pipeline?.Stop(); }                               catch { }
            try { _pipeline?.Dispose(); }                            catch { }
            try { _stream?.Close(); }                                catch { }
            try { _stream?.Dispose(); }                              catch { }
            try { _device?.Disconnect(); }                           catch { }
            try { _device?.Dispose(); }                              catch { }

            _pipeline = null; _stream = null; _device = null; _params = null;
            IsOpen = false;
        }

        // ── 획득 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 파이프라인에서 다음 프레임을 받아 Mono8 버퍼로 변환한다.
        /// 실패(타임아웃·불완전 프레임)면 null — 예외를 올리지 않는다(라이브 폴링에서 매 프레임 터지면 안 됨).
        /// </summary>
        public unsafe byte[]? Grab(uint timeoutMs)
        {
            lock (_sync)
            {
                if (!IsOpen || _pipeline == null) return null;

                PvBuffer buffer = null!;
                PvResult opResult = default;   // PvResult 는 값 형식(struct)
                PvResult res;
                try { res = _pipeline.RetrieveNextBuffer(ref buffer, timeoutMs, ref opResult); }
                catch (Exception ex)
                {
                    LoggerService.WriteToFile("WARN", $"[eBUS Vision] {CameraId} 프레임 수신 예외: {ex.Message}");
                    return null;
                }

                if (!res.IsOK || buffer == null) return null;

                try
                {
                    // opResult = 그 버퍼 자체의 수신 품질(패킷 손실 등). OK 가 아니면 깨진 프레임이다.
                    if (!opResult.IsOK) return null;

                    var img = buffer.Image;
                    if (img == null) return null;

                    int w = (int)img.Width, h = (int)img.Height;
                    if (w <= 0 || h <= 0) return null;

                    Width = w; Height = h;
                    HasWatermark = img.HasWatermark;

                    var mono = new byte[w * h];
                    byte* src = img.DataPointer;
                    if (src == null) return null;

                    uint bits = img.BitsPerPixel;
                    if (bits <= 8)
                    {
                        Marshal.Copy((IntPtr)src, mono, 0, mono.Length);
                    }
                    else
                    {
                        // Mono10/12/16 → 상위 8비트만 취해 Mono8 로 낮춘다(분석 파이프라인이 Mono8 기준).
                        for (int i = 0; i < mono.Length; i++) mono[i] = src[i * 2 + 1];
                    }
                    return mono;
                }
                finally
                {
                    try { _pipeline.ReleaseBuffer(buffer); } catch { }
                }
            }
        }

        // ── GenICam 파라미터 ────────────────────────────────────────────────
        public void SetExposureMs(CameraDeviceInfo cfg, double ms)
        {
            string? node = ResolveNode(ref _exposureNode, cfg.ExposureNode, ExposureNodeCandidates);
            if (node == null) return;
            WriteNumeric(node, ms * 1000.0);   // GenICam ExposureTime 단위는 µs
        }

        public void SetGain(CameraDeviceInfo cfg, double gain)
        {
            string? node = ResolveNode(ref _gainNode, cfg.GainNode, GainNodeCandidates);
            if (node == null) return;
            WriteNumeric(node, gain);
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
        /// 하드웨어 트리거 모드 On/Off. 켜면 카메라는 <b>트리거가 올 때만</b> 프레임을 내보낸다 —
        /// 이래야 스트로브가 얼린 그 순간이 찍힌다. 끄면 자유 실행(라이브뷰)으로 돌아간다.
        ///
        /// <para><see cref="CameraDeviceInfo.TriggerSource"/> 가 비어 있으면 <b>아무것도 하지 않는다.</b>
        /// Line 번호를 모르는 채로 TriggerMode 만 On 하면 오지 않는 트리거를 기다리며
        /// 화면이 통째로 멎는다 — 미설정은 자유 실행으로 두는 편이 안전하다.</para>
        ///
        /// <para>쓰기 순서가 중요하다: Selector 를 먼저 정해야 이어지는 Mode/Source/Activation 이
        /// 그 selector 에 적용된다. GenICam 에서 이 셋은 selector 로 인덱싱되는 값이다.</para>
        /// </summary>
        public void SetHardwareTrigger(CameraDeviceInfo cfg, bool on)
        {
            if (_params == null) return;

            if (string.IsNullOrWhiteSpace(cfg.TriggerSource))
            {
                if (on)
                    LoggerService.WriteToFile("WARN",
                        $"[eBUS Vision] {CameraId} 하드웨어 트리거 미설정 — VisionConfig 의 TriggerSource(예: Line0)를 " +
                        "채우기 전까지 자유 실행으로 촬영합니다(스트로브와 동기되지 않음).");
                return;
            }

            WriteEnum("TriggerSelector", Or(cfg.TriggerSelector, "FrameStart"));

            if (!on)
            {
                WriteEnum("TriggerMode", "Off");
                LoggerService.WriteToFile("INFO", $"[eBUS Vision] {CameraId} 하드웨어 트리거 해제 — 자유 실행");
                return;
            }

            WriteEnum("TriggerSource",     cfg.TriggerSource.Trim());
            WriteEnum("TriggerActivation", Or(cfg.TriggerActivation, "RisingEdge"));
            WriteEnum("TriggerMode",       "On");   // 마지막에 켠다 — 설정 도중의 프레임 유실 방지

            LoggerService.WriteToFile("INFO",
                $"[eBUS Vision] {CameraId} 하드웨어 트리거 ON — {Or(cfg.TriggerSelector, "FrameStart")} / " +
                $"{cfg.TriggerSource.Trim()} / {Or(cfg.TriggerActivation, "RisingEdge")}");
        }

        private static string Or(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        /// <summary>
        /// 쓸 수 있는 노드명을 한 번만 찾아 캐시한다. config 에 강제 지정이 있으면 그것만 쓴다
        /// (틀린 이름을 넣었을 때 조용히 다른 노드로 넘어가면 원인 파악이 어려워진다).
        /// </summary>
        private string? ResolveNode(ref string? cached, string configured, string[] candidates)
        {
            if (cached != null) return cached.Length == 0 ? null : cached;
            if (_params == null) return null;

            if (!string.IsNullOrWhiteSpace(configured))
            {
                cached = configured.Trim();
                return cached;
            }

            foreach (var name in candidates)
            {
                if (!Exists(name)) continue;
                cached = name;
                LoggerService.WriteToFile("INFO", $"[eBUS Vision] {CameraId} GenICam 노드 확정: {name}");
                return cached;
            }

            cached = "";   // 없음 — 다음 호출에서 다시 뒤지지 않도록 기록
            LoggerService.WriteToFile("WARN",
                $"[eBUS Vision] {CameraId} 노드를 찾지 못했습니다({string.Join("/", candidates)}) — " +
                "VisionConfig 의 ExposureNode/GainNode 로 지정하세요.");
            return null;
        }

        private bool Exists(string node)
        {
            try { return _params!.GetFloatValue(node) >= double.MinValue; } catch { }
            try { _ = _params!.GetIntegerValue(node); return true; }        catch { }
            return false;
        }

        private void WriteNumeric(string node, double value)
        {
            try { _params!.SetFloatValue(node, value); return; } catch { }
            try { _params!.SetIntegerValue(node, (long)Math.Round(value)); return; } catch { }
            LoggerService.WriteToFile("WARN", $"[eBUS Vision] {CameraId} {node} 쓰기 실패(값={value:F1})");
        }

        private double ReadNumeric(string node)
        {
            try { return _params!.GetFloatValue(node); }   catch { }
            try { return _params!.GetIntegerValue(node); } catch { }
            return 0.0;
        }

        /// <summary>
        /// 열거형 노드 쓰기. 실패해도 예외를 올리지 않고 로그만 남긴다 —
        /// 기종이 지원하지 않는 항목(예: TriggerActivation 이 고정인 카메라) 하나 때문에
        /// 트리거 설정 전체가 무너지면 안 된다. 다만 <b>조용히 넘기지는 않는다</b>:
        /// 트리거가 안 걸릴 때 어느 노드에서 어긋났는지가 유일한 단서다.
        /// </summary>
        private void WriteEnum(string node, string value)
        {
            try { _params!.SetEnumValue(node, value); }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN",
                    $"[eBUS Vision] {CameraId} {node}='{value}' 쓰기 실패: {ex.Message}");
            }
        }

        private void Execute(string command)
        {
            try { _params?.ExecuteCommand(command); }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN", $"[eBUS Vision] {CameraId} {command} 실패: {ex.Message}");
            }
        }

        /// <summary>분석 파이프라인이 Mono8 기준이라 가능하면 Mono8 로 맞춘다(안 되면 Grab 에서 다운시프트).</summary>
        private void TrySetMono8()
        {
            try { _params!.SetEnumValue("PixelFormat", "Mono8"); }
            catch { /* 지원 안 하는 기종 — Grab 에서 상위 8비트로 변환한다 */ }
        }

        private void ReadFrameSize()
        {
            try
            {
                Width  = (int)_params!.GetIntegerValue("Width");
                Height = (int)_params!.GetIntegerValue("Height");
            }
            catch { Width = 0; Height = 0; }
        }
    }
}
