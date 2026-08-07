using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>
    /// <b>지금 실행 중인 것이 어느 빌드인가</b>를 한눈에 답하기 위한 정보.
    ///
    /// <para>
    /// 어셈블리 버전(1.0.0.0)은 빌드마다 바뀌지 않아 "새 DLL 이 실제로 적용됐는지"를 구분하지 못한다.
    /// 실장에서 DLL 을 복사했는데 예전 화면이 그대로 뜨는 일이 반복됐고(2026-08-07), 그때
    /// <b>복사가 안 먹은 것인지 / 다른 폴더를 고친 것인지 / 앱이 다른 파일을 읽는 것인지</b>를
    /// 가릴 방법이 없었다. 파일의 수정시각은 복사한 사람이 탐색기에서 본 값과 바로 대조되므로,
    /// 이것을 창 제목과 부팅 로그에 남겨 추측 없이 확인할 수 있게 한다.
    /// </para>
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>진단에 의미가 있는 자체 어셈블리들 — 벤더/프레임워크 DLL 은 제외.</summary>
        private static readonly string[] Watched =
        {
            "IJPSystem.Platform.HMI",
            "IJPSystem.Platform.Infrastructure",
            "IJPSystem.Platform.Application",
            "IJPSystem.Platform.Domain",
            "IJPSystem.Drivers.Vision",
            "IJPSystem.Drivers.Motion",
            "IJPSystem.Drivers.IO",
        };

        /// <summary>
        /// 창 제목용 짧은 표기 — 컴파일 때 어셈블리 안에 박힌 빌드 시각.
        /// <para>
        /// 파일 수정시각을 쓰지 않는 이유: 복사하면 새 시각이 찍혀 <b>내용은 옛날인데 방금 빌드한
        /// 것처럼</b> 보인다. 실제로 7/22 산출물을 복사해 놓고 표시만 당일로 나와, 실장에서
        /// 예전 버전이 도는 원인을 며칠 못 찾았다(2026-08-07).
        /// 값은 csproj 의 SourceRevisionId 가 InformationalVersion 뒤(+) 에 붙여 준다.
        /// </para>
        /// </summary>
        public static string Stamp
        {
            get
            {
                string? info = typeof(BuildInfo).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                int plus = info?.IndexOf('+') ?? -1;
                if (plus >= 0 && plus + 1 < info!.Length) return "build " + info[(plus + 1)..];

                // SourceRevisionId 가 없는 빌드 — 파일 시각으로라도 표시하되 출처를 밝힌다.
                var (_, time, _) = Describe(typeof(BuildInfo).Assembly);
                return time is null ? "build ?" : $"build {time.Value:MMdd-HHmm}(파일시각)";
            }
        }

        /// <summary>
        /// 설치 폴더의 자체 어셈블리들이 <b>서로 다른 빌드</b>면 그 요약, 아니면 null.
        /// <para>
        /// DLL 을 손으로 복사할 때 일부만 바꾸면 어긋난 조합이 되고, 그 조합은 실행 중에
        /// MethodNotFound 로 죽는다 — 그것도 해당 코드에 <b>들어갈 때</b> 죽어서, 복사 직후가
        /// 아니라 한참 뒤 엉뚱한 화면에서 터진다(실장 2026-08-07: 웨이브폼 화면 진입 시).
        /// 부팅 때 미리 알려주면 그 자리에서 다시 복사하면 된다.
        /// </para>
        /// 로드 여부와 무관하게 <b>파일에서</b> 읽는다 — 아직 안 쓰인 어셈블리도 포함해야
        /// 나중에 터질 조합까지 잡힌다.
        /// </summary>
        public static string? MismatchSummary()
        {
            var found = new Dictionary<string, string>();
            foreach (string name in Watched)
            {
                string path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                if (!File.Exists(path)) continue;
                string rev = RevisionOf(path);
                if (rev != null!) found[name] = rev;
            }

            var revs = found.Values.Distinct().ToList();
            if (found.Count == 0 || revs.Count <= 1) return null;

            var worst = found.GroupBy(kv => kv.Value).OrderBy(g => g.Count()).First();
            return $"어셈블리 빌드가 섞여 있습니다 ({revs.Count}종) — " +
                   $"{string.Join(", ", worst.Select(kv => $"{kv.Key}={kv.Value}"))} 만 다릅니다. " +
                   "DLL 을 한 세트로 다시 복사하세요(Apply-Hotfix.ps1 권장).";
        }

        /// <summary>파일에서 읽은 빌드 시각. 없으면 "?".</summary>
        private static string RevisionOf(string path)
        {
            try
            {
                string? info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
                int plus = info?.IndexOf('+') ?? -1;
                return plus >= 0 && plus + 1 < info!.Length ? info[(plus + 1)..] : "?";
            }
            catch { return "?"; }
        }

        /// <summary>부팅 로그용 — 감시 대상 어셈블리의 경로·수정시각·크기 한 줄씩.</summary>
        public static IEnumerable<string> DescribeLoaded()
        {
            yield return $"[BOOT] 실행 폴더: {AppContext.BaseDirectory}";

            foreach (string name in Watched)
            {
                Assembly? asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

                if (asm == null)
                {
                    // 아직 안 쓰인 어셈블리는 로드 전이라 없을 수 있다 — 파일만이라도 확인한다.
                    string probe = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                    yield return File.Exists(probe)
                        ? $"[BOOT]   {name}: (미로드) {Stat(probe)}"
                        : $"[BOOT]   {name}: 파일 없음";
                    continue;
                }

                var (path, time, size) = Describe(asm);
                // 빌드 시각(어셈블리 내부)과 파일 시각을 함께 남긴다. 둘이 다르면 복사된 파일이고,
                // 어셈블리끼리 빌드 시각이 다르면 짝이 안 맞는 조합이다(MethodNotFound 로 죽는다).
                yield return time is null
                    ? $"[BOOT]   {name}: {Revision(asm)} · 경로 확인 불가(단일파일/메모리 로드)"
                    : $"[BOOT]   {name}: {Revision(asm)} · 파일 {time:yyyy-MM-dd HH:mm:ss} · {size:N0} bytes · {path}";
            }
        }

        /// <summary>어셈블리에 박힌 빌드 시각(SourceRevisionId). 없으면 "빌드시각 없음".</summary>
        private static string Revision(Assembly asm)
        {
            try
            {
                string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                int plus = info?.IndexOf('+') ?? -1;
                return plus >= 0 && plus + 1 < info!.Length ? "빌드 " + info[(plus + 1)..] : "빌드시각 없음";
            }
            catch { return "빌드시각 없음"; }
        }

        private static string Stat(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return $"{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss} · {fi.Length:N0} bytes · {path}";
            }
            catch { return path; }
        }

        private static (string? Path, DateTime? Time, long Size) Describe(Assembly asm)
        {
            try
            {
                string path = asm.Location;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (null, null, 0);
                var fi = new FileInfo(path);
                return (path, fi.LastWriteTime, fi.Length);
            }
            catch { return (null, null, 0); }
        }
    }
}
