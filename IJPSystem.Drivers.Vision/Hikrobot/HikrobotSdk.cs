using IJPSystem.Platform.Common.Utilities;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace IJPSystem.Drivers.Vision.Hikrobot
{
    /// <summary>
    /// Hikrobot MVS SDK 로딩 담당.
    ///
    /// <para><b>eBUS 와 다른 점</b> — MVS 설치관리자는 PATH 에 런타임 폴더를 넣지 않는다(2026-08-04 확인).
    /// 그래서 관리 어셈블리(<c>MvCameraControl.Net.dll</c>)뿐 아니라 그것이 P/Invoke 하는
    /// 네이티브 <c>MvCameraControl.dll</c> 까지 <b>둘 다</b> 직접 해석해줘야 한다.
    /// 네이티브는 <see cref="NativeLibrary.SetDllImportResolver"/> 로 건다.</para>
    ///
    /// <para><b>검증(2026-08-04)</b> — MvCameraControl.Net 4.8.1 이 .NET 8 x86(8.0.29)에서
    /// 로드되고 <c>SDKSystem.Initialize()</c> / <c>EnumDevices()</c> 네이티브 호출까지 정상 동작함을
    /// 실행으로 확인했다.</para>
    ///
    /// <para><b>지연 로딩</b> — MvCameraControl 타입을 직접 다루는 코드는 <see cref="HikrobotCamera"/>
    /// 안에만 둔다. 이 클래스의 <see cref="IsInstalled"/> 는 그 타입을 언급하지 않으므로,
    /// MVS 미설치 PC 에서 어셈블리 로드 자체가 일어나지 않는다.</para>
    /// </summary>
    internal static class HikrobotSdk
    {
        private const string ManagedAssemblyName = "MvCameraControl.Net";

        /// <summary>관리 어셈블리 후보 경로. 앱이 32비트이므로 win32 를 먼저 본다.</summary>
        private static readonly string[] ManagedDirCandidates =
        {
            @"MVS\Development\DotNet\win32\netstandard2.0",
            @"MVS\Development\DotNet\AnyCpu\netstandard2.0",
            @"MVS\Development\DotNet\win32\net40",
            @"MVS\Development\DotNet\AnyCpu\net40",
        };

        /// <summary>네이티브 런타임 경로(x86). Common Files 아래에 설치된다.</summary>
        private static readonly string NativeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            "MVS", "Runtime", "Win32_i86");

        private static readonly object _sync = new();
        private static bool _loaded;

        /// <summary>관리 어셈블리 파일 경로(없으면 null). MvCameraControl 타입을 건드리지 않는다.</summary>
        public static string? ManagedAssemblyPath
        {
            get
            {
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                foreach (var rel in ManagedDirCandidates)
                {
                    string p = Path.Combine(pf86, rel, ManagedAssemblyName + ".dll");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        /// <summary>MVS SDK 설치 여부(관리 어셈블리 + 네이티브 런타임 둘 다 있어야 한다).</summary>
        public static bool IsInstalled =>
            ManagedAssemblyPath != null && File.Exists(Path.Combine(NativeDir, "MvCameraControl.dll"));

        public static string DiagnosticPaths =>
            $"관리={ManagedAssemblyPath ?? "(없음)"} / 네이티브={NativeDir}";

        /// <summary>
        /// 관리 어셈블리와 네이티브 DLL 해석기를 등록한다(1회).
        /// MvCameraControl 타입을 처음 쓰기 <b>전에</b> 호출해야 한다.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void EnsureLoaded()
        {
            lock (_sync)
            {
                if (_loaded) return;

                string? managed = ManagedAssemblyPath;
                if (managed == null)
                    throw new FileNotFoundException($"MVS SDK 관리 어셈블리를 찾지 못했습니다. {DiagnosticPaths}");

                // ① 관리 어셈블리 — 출력 폴더에 복사하지 않으므로 설치 경로에서 직접 읽는다.
                AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                    name.Name == ManagedAssemblyName ? ctx.LoadFromAssemblyPath(managed) : null;

                var asm = Assembly.Load(new AssemblyName(ManagedAssemblyName));

                // ② 네이티브 — MVS 는 PATH 를 건드리지 않아 기본 탐색으로는 못 찾는다.
                //    비트수를 우리가 골라주므로 x64 런타임을 잘못 잡는 사고도 막힌다.
                NativeLibrary.SetDllImportResolver(asm, (libName, _, _) =>
                {
                    string file = libName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        ? libName : libName + ".dll";
                    string path = Path.Combine(NativeDir, file);
                    return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
                });

                _loaded = true;
                LoggerService.WriteToFile("INFO",
                    $"[Hikrobot Vision] MVS SDK 로드 — {asm.GetName().Version} @ {managed}");
            }
        }
    }
}
