using System;
using System.Runtime.InteropServices;
using System.Threading;
using IJPSystem.Platform.Common.Utilities;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>
    /// 프로세스 <b>주소공간</b> 감시. 남은 양과 <b>가장 큰 빈 덩어리</b>를 주기적으로 기록한다.
    ///
    /// <para><b>왜 만들었나</b>: 2026-09-03 실장에서 오토런 도중 WPF 가 네이티브 할당에 실패해
    /// (<c>0x80070008</c> in <c>GlyphTypeface.GlyphMetrics</c>) 렌더 스레드가 죽었다
    /// (<c>UCEERR_RENDERTHREADFAILURE</c>). 그런데 로그에는 <b>터진 자리</b>만 남고
    /// "누가 다 썼는지"도 "언제부터 위험했는지"도 없었다. 터진 뒤에 알아봐야 늦다.</para>
    ///
    /// <para><b>왜 GC 힙이 아니라 주소공간인가</b>: 관리 힙이 부족했다면
    /// <see cref="OutOfMemoryException"/> 이 났을 것이다. 네이티브(DWrite·milcore)가 못 잡았다는
    /// 것은 <b>프로세스 주소공간</b>이 없다는 뜻이다. 그리고 총량보다 무서운 것이 조각남이다 —
    /// 총 500MB 가 남아도 연속된 빈 덩어리가 2MB 뿐이면 큰 할당은 실패한다. 그래서
    /// 총 여유와 <b>최대 연속 여유</b>를 같이 본다.</para>
    ///
    /// <para><b>왜 x86 에서 중요한가</b>: 32비트 프로세스의 천장은 2GB(LARGE_ADDRESS_AWARE 가
    /// 없으면) 또는 4GB 다. 이 앱은 ComiEcatSdk 가 32비트라 win-x86 로 나가고, DW 프레임
    /// 8.1MB·GVC 1.31MB·Meteor SDK·OpenCV 가 같은 천장을 나눠 쓴다.</para>
    ///
    /// <para>재는 일만 한다 — 아무것도 정리하지 않고 아무것도 막지 않는다.
    /// 판단은 사람이 로그를 보고 한다.</para>
    /// </summary>
    public static class MemoryWatchdog
    {
        /// <summary>재는 주기. 이 주기로는 <b>기록하지 않는다</b> — 임계 판정만 한다.</summary>
        private const int PollMs = 30_000;

        /// <summary>평상시 기록 주기. 30초마다 남기면 로그가 이것만으로 찬다.</summary>
        private static readonly TimeSpan RoutineLogInterval = TimeSpan.FromMinutes(5);

        /// <summary>최대 연속 여유가 이보다 작으면 경고. 큰 비트맵·폰트 캐시가 못 들어가기 시작하는 선.</summary>
        private const long WarnLargestFreeBytes = 128L * 1024 * 1024;

        /// <summary>총 여유가 이보다 작으면 경고.</summary>
        private const long WarnTotalFreeBytes = 384L * 1024 * 1024;

        /// <summary>회수를 시도한 뒤 이만큼도 안 돌아오면 관리 힙이 원인이 아니다(= 재시작밖에 없다).</summary>
        private const long ReliefMeaningfulBytes = 64L * 1024 * 1024;

        /// <summary>회수 재시도 간격. 압축 GC 는 화면이 멎는 작업이라 자주 하면 안 된다.</summary>
        private static readonly TimeSpan ReliefCooldown = TimeSpan.FromMinutes(10);

        private static Timer? _timer;
        private static DateTime _lastRoutineLog = DateTime.MinValue;
        private static DateTime _lastReliefAt = DateTime.MinValue;
        private static bool _warned;        // 경고는 상태가 회복될 때까지 한 번만
        private static bool _deferLogged;   // "운전 중이라 미룸" 도 한 번만

        /// <summary>
        /// 장비가 도는 중인지 묻는다. 여기서 <c>true</c> 면 회수를 <b>하지 않는다</b>.
        ///
        /// <para>압축 GC 는 모든 스레드를 세운다. 인쇄·정렬 도중에 걸면 화면이 수 초 멎고,
        /// 그 사이 축은 계속 움직인다 — 메모리를 아끼려다 더 나쁜 것을 만든다.</para>
        /// </summary>
        public static Func<bool>? IsMachineBusy { get; set; }

        /// <summary>감시를 시작한다. 두 번 불러도 하나만 돈다.</summary>
        public static void Start()
        {
            if (_timer != null) return;
            LoggerService.WriteToFile("INFO", $"[MEM] {Describe()}");
            _timer = new Timer(_ => Tick(), null, PollMs, PollMs);
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private static void Tick()
        {
            try
            {
                var s = Sample();
                if (s == null) return;

                bool tight = s.Value.LargestFree < WarnLargestFreeBytes
                          || s.Value.TotalFree   < WarnTotalFreeBytes;

                if (tight)
                {
                    // 경고는 상황이 나빠진 순간에만. 매 폴링마다 찍으면 진짜 신호가 묻힌다.
                    if (!_warned)
                    {
                        _warned = true;
                        LoggerService.WriteToFile("WARN",
                            $"[MEM] 주소공간이 부족합니다 — {Format(s.Value)} · " +
                            "이 상태가 이어지면 화면 렌더가 실패합니다(0x80070008).");
                    }
                    _lastRoutineLog = DateTime.Now;
                    TryRelief(s.Value);
                    return;
                }

                _warned = false;
                _deferLogged = false;
                if (DateTime.Now - _lastRoutineLog < RoutineLogInterval) return;
                _lastRoutineLog = DateTime.Now;
                LoggerService.WriteToFile("INFO", $"[MEM] {Format(s.Value)}");
            }
            catch
            {
                // 감시가 앱을 흔들면 안 된다 — 못 재면 조용히 넘어간다.
            }
        }

        /// <summary>
        /// 관리 힙을 압축해 주소공간을 돌려받아 본다. <b>그리고 얼마나 돌아왔는지 남긴다.</b>
        ///
        /// <para><b>왜 "압축"인가</b>: 큰 배열(GVC 1.31MB·DW 8.13MB 프레임)은 대형 객체 힙에
        /// 잡히는데, 기본 GC 는 대형 객체 힙을 <b>쓸기만 하고 모으지 않는다</b>. 그래서 총량은
        /// 남아도 연속된 덩어리가 없어지고, 그 상태에서 네이티브(DWrite·milcore)가 할당에
        /// 실패한다 — 이번 <c>0x80070008</c> 이 그 모습이다. 압축을 시켜야 조각이 합쳐진다.</para>
        ///
        /// <para><b>이게 진단이기도 하다</b>: 돌아오면 원인은 관리 힙이고 여기서 끝난다.
        /// 안 돌아오면 네이티브(카메라 SDK·Meteor·WPF 폰트 캐시)가 쥐고 있다는 뜻이고,
        /// 그건 <b>프로세스를 다시 띄우는 것 말고는 되돌릴 방법이 없다</b>. 어느 쪽인지
        /// 로그가 답하게 만든다 — 추측으로 고치면 엉뚱한 데를 파게 된다.</para>
        /// </summary>
        private static void TryRelief(Sampled before)
        {
            // 운전 중에는 절대 하지 않는다 — 압축 GC 는 모든 스레드를 세운다.
            if (IsMachineBusy?.Invoke() == true)
            {
                if (!_deferLogged)
                {
                    _deferLogged = true;
                    LoggerService.WriteToFile("INFO",
                        "[MEM] 운전 중이라 회수를 미룹니다 — 사이클이 끝나면 시도합니다.");
                }
                return;
            }

            if (DateTime.Now - _lastReliefAt < ReliefCooldown) return;
            _lastReliefAt = DateTime.Now;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            sw.Stop();

            var after = Sample();
            if (after == null) return;

            long gained = after.Value.TotalFree - before.TotalFree;
            LoggerService.WriteToFile("INFO",
                $"[MEM] 회수 시도 {sw.ElapsedMilliseconds}ms · 여유 {Mb(before.TotalFree)}→{Mb(after.Value.TotalFree)}MB " +
                $"(+{Mb(gained)}MB) · 최대연속 {Mb(before.LargestFree)}→{Mb(after.Value.LargestFree)}MB");

            if (gained < ReliefMeaningfulBytes)
            {
                // 관리 힙이 아니면 우리가 손쓸 수 있는 게 없다. 사람에게 넘긴다.
                LoggerService.WriteToFile("WARN",
                    "[MEM] 회수해도 돌아오지 않습니다 — 네이티브 메모리(카메라 SDK·Meteor·폰트 캐시)입니다. " +
                    "프로그램을 재시작해야 풀립니다. 인쇄를 마치는 대로 재시작하세요.");
            }
            else
            {
                _warned = false;   // 회복됐으니 다음에 다시 나빠지면 새로 경고한다
            }
        }

        /// <summary>비트수·주소공간 천장을 한 줄로. 기동 배너에서 쓴다.</summary>
        public static string Describe()
        {
            string bits = Environment.Is64BitProcess ? "x64" : "x86";
            string ceiling = Environment.Is64BitProcess
                ? "천장 없음(64bit)"
                : (IsLargeAddressAware() ? "천장 4GB (LARGE_ADDRESS_AWARE)" : "천장 2GB ★LARGE_ADDRESS_AWARE 없음");

            var s = Sample();
            return s == null
                ? $"{bits} · {ceiling}"
                : $"{bits} · {ceiling} · {Format(s.Value)}";
        }

        private static string Format(Sampled s) =>
            $"사용 {Mb(s.Committed)}MB · 여유 {Mb(s.TotalFree)}MB · 최대연속 {Mb(s.LargestFree)}MB · GC {Mb(GC.GetTotalMemory(false))}MB";

        private static long Mb(long bytes) => bytes / (1024 * 1024);

        private readonly struct Sampled
        {
            public Sampled(long committed, long totalFree, long largestFree)
            { Committed = committed; TotalFree = totalFree; LargestFree = largestFree; }

            public long Committed   { get; }
            public long TotalFree   { get; }
            public long LargestFree { get; }
        }

        /// <summary>
        /// 주소공간을 처음부터 끝까지 훑어 예약/여유를 합산한다.
        ///
        /// <para><c>Process.PrivateMemorySize64</c> 로는 조각남을 못 본다 — 총량만 나온다.
        /// 실패하는 이유는 대개 "총량이 없어서"가 아니라 "연속된 덩어리가 없어서"다.</para>
        /// </summary>
        private static Sampled? Sample()
        {
            try
            {
                GetSystemInfo(out var si);

                ulong addr = (ulong)(long)si.lpMinimumApplicationAddress;
                ulong max  = (ulong)(long)si.lpMaximumApplicationAddress;

                long committed = 0, totalFree = 0, largestFree = 0;
                int guard = 0;

                while (addr < max && guard++ < 2_000_000)
                {
                    long regionSize;
                    uint state;

                    if (IntPtr.Size == 8)
                    {
                        if (VirtualQuery((IntPtr)(long)addr, out MemoryBasicInformation64 mbi,
                                         (IntPtr)Marshal.SizeOf<MemoryBasicInformation64>()) == IntPtr.Zero) break;
                        regionSize = (long)mbi.RegionSize;
                        state      = mbi.State;
                    }
                    else
                    {
                        if (VirtualQuery((IntPtr)(long)addr, out MemoryBasicInformation32 mbi,
                                         (IntPtr)Marshal.SizeOf<MemoryBasicInformation32>()) == IntPtr.Zero) break;
                        regionSize = mbi.RegionSize;
                        state      = mbi.State;
                    }

                    if (regionSize <= 0) break;

                    if (state == MemFree)
                    {
                        totalFree += regionSize;
                        if (regionSize > largestFree) largestFree = regionSize;
                    }
                    else if (state == MemCommit)
                    {
                        committed += regionSize;
                    }

                    addr += (ulong)regionSize;
                }

                return new Sampled(committed, totalFree, largestFree);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>실행 중인 이미지의 PE 헤더에서 LARGE_ADDRESS_AWARE 를 읽는다.</summary>
        private static bool IsLargeAddressAware()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return false;

                using var fs = System.IO.File.OpenRead(exe);
                using var br = new System.IO.BinaryReader(fs);

                fs.Position = 0x3C;
                int peOffset = br.ReadInt32();
                if (peOffset <= 0 || peOffset + 24 > fs.Length) return false;

                fs.Position = peOffset;
                if (br.ReadUInt32() != 0x0000_4550u) return false;   // "PE\0\0"

                fs.Position = peOffset + 4 + 18;                     // COFF Characteristics
                return (br.ReadUInt16() & 0x0020) != 0;
            }
            catch
            {
                return false;
            }
        }

        // ── Win32 ────────────────────────────────────────────────────────────
        private const uint MemCommit = 0x1000;
        private const uint MemFree   = 0x10000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemInfo
        {
            public uint   dwOemId;
            public uint   dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint   dwNumberOfProcessors;
            public uint   dwProcessorType;
            public uint   dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation32
        {
            public uint BaseAddress;
            public uint AllocationBase;
            public uint AllocationProtect;
            public int  RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint  AllocationProtect;
            public uint  Alignment1;
            public ulong RegionSize;
            public uint  State;
            public uint  Protect;
            public uint  Type;
            public uint  Alignment2;
        }

        [DllImport("kernel32.dll")]
        private static extern void GetSystemInfo(out SystemInfo lpSystemInfo);

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation32 lpBuffer, IntPtr dwLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation64 lpBuffer, IntPtr dwLength);
    }
}
