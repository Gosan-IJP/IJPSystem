using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace IJPSystem.Drivers.Vision.Ebus
{
    /// <summary>
    /// Pleora eBUS SDK 어셈블리 로딩 담당.
    ///
    /// <para><b>왜 이 클래스가 필요한가</b> — 프로젝트는 <c>PvDotNet.dll</c> 을 <c>Private=false</c>
    /// (출력 폴더에 복사 안 함)로 참조한다. 런타임에는 <b>설치된 SDK 폴더에서 직접</b> 로드해
    /// 관리 어셈블리와 네이티브 DLL(PvBase/PvDevice/PvStream/GenICam…)의 버전이 항상 일치하게 한다.
    /// 우리 설치 파일이 Pleora 바이너리를 재배포하지 않아도 되는 이점도 있다.</para>
    ///
    /// <para><b>검증(2026-08-03)</b> — PvDotNet 은 C++/CLI 혼합 어셈블리(ILONLY=false, 네이티브
    /// 엔트리포인트)지만, <b>.NET 8 x86 self-contained 런타임(8.0.29)에서 로드·인스턴스화·
    /// 네이티브 Find() 까지 정상 동작</b>함을 실행으로 확인했다. 별도 x64 브리지 프로세스 불필요.</para>
    ///
    /// <para><b>지연 로딩 주의</b> — CLR 은 메서드를 JIT 할 때 그 안에서 쓰이는 타입을 로드한다.
    /// 따라서 PvDotNet 타입을 직접 다루는 코드는 반드시 <see cref="EbusCamera"/> 쪽에 두고,
    /// 이 클래스와 드라이버의 진입부는 PvDotNet 타입을 언급하지 않는다.
    /// 그래야 eBUS 미설치 PC(0호기·개발 PC)에서 Vision=Imaqdx/Virtual 로 띄울 때
    /// 어셈블리 로드 자체가 일어나지 않는다.</para>
    /// </summary>
    internal static class EbusSdk
    {
        /// <summary>
        /// x86 런타임 설치 경로. 앱이 32비트이므로 (x86) 쪽을 쓴다.
        /// 표준 경로를 먼저 보고, 없으면 PATH 에서 Pleora 항목을 찾는다
        /// (eBUS 설치관리자가 PATH 에 x86/x64 폴더를 모두 추가하므로 x86 쪽이 걸린다).
        /// </summary>
        public static readonly string InstallDir = FindInstallDir();

        private static string FindInstallDir()
        {
            string standard = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                "Pleora", "eBUS SDK");
            if (File.Exists(Path.Combine(standard, "PvDotNet.dll"))) return standard;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.IndexOf("Pleora", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { if (File.Exists(Path.Combine(dir, "PvDotNet.dll"))) return dir; } catch { }
            }
            return standard;   // 미설치 — IsInstalled 가 false 를 돌려주고 드라이버가 미연결로 처리
        }

        private static bool _resolverRegistered;
        private static readonly object _sync = new();

        /// <summary>SDK 가 설치되어 있는지(= PvDotNet.dll 존재). PvDotNet 타입을 건드리지 않는다.</summary>
        public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "PvDotNet.dll"));

        /// <summary>
        /// 설치 폴더에서 Pv*.dll 을 찾아주는 해석기를 등록한다(1회).
        /// PvDotNet 을 처음 쓰기 <b>전에</b> 호출해야 한다.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void EnsureResolver()
        {
            lock (_sync)
            {
                if (_resolverRegistered) return;
                _resolverRegistered = true;

                AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                {
                    if (name.Name is not string n ||
                        !n.StartsWith("Pv", StringComparison.OrdinalIgnoreCase))
                        return null;

                    string path = Path.Combine(InstallDir, n + ".dll");
                    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
                };
            }
        }
    }
}
