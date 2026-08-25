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

        /// <summary>이 비율 안의 차이는 진행하되 오차만 알린다. 넘으면 막는다.</summary>
        public const double SceneTolerancePercent = 2.0;

        /// <summary>
        /// 등록 화면과 지금 화면의 해상도 차이를 판정한다.
        ///
        /// <para><b>매칭 자체는 해상도와 무관하다.</b> 정규화 상관은 템플릿을 장면 위로 훑을 뿐이라
        /// 장면이 몇 픽셀 크든 작든 찾는 데는 지장이 없다(템플릿보다 크기만 하면 된다).
        /// 흔들리는 것은 <b>기준 좌표</b>다 — "등록할 때 있던 자리에서 얼마나 벗어났나"의 그 자리.</para>
        ///
        /// <para>그래서 조금 다르다고 막을 이유가 없다. 다만 차이가 <b>축소·확대인지 잘라낸 것인지</b>는
        /// 알 수 없으므로 임의로 좌표를 늘려 맞추지 않는다 — 대신 그 차이가 기준 좌표에 얼마만큼의
        /// 오차로 들어오는지를 계산해 알린다. 크게 다르면 그 오차가 의미를 잃으므로 막는다.</para>
        /// </summary>
        public SceneCheck CheckScene(int width, int height)
        {
            if (SceneWidth <= 0 || SceneHeight <= 0 || width <= 0 || height <= 0)
                return new SceneCheck(SceneFit.Same, 0, "");   // 등록 정보가 없으면 따지지 않는다

            if (SceneWidth == width && SceneHeight == height)
                return new SceneCheck(SceneFit.Same, 0, "");

            double dw = Math.Abs(width  - SceneWidth)  * 100.0 / SceneWidth;
            double dh = Math.Abs(height - SceneHeight) * 100.0 / SceneHeight;

            string what = $"등록 {SceneWidth}×{SceneHeight} → 지금 {width}×{height}";

            if (Math.Max(dw, dh) > SceneTolerancePercent)
                return new SceneCheck(SceneFit.Different, 0,
                    $"등록할 때와 해상도가 너무 다릅니다.\n{what}\n\n" +
                    "기준 좌표를 믿을 수 없어 패턴을 다시 등록해야 합니다.");

            // 오차는 두 해석 중 큰 쪽으로 잡는다 — 어느 쪽인지 모르니 나쁜 쪽을 말해야 한다.
            //   ① 같은 화면을 배율만 달리 찍었다 → 기준 좌표가 비율만큼 밀린다
            //   ② 가장자리를 더/덜 잘라냈다      → 최대 그 픽셀 차이만큼 밀린다
            double ex = Math.Max(Math.Abs(ReferenceX * (width  / (double)SceneWidth  - 1)), Math.Abs(width  - SceneWidth));
            double ey = Math.Max(Math.Abs(ReferenceY * (height / (double)SceneHeight - 1)), Math.Abs(height - SceneHeight));
            double err = Math.Max(ex, ey);

            return new SceneCheck(SceneFit.Close, err,
                $"해상도가 조금 다릅니다({what}) — 벗어난 양에 최대 약 {err:F0}px 오차가 섞입니다.");
        }
    }

    /// <summary>등록 해상도와 지금 해상도의 관계.</summary>
    public enum SceneFit
    {
        /// <summary>같다(또는 등록 정보 없음).</summary>
        Same,
        /// <summary>조금 다르다 — 찾기는 하되 오차를 알린다.</summary>
        Close,
        /// <summary>너무 다르다 — 기준 좌표가 의미를 잃는다.</summary>
        Different,
    }

    /// <summary>해상도 판정 결과.</summary>
    public readonly record struct SceneCheck(SceneFit Fit, double MaxRefErrorPx, string Message)
    {
        /// <summary>찾기를 진행해도 되는가.</summary>
        public bool CanFind => Fit != SceneFit.Different;
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
