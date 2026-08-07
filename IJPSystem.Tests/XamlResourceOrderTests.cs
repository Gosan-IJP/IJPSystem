using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// XAML 리소스 딕셔너리 안의 <b>전방 참조</b>를 잡는다.
    ///
    /// <para>
    /// <c>StaticResource</c> 는 같은 딕셔너리에서 <b>앞에 정의된 것만</b> 찾는다. 뒤에 정의된 것을
    /// 참조하면 컴파일은 통과하고 <b>화면을 열 때</b> 죽는다 — 실제로 새 스타일을 딕셔너리 위쪽에
    /// 넣었다가 레시피 화면 진입에서 "NumericEditingStyle 을 찾을 수 없음" 으로 앱이 내려갔다
    /// (2026-08-07). 빌드로는 안 잡히니 여기서 잡는다.
    /// </para>
    /// <para>
    /// 딕셔너리 <b>바깥</b>(요소 트리)의 참조는 검사하지 않는다 — 그때는 딕셔너리가 이미 다
    /// 읽힌 뒤라 순서와 무관하다.
    /// </para>
    /// </summary>
    public class XamlResourceOrderTests
    {
        [Fact]
        public void StaticResource_IsNeverUsedBeforeItIsDefined()
        {
            var offenders = new List<string>();

            foreach (string path in XamlFiles())
            {
                string text = File.ReadAllText(path);

                // 이 파일에서 정의하는 리소스의 <b>첫</b> 정의 위치.
                var definedAt = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches(text, @"x:Key\s*=\s*""([^""{}]+)"""))
                {
                    string key = m.Groups[1].Value;
                    if (!definedAt.ContainsKey(key)) definedAt[key] = m.Index;
                }
                if (definedAt.Count == 0) continue;

                int dictEnd = ResourceSectionEnd(text);
                if (dictEnd <= 0) continue;

                foreach (Match m in Regex.Matches(text, @"\{StaticResource\s+([^}\s]+)\s*\}"))
                {
                    if (m.Index >= dictEnd) continue;                    // 요소 트리 — 순서 무관
                    string key = m.Groups[1].Value;
                    if (!definedAt.TryGetValue(key, out int defIndex)) continue;  // 다른 파일(App.xaml 등) 정의
                    if (m.Index >= defIndex) continue;                   // 정상: 정의가 앞

                    offenders.Add($"{Path.GetFileName(path)}: '{key}' 를 정의({Line(text, defIndex)}행)보다 " +
                                  $"앞({Line(text, m.Index)}행)에서 참조 — 화면을 열 때 죽는다");
                }
            }

            Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
        }

        /// <summary>리소스 섹션의 끝 위치. 여러 개면 마지막 것 — 그 뒤는 전부 요소 트리다.</summary>
        private static int ResourceSectionEnd(string text)
        {
            int end = -1;
            foreach (Match m in Regex.Matches(text, @"</\w+(\.\w+)?\.Resources>"))
                end = Math.Max(end, m.Index + m.Length);
            return end;
        }

        private static int Line(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

        private static IEnumerable<string> XamlFiles()
        {
            string root = RepoRoot();
            string hmi = Path.Combine(root, "IJPSystem.Platform.HMI");
            Assert.True(Directory.Exists(hmi), $"HMI 프로젝트를 찾지 못했다: {hmi}");

            return Directory.EnumerateFiles(hmi, "*.xaml", SearchOption.AllDirectories)
                            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IJPSystem.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
