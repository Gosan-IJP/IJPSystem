using System;
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
    public sealed class MeteorStatusMonitor : IDisposable
    {
        private readonly string _nativeDir;
        private readonly object _io = new();
        private bool _nativeDirSet;
        private bool _opened;
        private bool _unavailable;   // 네이티브 로드 실패 등 — 더 시도하지 않음

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

        // 네이티브 PrinterInterface.dll/PrintEngine.dll 이 Api\x86 에서 로드되도록 검색경로 추가(1회).
        // 폴더가 없으면(개발PC) 그냥 진행 → 첫 Pi* 호출에서 DllNotFound → 자동 비활성화.
        private void EnsureNativeDir()
        {
            if (_nativeDirSet) return;
            if (Directory.Exists(_nativeDir))
                SetDllDirectory(_nativeDir);
            _nativeDirSet = true;
        }

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
