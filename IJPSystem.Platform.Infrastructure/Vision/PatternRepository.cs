using IJPSystem.Platform.Common.Utilities;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IJPSystem.Platform.Infrastructure.Vision
{
    /// <summary>
    /// 등록된 정렬 패턴 하나. 이미지는 옆의 <c>.png</c> 에 따로 있다.
    ///
    /// <para><b>기준 위치를 같이 적는 이유</b>: 정렬은 "찾았다"가 아니라
    /// "등록할 때 있던 자리에서 얼마나 벗어났나"다. 기준이 없으면 벗어난 양을 말할 수 없다.</para>
    /// </summary>
    public sealed class PatternDefinition
    {
        public string Name { get; set; } = "";

        /// <summary>등록 당시 패턴 중심의 장면 픽셀 좌표. 이 값이 정렬의 0점이다.</summary>
        public double ReferenceX { get; set; }
        public double ReferenceY { get; set; }

        /// <summary>등록에 쓴 화면 해상도. 다르면 좌표 기준이 어긋난다.</summary>
        public int SceneWidth { get; set; }
        public int SceneHeight { get; set; }

        public int TemplateWidth { get; set; }
        public int TemplateHeight { get; set; }

        /// <summary>합격 점수(0~1). 이 아래면 못 찾은 것으로 본다.</summary>
        public double MinScore { get; set; } = 0.70;

        /// <summary>기준 위치 주변만 볼 반경(픽셀). 0 이면 화면 전체.</summary>
        public int SearchRadiusPx { get; set; }

        public DateTime SavedAt { get; set; } = DateTime.Now;

        /// <summary>등록 화면과 지금 화면의 해상도가 같은지. 다르면 좌표를 믿을 수 없다.</summary>
        public bool MatchesScene(int width, int height)
            => SceneWidth == 0 || SceneHeight == 0 || (SceneWidth == width && SceneHeight == height);
    }

    /// <summary>패턴 파일 한 벌(정의 + 이미지).</summary>
    public sealed record PatternEntry(PatternDefinition Definition, GrayImage Template, string BasePath);

    /// <summary>
    /// 정렬 패턴 저장소. <c>Config\Patterns\</c> 에 <c>이름.png</c> + <c>이름.json</c> 두 벌로 둔다.
    ///
    /// <para>이미지를 json 안에 base64 로 넣지 않은 이유: 등록한 패턴을 사람이 그림판으로 열어
    /// 확인할 수 있어야 한다. 현장에서 "무엇을 등록해 둔 건지" 묻는 일이 가장 많다.</para>
    /// </summary>
    public sealed class PatternRepository
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
        };

        public PatternRepository(string? rootDirectory = null)
        {
            RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? PathUtils.GetConfigPath("Patterns")
                : rootDirectory!;
        }

        public string RootDirectory { get; }

        public IReadOnlyList<string> List()
        {
            if (!Directory.Exists(RootDirectory)) return Array.Empty<string>();

            return Directory.EnumerateFiles(RootDirectory, "*.json")
                            .Select(Path.GetFileNameWithoutExtension)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Select(n => n!)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }

        /// <summary>파일 이름에 못 쓰는 문자를 걷어낸다.</summary>
        public static string SanitizeName(string name)
        {
            string s = (name ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "pattern" : s;
        }

        public string BasePathOf(string name) => Path.Combine(RootDirectory, SanitizeName(name));

        /// <summary>
        /// 저장. 두 파일이 한 벌이므로 <b>둘 다 성공해야</b> 한다 —
        /// 이미지만 남고 정의가 없으면 다음에 읽을 때 조용히 무시된다.
        /// </summary>
        public void Save(PatternDefinition definition, GrayImage template)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (template == null) throw new ArgumentNullException(nameof(template));

            Directory.CreateDirectory(RootDirectory);

            string basePath = BasePathOf(definition.Name);
            definition.TemplateWidth  = template.Width;
            definition.TemplateHeight = template.Height;

            WritePng(basePath + ".png", template);
            File.WriteAllText(basePath + ".json", JsonSerializer.Serialize(definition, Json));
        }

        /// <summary>읽기. 한 벌이 갖춰지지 않았으면 null.</summary>
        public PatternEntry? Load(string name)
        {
            string basePath = BasePathOf(name);
            string json = basePath + ".json";
            string png  = basePath + ".png";

            if (!File.Exists(json) || !File.Exists(png)) return null;

            try
            {
                var def = JsonSerializer.Deserialize<PatternDefinition>(File.ReadAllText(json));
                if (def == null) return null;

                var img = ReadPng(png);
                if (img == null) return null;

                if (string.IsNullOrWhiteSpace(def.Name)) def.Name = name;
                return new PatternEntry(def, img, basePath);
            }
            catch { return null; }
        }

        public void Remove(string name)
        {
            string basePath = BasePathOf(name);
            foreach (string ext in new[] { ".json", ".png" })
            {
                try { File.Delete(basePath + ext); } catch { /* 이미 없으면 그만 */ }
            }
        }

        // ── 이미지 입출력 ────────────────────────────────────────────────

        private static void WritePng(string path, GrayImage img)
        {
            using var mat = new Mat(img.Height, img.Width, MatType.CV_8UC1);
            System.Runtime.InteropServices.Marshal.Copy(img.Pixels, 0, mat.Data, img.Width * img.Height);

            // 메모리로 인코딩한 뒤 임시 파일에 쓰고 바꿔치기한다.
            // ImWrite 로 바로 쓰지 않는 이유: 형식을 <b>확장자로</b> 고르는데, 임시 이름은
            // ".tmp" 라 인코더를 못 찾는다(테스트에서 걸렸다).
            Cv2.ImEncode(".png", mat, out byte[] png);

            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, png);
            File.Move(tmp, path, overwrite: true);
        }

        private static GrayImage? ReadPng(string path)
        {
            using var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (mat.Empty()) return null;

            var pixels = new byte[mat.Width * mat.Height];
            if (mat.IsContinuous())
            {
                System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
            }
            else
            {
                for (int y = 0; y < mat.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        mat.Ptr(y), pixels, y * mat.Width, mat.Width);
            }
            return new GrayImage(pixels, mat.Width, mat.Height);
        }
    }
}
