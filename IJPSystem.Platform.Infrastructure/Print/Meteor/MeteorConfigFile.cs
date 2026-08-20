using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print.Meteor
{
    /// <summary>
    /// cfg 에 등록된 파형 한 개. Meteor 는 <c>.ComA</c> 만 적고 같은 이름의
    /// <c>.ComB</c> 를 자동으로 함께 읽는다(파일 주석에 그렇게 적혀 있다).
    /// </summary>
    /// <param name="Index">Waveform<b>N</b> 의 N. 레시피가 아니라 이 번호로 헤드가 파형을 고른다.</param>
    /// <param name="Relative">cfg 에 적힌 그대로의 경로.</param>
    /// <param name="FullPath">cfg 폴더 기준으로 푼 경로.</param>
    /// <param name="Exists">그 자리에 실제로 파일이 있나.</param>
    /// <param name="IsDefault">WaveformFileIdx 가 가리키는 기본 파형인가.</param>
    public sealed record MeteorWaveformRef(
        int Index, string Relative, string FullPath, bool Exists, bool IsDefault)
    {
        public string Name => Path.GetFileNameWithoutExtension(Relative);
    }

    /// <summary>
    /// Meteor PrintEngine 설정 파일(.cfg) 읽기.
    ///
    /// <para><b>거의 읽기만 한다</b>: 이 파일은 Meteor 설치가 관리하고 현장에서 사람이
    /// 직접 편집한다. HMI 가 통째로 고쳐 쓰면 어느 쪽이 맞는지 알 수 없게 되므로,
    /// 화면은 "PCC 가 실제로 무엇을 읽고 있는지"를 보여 주는 것이 본업이다.
    /// 쓰기는 <see cref="SetValue"/> 하나뿐이고, 그것도 지정한 키 한 줄만 바꾼다.</para>
    ///
    /// <para>형식은 INI 계열이다 — <c>[섹션]</c>, <c>Key = Value</c>, <c>;</c> 뒤는 주석,
    /// 값은 따옴표로 감쌀 수 있다. 헤드 관련 값은 <c>[System] HeadType</c> 이름의
    /// 섹션(예: <c>[EPSON_S3200]</c>)에 들어 있어, 헤드를 바꾸면 섹션 이름도 바뀐다.</para>
    /// </summary>
    public sealed class MeteorConfigFile
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections;

        private MeteorConfigFile(string path, bool exists, string rawText,
                                 Dictionary<string, Dictionary<string, string>> sections)
        {
            FilePath  = path;
            Exists    = exists;
            RawText   = rawText;
            _sections = sections;
        }

        public string FilePath { get; }
        public bool   Exists   { get; }

        /// <summary>파일 원문. 화면에서 그대로 보여 준다 — 우리가 못 읽는 항목도 눈으로 확인하려고.</summary>
        public string RawText { get; }

        /// <summary>읽기 실패 사유. 성공했거나 파일이 없으면 빈 문자열.</summary>
        public string LoadError { get; private set; } = "";

        /// <summary>파일을 읽는다. 없거나 못 읽어도 예외를 던지지 않는다(화면이 사유를 띄운다).</summary>
        public static MeteorConfigFile Load(string path)
        {
            var empty = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new MeteorConfigFile(path ?? "", false, "", empty);

            try
            {
                string text = File.ReadAllText(path);
                return new MeteorConfigFile(path, true, text, Parse(text));
            }
            catch (Exception ex)
            {
                return new MeteorConfigFile(path, true, "", empty) { LoadError = ex.Message };
            }
        }

        private static Dictionary<string, Dictionary<string, string>> Parse(string text)
        {
            var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[""] = current;

            foreach (string raw in text.Split('\n'))
            {
                // 주석은 ';' 부터 줄 끝까지. 값 안의 따옴표에 ';' 가 들어간 예는 없다.
                int c = raw.IndexOf(';');
                string line = (c >= 0 ? raw[..c] : raw).Trim();
                if (line.Length == 0) continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    string name = line[1..^1].Trim();
                    if (!map.TryGetValue(name, out current))
                        map[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim().Trim('"').Trim();
                if (key.Length > 0) current[key] = val;
            }
            return map;
        }

        // ── 값 꺼내기 ────────────────────────────────────────────────────
        public string Get(string section, string key)
            => _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out string? v) ? v : "";

        public int GetInt(string section, string key, int fallback = 0)
            => int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                   ? v : fallback;

        /// <summary>섹션 이름 목록 — 화면에서 "무엇이 들어 있는지" 훑을 때 쓴다.</summary>
        public IReadOnlyList<string> SectionNames
            => _sections.Keys.Where(k => k.Length > 0).ToList();

        // ── 자주 보는 값 ─────────────────────────────────────────────────
        public string PccType         => Get("System", "PccType");
        public string HeadType        => Get("System", "HeadType");
        public string EthernetAdapter => Get("Ethernet", "Adapter1");

        /// <summary>헤드 전용 섹션 이름. <c>[System] HeadType</c> 과 같은 이름이다.</summary>
        public string HeadSection => HeadType;

        public int Xdpi         => GetInt(HeadSection, "Xdpi");
        public int BitsPerPixel => GetInt(HeadSection, "BitsPerPixel");

        /// <summary>계조 단계 수. BPP 2 = 4단계(GL0~GL3) — 파형 화면의 GL 배정표와 같은 수다.</summary>
        public int GreyLevels => BitsPerPixel > 0 ? 1 << BitsPerPixel : 0;

        public int    PlanesPerHdc => GetInt("Planes", "PlanesPerHdc");
        public int    Yinterlace   => GetInt("Planes", "Yinterlace");
        public string Plane1       => Get("Planes", "Plane1");

        public int PrintClock       => GetInt("Encoder", "PrintClock");
        public int EncoderMultiplier=> GetInt("Encoder", "Multiplier");
        public int EncoderDivider   => GetInt("Encoder", "Divider");
        public bool EncoderQuadrature => GetInt("Encoder", "Quadrature") != 0;

        /// <summary>기본 파형 번호. 헤드는 레시피가 아니라 이 번호로 파형을 고른다.</summary>
        public int WaveformFileIdx => GetInt("DefaultParameterValues", "WaveformFileIdx", 1);

        /// <summary>cfg 에 등록된 파형 목록(Waveform1~100). 헤드가 고를 수 있는 전부다.</summary>
        public IReadOnlyList<MeteorWaveformRef> Waveforms
        {
            get
            {
                var list = new List<MeteorWaveformRef>();
                if (!Exists) return list;

                string baseDir = Path.GetDirectoryName(FilePath) ?? "";
                int defaultIdx = WaveformFileIdx;

                for (int i = 1; i <= 100; i++)
                {
                    string rel = Get(HeadSection, "Waveform" + i);
                    if (string.IsNullOrWhiteSpace(rel)) continue;

                    string full = rel;
                    try
                    {
                        if (!Path.IsPathRooted(rel)) full = Path.GetFullPath(Path.Combine(baseDir, rel));
                    }
                    catch { /* 경로에 못 쓰는 문자 — 적힌 그대로 보여 준다 */ }

                    bool exists = false;
                    try { exists = File.Exists(full); } catch { }

                    list.Add(new MeteorWaveformRef(i, rel, full, exists, i == defaultIdx));
                }
                return list;
            }
        }

        // ── 로그 설정([Test] 섹션) ───────────────────────────────────────
        // 엔진 로그는 헤드가 안 뜰 때 유일한 단서다. 어디에 쓰이고 있는지 화면에서
        // 바로 알 수 있어야 한다.

        public const string TestSection = "Test";

        /// <summary>로그를 파일로 남기나. 0 이면 화면 로그만 나오고 파일은 안 생긴다.</summary>
        public bool LogToDisk => GetInt(TestSection, "LogToDisk") != 0;

        /// <summary>로그 파일 이름. 비어 있으면 엔진 기본값을 쓴다.</summary>
        public string LogFileName => Get(TestSection, "LogFile");

        /// <summary>로그 파일의 실제 경로(상대경로는 cfg 폴더 기준). 이름이 없으면 빈 문자열.</summary>
        public string LogFilePath => ResolveRelative(LogFileName);

        /// <summary>Sim 파일 폴더.</summary>
        public string SimFilePath => ResolveRelative(Get(TestSection, "SimFilePath"));

        /// <summary>cfg 에 적힌 상대 경로를 cfg 폴더 기준으로 푼다.</summary>
        public string ResolveRelative(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return "";
            try
            {
                if (Path.IsPathRooted(relative)) return Path.GetFullPath(relative);
                string baseDir = Path.GetDirectoryName(FilePath) ?? "";
                return Path.GetFullPath(Path.Combine(baseDir, relative));
            }
            catch { return relative; }
        }

        // ── 값 하나만 고쳐 쓰기 ──────────────────────────────────────────

        /// <summary>
        /// 키 하나를 제자리에서 갈아 끼운다.
        ///
        /// <para><b>파일 전체를 다시 쓰지 않는 이유</b>: 이 cfg 는 현장에서 사람이 직접
        /// 편집하는 파일이다. 우리가 파싱한 값으로 통째로 다시 쓰면 손으로 넣은 주석과
        /// 줄 순서가 날아간다 — 그러면 다음 사람이 무엇을 왜 바꿨는지 알 수 없다.
        /// 줄 끝 주석까지 그대로 두고, 그 줄만 바꾼다.</para>
        ///
        /// <para>임시 파일에 다 쓴 뒤 바꿔치기한다. 쓰는 도중에 죽어도 기존 cfg 는 멀쩡하다.</para>
        /// </summary>
        public static void SetValue(string path, string section, string key, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

            int start = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length < 2 || t[0] != '[' || t[^1] != ']') continue;

                string name = t[1..^1].Trim();
                if (start < 0 && string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) start = i;
                else if (start >= 0) { end = i; break; }
            }

            if (start < 0)
            {
                // 섹션 자체가 없으면 파일 끝에 새로 만든다.
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add($"{key} = {value}");
            }
            else
            {
                bool replaced = false;
                for (int i = start + 1; i < end; i++)
                {
                    string line = lines[i];
                    int c = line.IndexOf(';');
                    string body = c >= 0 ? line[..c] : line;

                    int eq = body.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(body[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;

                    string comment = line.Length > body.Length ? line[body.Length..] : "";
                    lines[i] = $"{key} = {value}{comment}";
                    replaced = true;
                    break;
                }

                if (!replaced) lines.Insert(end, $"{key} = {value}");
            }

            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
