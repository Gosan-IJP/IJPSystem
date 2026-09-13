using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace IJPSystem.Platform.Infrastructure.Config
{
    /// <summary>
    /// 기동할 때 <b>설정 파일이 무엇이었나</b>를 로그 줄로 남긴다.
    ///
    /// <para><b>왜 필요한가</b>: 호기마다 모양이 똑같은 Config 폴더에 다른 호기 파일이 섞이는 사고가
    /// 가장 찾기 어렵다(2026-09-11 10호기에 11호기의 COM10 이 들어가 있었다). 기동 로그에는 드라이버
    /// 모드와 호기만 남아, 출하 후 "그날 어떤 설정으로 돌았나" 를 로그로는 확정할 수 없었다.</para>
    ///
    /// <para><b>무엇을 남기나</b></para>
    /// <list type="bullet">
    ///   <item>파일마다 수정 시각·크기·짧은 해시 — 두 날의 로그를 견주면 "설정이 바뀌었나" 가 바로 갈린다.</item>
    ///   <item>JSON 안의 <b>호기마다 다른 값</b>(포트·주소·축 배선·원점 방식) — 이름으로 찾아 경로와 함께 적는다.</item>
    ///   <item>PCC 가 읽는 cfg 의 헤드·해상도·파형 번호 — 이 파일은 Config 폴더 밖에 있을 수도 있다.</item>
    /// </list>
    ///
    /// <para><b>구조를 가정하지 않는다</b>: 파일마다 모양을 적어 두면 설정 구조가 바뀔 때 조용히 틀린다.
    /// 대신 알려진 키 이름을 트리 전체에서 찾는다 — 키가 옮겨 가도 따라간다.</para>
    ///
    /// <para><c>*호기*</c> 사본(예: <c>VisionConfig_11호기.json</c>)은 뺀다 — 앱이 읽지 않는 참고용이라,
    /// 적어 두면 "이 값으로 돌았다" 로 잘못 읽힌다.</para>
    /// </summary>
    public static class ConfigSnapshot
    {
        /// <summary>호기마다 다르거나, 틀리면 장비가 엉뚱하게 움직이는 값들. 이 이름의 키를 찾아 적는다.</summary>
        private static readonly HashSet<string> KeyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MachineNo", "MachineType",
            "IO", "Motion", "Vision", "Head", "Meniscus",          // DriverMode
            "MeteorConfigPath", "IsDoorCheckEnabled", "LogSaveDays", "LogMaxTotalMB",
            "ComPort", "BaudRate", "UnitId",                       // 시리얼 장치
            "MacAddress", "SerialNumber", "IpAddress", "Driver",   // 카메라
            "HwAxis", "InvertDirection", "SwapLimitSensors", "EncoderPulsePerUnit",
            "Mode", "Direction", "Absolute",                       // 원점복귀
            "Device",
            // NI 트리거 체인 — 단자 이름에 장치 이름(Dev1)이 들어 있어 배선·NI MAX 이름이 함께 보인다
            "SpitPulseTerminal", "TimebaseTerminal", "DividerCounter", "LedCounter", "CamCounter", "DivideRatio",
            // 드랍와처 — 노즐 피치(84.7 인가 254 인가)와 픽셀 크기는 측정값 전부를 좌우한다
            "NozzlePitchUm", "NozzlePitchPx", "MicronsPerPixel", "FieldOfViewXUm", "NozzleOriginXPx",
            // Comizoa 라이브러리(ini) — SimulationMode=1 이면 실장비가 가짜로 움직인다
            "EmbeddedMode", "SimulationMode", "LOCAL_IP", "DAEMON_IP", "MASTER_IP",
        };

        /// <summary>파일 하나에서 적을 값의 상한 — 넘으면 "외 N개" 로 줄인다(한 줄이 끝없이 길어지지 않게).</summary>
        private const int MaxValuesPerFile = 40;

        /// <summary>기동 로그에 쓸 줄들 — 앱이 실제로 읽는 Config 폴더 기준. 무엇 하나 못 읽어도 나머지는 돌려준다.</summary>
        public static IReadOnlyList<string> Lines()
        {
            string dir;
            try
            {
                dir = Path.GetDirectoryName(PathUtils.GetConfigPath("AppConfig.json")) ?? "";
            }
            catch (Exception ex)
            {
                return new[] { $"[CONFIG] 스냅샷 — Config 폴더를 못 찾음: {ExceptionText.Summary(ex)}" };
            }
            return Lines(dir);
        }

        /// <summary>폴더를 지정해 읽는다 — 테스트나 다른 PC 의 Config 사본을 볼 때.</summary>
        public static IReadOnlyList<string> Lines(string dir)
        {
            var lines = new List<string>();

            if (!Directory.Exists(dir))
            {
                lines.Add($"[CONFIG] 스냅샷 — Config 폴더 없음: {dir}");
                return lines;
            }

            lines.Add($"[CONFIG] 스냅샷 — {dir}");

            IEnumerable<string> files;
            try
            {
                files = new[] { "*.json", "*.ini", "*.cfg" }
                    .SelectMany(p => Directory.EnumerateFiles(dir, p, SearchOption.TopDirectoryOnly))
                    .Where(f => !Path.GetFileName(f).Contains("호기"))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                lines.Add($"[CONFIG] 스냅샷 — 파일 목록 실패: {ExceptionText.Summary(ex)}");
                return lines;
            }

            foreach (var file in files)
            {
                lines.Add(FileLine(file));

                string? values =
                    file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonValues(file) :
                    file.EndsWith(".ini",  StringComparison.OrdinalIgnoreCase) ? IniValues(file)  : null;
                if (values != null) lines.Add($"[CONFIG]   {Path.GetFileName(file)}: {values}");
            }

            lines.AddRange(MeteorCfgLines());
            return lines;
        }

        /// <summary><c>MotorConfig.json · 2026-09-11 16:02 · 3,412B · #a1b2c3d4</c></summary>
        private static string FileLine(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return $"[CONFIG] {fi.Name} · {fi.LastWriteTime:yyyy-MM-dd HH:mm} · {fi.Length:N0}B · #{ShortHash(path)}";
            }
            catch (Exception ex)
            {
                return $"[CONFIG] {Path.GetFileName(path)} · 읽기 실패: {ExceptionText.Summary(ex)}";
            }
        }

        /// <summary>파일 내용의 짧은 지문(SHA-256 앞 8자리). 같은 이름·크기라도 내용이 바뀌면 달라진다.</summary>
        private static string ShortHash(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        /// <summary>JSON 에서 <see cref="KeyNames"/> 에 든 키를 찾아 <c>경로=값</c> 으로 잇는다. 없으면 null.</summary>
        private static string? JsonValues(string path)
        {
            try
            {
                using var fs  = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions
                {
                    CommentHandling     = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                var found = new List<string>();
                Walk(doc.RootElement, "", found);
                if (found.Count == 0) return null;

                return found.Count <= MaxValuesPerFile
                    ? string.Join(", ", found)
                    : string.Join(", ", found.Take(MaxValuesPerFile)) + $", … 외 {found.Count - MaxValuesPerFile}개";
            }
            catch (Exception ex)
            {
                return $"(해석 실패: {ExceptionText.Summary(ex)})";
            }
        }

        /// <summary>INI 의 <c>Key = Value</c> 중 <see cref="KeyNames"/> 에 든 것. <c>;</c> 뒤는 주석. 없으면 null.</summary>
        private static string? IniValues(string path)
        {
            try
            {
                var found = new List<string>();
                string section = "";
                foreach (string raw in File.ReadLines(path))
                {
                    int c = raw.IndexOf(';');
                    string line = (c >= 0 ? raw[..c] : raw).Trim();
                    if (line.Length == 0) continue;

                    if (line[0] == '[' && line[^1] == ']') { section = line[1..^1].Trim(); continue; }

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq].Trim();
                    if (KeyNames.Contains(key))
                        found.Add($"{(section.Length > 0 ? section + "." : "")}{key}={line[(eq + 1)..].Trim()}");
                }
                return found.Count == 0 ? null : string.Join(", ", found);
            }
            catch (Exception ex)
            {
                return $"(해석 실패: {ExceptionText.Summary(ex)})";
            }
        }

        private static void Walk(JsonElement e, string at, List<string> found)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name.StartsWith("_")) continue;   // _comment 류
                        string child = at.Length == 0 ? p.Name : $"{at}.{p.Name}";

                        if (IsLeaf(p.Value))
                        {
                            if (KeyNames.Contains(p.Name)) found.Add($"{child}={Leaf(p.Value)}");
                        }
                        else Walk(p.Value, child, found);
                    }
                    break;

                case JsonValueKind.Array:
                    int i = 0;
                    foreach (var item in e.EnumerateArray())
                    {
                        // 배열 원소는 번호보다 이름표가 읽힌다 — MotionAxisList[T].Home.Mode
                        Walk(item, $"{at}[{Label(item, i)}]", found);
                        i++;
                    }
                    break;
            }
        }

        private static bool IsLeaf(JsonElement e)
            => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);

        private static string Leaf(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Null   => "null",
            _                    => e.GetRawText(),
        };

        /// <summary>배열 원소의 이름표 — AxisNo / Name / Id 가 있으면 그것, 없으면 번호.</summary>
        private static string Label(JsonElement item, int index)
        {
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "AxisNo", "Name", "Id" })
                    if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? index.ToString();
            return index.ToString();
        }

        /// <summary>PCC 가 읽는 cfg — Config 밖에 있을 수 있어 따로 찾는다.</summary>
        private static IEnumerable<string> MeteorCfgLines()
        {
            string path;
            try
            {
                path = PathUtils.ResolveConfigPath(AppSettingsService.Current?.MeteorConfigPath, AppConstants.MeteorConfigFile);
            }
            catch (Exception ex)
            {
                return new[] { $"[CONFIG] Meteor cfg — 경로 해석 실패: {ExceptionText.Summary(ex)}" };
            }

            try
            {
                var cfg = MeteorConfigFile.Load(path);
                if (!cfg.Exists) return new[] { $"[CONFIG] Meteor cfg — 없음: {path}" };

                return new[]
                {
                    FileLine(path).Replace("[CONFIG] ", "[CONFIG] Meteor cfg "),
                    $"[CONFIG]   Meteor cfg: HeadType={cfg.HeadType}, PccType={cfg.PccType}, Adapter1={cfg.EthernetAdapter}, " +
                    $"Xdpi={cfg.Xdpi}, BitsPerPixel={cfg.BitsPerPixel}, WaveformFileIdx={cfg.WaveformFileIdx}, " +
                    $"Yinterlace={cfg.Yinterlace}, 파형 {cfg.Waveforms.Count}개(없는 파일 {cfg.Waveforms.Count(w => !w.Exists)}개)",
                };
            }
            catch (Exception ex)
            {
                return new[] { $"[CONFIG] Meteor cfg — 읽기 실패 ({path}): {ExceptionText.Summary(ex)}" };
            }
        }
    }
}
