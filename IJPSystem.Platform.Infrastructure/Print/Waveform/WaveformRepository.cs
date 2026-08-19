using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print.Waveform
{
    /// <summary>파형 목록의 한 항목. 화면 콤보 한 줄에 대응한다.</summary>
    /// <param name="Index">목록 순번(1부터). Meteor 도구 표기와 맞추기 위한 표시용이다.</param>
    /// <param name="Name">확장자를 뺀 베이스명. 실제 파일은 <c>{Name}.ComA</c> 등으로 존재한다.</param>
    /// <param name="BasePath">확장자를 뺀 전체 경로. 레시피에 기록하는 값과 같은 형식이다.</param>
    /// <param name="HasComB">ComB 파일이 함께 있는가.</param>
    public sealed record WaveformEntry(int Index, string Name, string BasePath, bool HasComB, bool IsDefault)
    {
        /// <summary>"*52: 26.06.30_EG+EtoH test1  [Default]" 형태의 표시 문자열.</summary>
        public string DisplayName =>
            $"{(IsDefault ? "*" : "")}{Index}: {Name}{(HasComB ? "" : "  (ComA 만)")}{(IsDefault ? "  [Default]" : "")}";
    }

    /// <summary>
    /// 파형 파일 저장소 — 목록 · 가져오기 · 삭제 · 이름 변경 · 기본값 지정.
    ///
    /// <para><b>한 파형은 파일 하나가 아니라 파일 묶음이다</b>: <c>.ComA</c> · <c>.ComB</c> · <c>.Vst</c>.
    /// 그래서 모든 조작은 묶음 단위로 한다. ComA 만 이름을 바꾸면 짝이 끊어지는데,
    /// 화면에는 멀쩡히 목록에 남아 있고 로드할 때가 되어서야 ComB 가 없다는 걸 알게 된다.</para>
    /// </summary>
    public sealed class WaveformRepository
    {
        /// <summary>한 파형을 이루는 확장자. 앞의 것이 대표(목록 판정 기준)다.</summary>
        public static readonly string[] Extensions = { ".ComA", ".ComB", ".Vst" };

        /// <summary>기본 파형 이름을 적어 두는 파일. 파일명 규칙을 건드리지 않으려고 따로 둔다.</summary>
        private const string DefaultMarkerFile = "_default.txt";

        public string RootDirectory { get; }

        public WaveformRepository(string? rootDirectory = null)
        {
            RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? DefaultRoot() : rootDirectory!;
            Directory.CreateDirectory(RootDirectory);
        }

        /// <summary>
        /// 파형 폴더를 정한다. 이미 쓰던 폴더가 있으면 그것을 그대로 쓴다 —
        /// 새 규약을 들고 오면서 설비에 있던 파형이 목록에서 사라지면 안 된다.
        /// <list type="number">
        ///   <item>Meteor 설치 규약 경로(<c>%PUBLIC%\Documents\Meteor\Waveform</c>)가 있으면 그것</item>
        ///   <item>없고 예전 경로(<c>C:\Waveforms</c>)가 있으면 그것</item>
        ///   <item>둘 다 없으면 Meteor 규약 경로를 새로 만든다</item>
        /// </list>
        /// </summary>
        public static string DefaultRoot()
        {
            string meteor = MeteorRoot();
            if (Directory.Exists(meteor)) return meteor;
            if (Directory.Exists(LegacyRoot)) return LegacyRoot;
            return meteor;
        }

        /// <summary>예전부터 쓰던 파형 폴더.</summary>
        public const string LegacyRoot = @"C:\Waveforms";

        public static string MeteorRoot()
        {
            string pub = Environment.GetEnvironmentVariable("PUBLIC")
                         ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            return Path.Combine(pub, "Documents", "Meteor", "Waveform");
        }

        /// <summary>폴더의 파형 목록. 대표 확장자(.ComA)가 있는 것만 하나의 파형으로 본다.</summary>
        public IReadOnlyList<WaveformEntry> List()
        {
            if (!Directory.Exists(RootDirectory)) return Array.Empty<WaveformEntry>();

            string? def = ReadDefaultName();
            return Directory.EnumerateFiles(RootDirectory, "*" + Extensions[0])
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select((f, i) =>
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    string basePath = Path.Combine(RootDirectory, name);
                    return new WaveformEntry(
                        i + 1, name, basePath,
                        File.Exists(basePath + Extensions[1]),
                        string.Equals(name, def, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }

        public WaveformEntry? Find(string name) =>
            List().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 파형을 폴더로 들여온다. 고른 파일이 <c>.ComA</c> 든 <c>.ComB</c> 든
        /// <b>같은 베이스명의 짝을 모두</b> 가져온다.
        /// </summary>
        public WaveformEntry Import(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("가져올 파형 파일이 없습니다.", sourcePath);

            string srcDir  = Path.GetDirectoryName(sourcePath) ?? "";
            string srcBase = BaseNameOf(Path.GetFileName(sourcePath));

            var found = Extensions
                .Select(ext => Path.Combine(srcDir, srcBase + ext))
                .Where(File.Exists)
                .ToList();
            if (found.Count == 0)
                throw new FileNotFoundException($"'{srcBase}' 의 파형 파일을 찾지 못했습니다.", sourcePath);

            string target = UniqueName(srcBase);
            foreach (string src in found)
                File.Copy(src, Path.Combine(RootDirectory, target + Path.GetExtension(src)), overwrite: false);

            return Find(target) ?? throw new IOException($"가져오기 후 목록에서 '{target}' 을 찾지 못했습니다.");
        }

        /// <summary>파형을 이루는 파일을 모두 지운다. 기본값이었다면 표시도 지운다.</summary>
        public void Remove(WaveformEntry entry)
        {
            foreach (string p in SetFiles(entry.Name)) File.Delete(p);
            if (entry.IsDefault) ClearDefault();
        }

        /// <summary>파형 묶음 전체의 이름을 바꾼다.</summary>
        public WaveformEntry Rename(WaveformEntry entry, string newName)
        {
            string target = SanitizeName(newName);
            if (string.Equals(target, entry.Name, StringComparison.OrdinalIgnoreCase)) return entry;

            // 짝 중 하나라도 이미 있으면 멈춘다 — 일부만 덮어쓰면 서로 다른 파형이 섞인다.
            foreach (string ext in Extensions)
                if (File.Exists(Path.Combine(RootDirectory, target + ext)))
                    throw new IOException($"같은 이름의 파형이 이미 있습니다: {target}");

            foreach (string src in SetFiles(entry.Name))
                File.Move(src, Path.Combine(RootDirectory, target + Path.GetExtension(src)));

            if (entry.IsDefault) WriteDefaultName(target);
            return Find(target) ?? throw new IOException($"이름 변경 후 목록에서 '{target}' 을 찾지 못했습니다.");
        }

        public void MakeDefault(WaveformEntry entry) => WriteDefaultName(entry.Name);

        public WaveformEntry? GetDefault() => List().FirstOrDefault(e => e.IsDefault);

        // ── 내부 ──────────────────────────────────────────────────────────

        /// <summary>실제로 존재하는 묶음 파일 경로들.</summary>
        private IEnumerable<string> SetFiles(string name) =>
            Extensions.Select(ext => Path.Combine(RootDirectory, name + ext)).Where(File.Exists);

        private string DefaultMarkerPath => Path.Combine(RootDirectory, DefaultMarkerFile);

        private string? ReadDefaultName() =>
            File.Exists(DefaultMarkerPath) ? File.ReadAllText(DefaultMarkerPath).Trim() : null;

        private void WriteDefaultName(string name) => File.WriteAllText(DefaultMarkerPath, name);

        private void ClearDefault()
        {
            if (File.Exists(DefaultMarkerPath)) File.Delete(DefaultMarkerPath);
        }

        private string UniqueName(string baseName)
        {
            string name = SanitizeName(baseName);
            string candidate = name;
            int n = 2;
            while (Extensions.Any(ext => File.Exists(Path.Combine(RootDirectory, candidate + ext))))
                candidate = $"{name}_{n++}";
            return candidate;
        }

        /// <summary>확장자가 파형 확장자면 떼어낸다. 아니면 파일명을 그대로 베이스명으로 본다.</summary>
        public static string BaseNameOf(string fileName)
        {
            foreach (string ext in Extensions)
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return fileName[..^ext.Length];
            return Path.GetFileNameWithoutExtension(fileName);
        }

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Untitled";
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
