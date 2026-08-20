using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Ttp.Meteor;   // PrinterInterfaceCLS(정적), eRET, TAppStatus

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>Meteor 헤드(PCC) 연결 상태 1회 조회 결과 — HMI 상태바/툴팁용 순수 데이터.</summary>
    public sealed class MeteorHeadStatus
    {
        /// <summary>PCC(하드웨어)가 실제로 부착됨(PccsAttached ≥ PccsRequired).</summary>
        public bool Connected { get; set; }
        /// <summary>엔진에 접근해 상태를 읽을 수 있었음(부착 여부와 별개).</summary>
        public bool Reachable { get; set; }
        public int PccsAttached { get; set; }
        public int PccsRequired { get; set; }
        public string PrinterState { get; set; } = "";
        public string HeadPower { get; set; } = "";
        /// <summary>상태바 툴팁 한 줄.</summary>
        public string Detail { get; set; } = "헤드(Meteor) 미연결";

        /// <summary>
        /// 실물이 아니라 만들어 낸 값인가. 화면은 이 값이 true 면 계속 표시해야 한다 —
        /// 한 번 뜨고 마는 배너로는 스크린샷만 보고 실물로 오해한다.
        /// </summary>
        public bool IsSimulated { get; set; }

        /// <summary>제품 감지·인쇄 카운터. 늘고 있으면 실제로 찍고 있다는 뜻이다.</summary>
        public int PdCount { get; set; }
        public int PrintCount { get; set; }

        /// <summary>인쇄 속도(엔진이 보고하는 값).</summary>
        public int PrintSpeed { get; set; }

        /// <summary>
        /// 엔진이 "보인다"고 하는 PCC 번호들. 부착(Attached)과 다르다 —
        /// DHCP 로 IP 를 못 받으면 여기가 비고, 그때 화면에는 하드웨어 고장처럼 보인다.
        /// </summary>
        public string PccsPresent { get; set; } = "";

        /// <summary>보조(Aux) 계통 카운터. 이더넷 하드웨어에서 쓴다.</summary>
        public int PdCount2 { get; set; }
        public int PrintCount2 { get; set; }

        /// <summary>데이터 경로 — 어디까지 갔나. 큐에 쌓여만 있으면 헤드로 안 나가고 있다는 뜻이다.</summary>
        public int PreloadDocsSent { get; set; }
        public int FifoDocsSent { get; set; }
        public int DocsQueuedLane1 { get; set; }
        public int DocsQueuedLane2 { get; set; }
        public int DocCount { get; set; }
        public int JobCount { get; set; }

        /// <summary>PCC 별 상세. 못 읽었으면 빈 목록.</summary>
        public IReadOnlyList<MeteorPccStatus> Pccs { get; set; } = Array.Empty<MeteorPccStatus>();

        /// <summary>HDC/헤드 별 상세. 못 읽었으면 빈 목록.</summary>
        public IReadOnlyList<MeteorHdcStatus> Hdcs { get; set; } = Array.Empty<MeteorHdcStatus>();
    }

    /// <summary>PCC 한 대의 상태. 단위가 분명한 값만 담는다 — 온도·전압은 스케일이
    /// 확인되기 전에 화면에 숫자로 띄우면 틀린 값을 믿게 된다.</summary>
    public sealed class MeteorPccStatus
    {
        public int  Number { get; set; }
        public uint StatusBits { get; set; }
        public uint StatusBits2 { get; set; }

        /// <summary>정상 가동 중에는 0 이어야 한다. 0이 아니면 <c>PccFaultDecoder</c> 로 푼다.</summary>
        public uint FaultRegister { get; set; }

        public int PdCount { get; set; }
        public int PrintCount { get; set; }
        public int EncoderCount { get; set; }
        public int AbsXCount { get; set; }

        /// <summary>USB/이더넷 전송 오류. self-clearing 이라 폴링할 때마다 봐야 놓치지 않는다.</summary>
        public bool DataTransferError { get; set; }

        /// <summary>헤드 전원 인가가 진행 중.</summary>
        public bool HeadPowerInProgress { get; set; }

        /// <summary>펌웨어·FPGA 버전. 엔진 로그의 "PCC1 Firmware version: 0x…" 과 같은 값이다.</summary>
        public uint FwVersion { get; set; }
        public uint FpgaVersion { get; set; }

        /// <summary>PCC 가 받은 IP. 비어 있으면 DHCP 로 주소를 못 받은 것이다.</summary>
        public string IpAddress { get; set; } = "";

        /// <summary>이 PCC 가 몰 수 있는 HDC 수.</summary>
        public int MaxHdcs { get; set; }

        public bool HasFault => FaultRegister != 0;
    }

    /// <summary>
    /// HDC/헤드 한 개의 상태.
    ///
    /// <para>온도·전압은 <b>일부러 담지 않았다</b> — 구조체가 정수인데 스케일이 매뉴얼에 없다.
    /// 51.0 인지 510 인지 모르는 값을 화면에 숫자로 띄우면 틀린 값을 믿게 된다.</para>
    /// </summary>
    public sealed class MeteorHdcStatus
    {
        public int PccNumber { get; set; }
        public int HeadNumber { get; set; }

        /// <summary>헤드 상태(eHeadState). 정상 가동은 ST_RUNNING.</summary>
        public string State { get; set; } = "";

        public uint StatusBits { get; set; }
        public uint StatusBits2 { get; set; }

        /// <summary>버퍼 사용률(%). 100 에 붙어 있으면 데이터가 안 빠지고 있다.</summary>
        public int PreloadDataUsedPercent { get; set; }
        public int FifoDataUsedPercent { get; set; }

        /// <summary>DDRAM 에 쌓인 DWORD. A/B 는 노즐 열 두 계통이다.</summary>
        public int DdramDwordsA { get; set; }
        public int DdramDwordsB { get; set; }

        /// <summary>HDC 로 실제로 나간 DWORD. 늘고 있으면 인쇄가 돌고 있다는 뜻이다.</summary>
        public int HeadDwordsA { get; set; }
        public int HeadDwordsB { get; set; }

        public int ImagesQueuedA { get; set; }
        public int DocsPrintedA { get; set; }
    }

    /// <summary>
    /// Meteor 헤드(PCC) 연결 상태를 <b>읽기 전용</b>으로 모니터링한다.
    /// 엔진을 시작하지 않고 <see cref="PrinterInterfaceCLS.PiOpenPrinter"/> 로 붙어(attach)
    /// <see cref="PrinterInterfaceCLS.PiGetPrnStatus"/> 의 PccsAttached 만 폴링한다.
    /// 발사·설정변경은 하지 않으므로 약액 없이 안전. 실제 발사 배선은 <see cref="MeteorSpit"/>.
    ///
    /// <para>안전장치: 네이티브 x86 DLL 이 없는 환경(개발PC)에서는 첫 호출에서 예외를 잡아
    /// 스스로 비활성화(<see cref="_unavailable"/>)하고 조용히 "미탑재"를 돌려준다.</para>
    ///
    /// <para>주의: PiOpenPrinter 는 프린터를 점유(claim)한다 — Meteor 는 한 프로세스만 소유 가능.
    /// 다른 앱(LabVIEW/MeteorConnect)이 이미 점유 중이면 RVAL_CLAIMED → "점유중"으로 표시하고 뺏지 않는다.</para>
    /// </summary>
    public sealed class MeteorStatusMonitor : IMeteorStatusSource
    {
        private readonly string _nativeDir;
        private readonly object _io = new();
        private bool _nativeDirSet;
        private bool _opened;
        private bool _unavailable;   // 네이티브 로드 실패 등 — 더 시도하지 않음

        /// <summary>PCC-E 한 대가 HDC 8개까지 몬다. 그 이상 도는 것은 의미가 없다.</summary>
        private const int MaxPccs = 8;

        /// <summary>PCC-E 한 대가 HDC 8개까지 몬다.</summary>
        private const int MaxHdcsPerPcc = 8;

        public MeteorStatusMonitor(
            string nativeDir = @"C:\Program Files\Meteor Inkjet\Meteor\Api\x86")
        {
            _nativeDir = nativeDir;
        }

        /// <summary>상태 1회 조회. 절대 예외를 던지지 않는다(항상 결과 반환).</summary>
        public MeteorHeadStatus Poll()
        {
            lock (_io)
            {
                var res = new MeteorHeadStatus();
                if (_unavailable) { res.Detail = "헤드(Meteor) 미탑재"; return res; }

                try
                {
                    EnsureNativeDir();

                    if (!_opened)
                    {
                        var ro = PrinterInterfaceCLS.PiOpenPrinter();
                        if (ro == eRET.RVAL_OK)
                        {
                            _opened = true;
                        }
                        else
                        {
                            // 엔진 미실행/점유중 — 뺏지 않고 사유만 표시
                            res.Detail = ro == eRET.RVAL_CLAIMED
                                ? "헤드 점유중(다른 앱)"
                                : "헤드(Meteor) 엔진 미실행 — DHCP 서버·엔진 확인";
                            return res;
                        }
                    }

                    if (PrinterInterfaceCLS.PiGetPrnStatus(out TAppStatus st) == eRET.RVAL_OK)
                    {
                        res.Reachable    = true;
                        res.PccsAttached = st.PccsAttached;
                        res.PccsRequired = st.PccsRequired;
                        res.PrinterState = st.PrinterState.ToString();
                        res.HeadPower    = st.HeadPowerState.ToString();
                        res.Connected    = st.PccsRequired > 0 && st.PccsAttached >= st.PccsRequired;
                        res.PdCount     = st.PdCount;
                        res.PrintCount  = st.PrintCount;
                        res.PrintSpeed  = st.PrintSpeed;
                        res.PccsPresent = PresentText(st.PCCsPresent);
                        res.PdCount2    = st.PdCount2;
                        res.PrintCount2 = st.PrintCount2;

                        res.PreloadDocsSent = st.PreloadPath.DocsSent;
                        res.FifoDocsSent    = st.FifoPath.DocsSent;
                        res.DocsQueuedLane1 = st.DocsQueuedLane1;
                        res.DocsQueuedLane2 = st.DocsQueuedLane2;
                        res.DocCount        = st.DocCount;
                        res.JobCount        = st.JobCount;

                        res.Pccs = ReadPccs(st);
                        res.Hdcs = ReadHdcs(res.Pccs);

                        res.Detail = res.Connected
                            ? $"PCC {st.PccsAttached}/{st.PccsRequired} · {st.PrinterState} · HeadPower {st.HeadPowerState}"
                            : $"PCC 미부착 {st.PccsAttached}/{st.PccsRequired} · {st.PrinterState}";
                    }
                    else
                    {
                        _opened = false;   // 다음 폴에서 재연결 시도
                        res.Detail = "헤드 상태 읽기 실패 — 재연결 시도";
                    }
                }
                catch (DllNotFoundException)
                {
                    _unavailable = true;
                    res.Detail = "헤드(Meteor) 미탑재 — 네이티브 DLL 없음";
                }
                catch (BadImageFormatException)
                {
                    _unavailable = true;
                    res.Detail = "헤드(Meteor) 비트수 불일치(x86 필요)";
                }
                catch (Exception ex)
                {
                    res.Detail = "헤드(Meteor) 오류: " + ex.Message;
                }
                return res;
            }
        }

        /// <summary>
        /// PCC 별 상태를 읽는다. 실패해도 프린터 상태 자체는 이미 읽었으므로 조용히 건너뛴다
        /// — 여기서 예외가 나가면 상태바가 통째로 "미연결"이 된다.
        /// </summary>
        private static IReadOnlyList<MeteorPccStatus> ReadPccs(TAppStatus st)
        {
            int count = Math.Min(MaxPccs, Math.Max(st.PccsAttached, st.PccsRequired));
            if (count <= 0) return Array.Empty<MeteorPccStatus>();

            var list = new List<MeteorPccStatus>(count);
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    if (PrinterInterfaceCLS.PiGetPccStatus(i, out TAppPccStatus p) != eRET.RVAL_OK) continue;

                    list.Add(new MeteorPccStatus
                    {
                        Number              = i,
                        StatusBits          = unchecked((uint)p.bmStatusBits),
                        StatusBits2         = unchecked((uint)p.bmStatusBits2),
                        FaultRegister       = unchecked((uint)p.FaultRegister),
                        PdCount             = p.PdCount,
                        PrintCount          = p.PrintCount,
                        EncoderCount        = p.EncoderCount,
                        AbsXCount           = p.AbsXCount,
                        DataTransferError   = (p.bmStatusBits2 & Bmps.BMPS2_DATA_XFER_ERROR) != 0,
                        HeadPowerInProgress = (p.bmStatusBits2 & Bmps.BMPS2_HEAD_POWER_IN_PROGRESS) != 0,
                        FwVersion           = unchecked((uint)p.FwVersion),
                        FpgaVersion         = unchecked((uint)p.FpgaVersion),
                        IpAddress           = IpText(p.IpV4Addr),
                        MaxHdcs             = p.MaxHdcs,
                    });
                }
                catch { break; }   // 이 SDK 버전에 없는 호출 — 더 시도해도 같다
            }
            return list;
        }

        /// <summary>
        /// PCC 마다 HDC/헤드 상태를 읽는다. PCC 를 못 읽었으면 여기도 건너뛴다
        /// — 붙지도 않은 헤드를 도는 것은 시간 낭비다.
        /// </summary>
        private static IReadOnlyList<MeteorHdcStatus> ReadHdcs(IReadOnlyList<MeteorPccStatus> pccs)
        {
            if (pccs.Count == 0) return Array.Empty<MeteorHdcStatus>();

            var list = new List<MeteorHdcStatus>();
            foreach (var pcc in pccs)
            {
                int heads = pcc.MaxHdcs > 0 ? Math.Min(MaxHdcsPerPcc, pcc.MaxHdcs) : MaxHdcsPerPcc;

                for (int hd = 1; hd <= heads; hd++)
                {
                    try
                    {
                        if (PrinterInterfaceCLS.PiGetHeadStatus(pcc.Number, hd, out TAppHeadStatus h) != eRET.RVAL_OK)
                            continue;

                        list.Add(new MeteorHdcStatus
                        {
                            PccNumber              = pcc.Number,
                            HeadNumber             = hd,
                            State                  = h.HeadState.ToString(),
                            StatusBits             = unchecked((uint)h.bmStatusBits),
                            StatusBits2            = unchecked((uint)h.bmStatusBits2),
                            PreloadDataUsedPercent = h.PreloadDataUsed,
                            FifoDataUsedPercent    = h.FifoDataUsed,
                            DdramDwordsA           = unchecked((int)h.DdramDwordsA),
                            DdramDwordsB           = unchecked((int)h.DdramDwordsB),
                            HeadDwordsA            = unchecked((int)h.HeadDwordsA),
                            HeadDwordsB            = unchecked((int)h.HeadDwordsB),
                            ImagesQueuedA          = h.ImagesQueuedA,
                            DocsPrintedA           = h.DocsPrintedA,
                        });
                    }
                    catch { return list; }   // 이 SDK 버전에 없는 호출 — 더 돌아도 같다
                }
            }
            return list;
        }

        /// <summary>PCC 가 받은 IPv4 주소. 0 이면 주소를 못 받은 것이라 빈 문자열로 둔다.</summary>
        private static string IpText(int addr)
        {
            if (addr == 0) return "";

            uint v = unchecked((uint)addr);
            return $"{v >> 24 & 0xFF}.{v >> 16 & 0xFF}.{v >> 8 & 0xFF}.{v & 0xFF}";
        }

        /// <summary>엔진이 보고 있는 PCC 번호 목록. 비어 있으면 DHCP·네트워크부터 볼 것.</summary>
        private static string PresentText(uint[]? present)
        {
            if (present == null || present.Length == 0) return "";

            var seen = new List<string>();
            for (int i = 0; i < present.Length; i++)
                if (present[i] != 0) seen.Add((i + 1).ToString());

            return string.Join(", ", seen);
        }

        // 네이티브 PrinterInterface.dll/PrintEngine.dll 이 Api\x86 에서 로드되도록 검색경로 추가(1회).
        // 폴더가 없으면(개발PC) 그냥 진행 → 첫 Pi* 호출에서 DllNotFound → 자동 비활성화.
        private void EnsureNativeDir()
        {
            if (_nativeDirSet) return;
            if (Directory.Exists(_nativeDir))
                SetDllDirectory(_nativeDir);
            _nativeDirSet = true;
        }

        /// <summary>실물에는 고를 상황이 없다. 있는 그대로만 보여 준다.</summary>
        public IReadOnlyList<string> Scenarios => Array.Empty<string>();

        /// <summary>실물에서는 설정해도 아무 일도 일어나지 않는다.</summary>
        public string Scenario { get => ""; set { } }

        public void Dispose()
        {
            lock (_io)
            {
                if (!_opened) return;
                try { PrinterInterfaceCLS.PiClosePrinter(); } catch { /* 종료 경로 — 무시 */ }
                _opened = false;
            }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string? lpPathName);
    }
}
