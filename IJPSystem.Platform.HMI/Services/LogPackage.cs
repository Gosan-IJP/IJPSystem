using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace IJPSystem.Platform.HMI.Services
{
    /// <summary>
    /// 원격 분석에 필요한 파일을 zip 하나로 묶는다 — 로그 화면의 [로그 압축].
    ///
    /// <para><b>왜 필요한가</b>: 출하 후 문제가 생기면 고객에게 "로그 폴더, Config 폴더, 알람 DB, PCC 엔진
    /// 로그를 보내 주세요" 라고 해야 했다. 경로도 제각각이고(C:\Logs, 설치 폴더\Config, cfg 가 가리키는 곳)
    /// 하나라도 빠지면 다시 요청해야 한다. 버튼 하나로 같은 묶음이 나오게 한다(2026-09-13).</para>
    ///
    /// <list type="bullet">
    ///   <item><c>logs/</c> — 최근 N일의 텍스트 로그, C:\Logs 의 DB(시스템 로그 등)</item>
    ///   <item><c>config/</c> — Config 폴더의 설정 파일과 DB(레시피·장비 설정·알람 이력)</item>
    ///   <item><c>meteor/</c> — PCC 가 읽는 cfg 와 엔진 로그</item>
    ///   <item><c>info.txt</c> — 호기·빌드·PC·설정 스냅샷(압축을 푼 사람이 제일 먼저 읽는 파일)</item>
    /// </list>
    ///
    /// <para><b>비전 이미지는 넣지 않는다</b> — 로그 폴더 용량의 대부분이라 메일로 못 보낼 크기가 된다.</para>
    ///
    /// <para><b>쓰는 중인 파일도 읽는다</b>: 앱이 돌고 있으면 오늘 로그와 DB 가 열려 있다. 공유 읽기로 연다 —
    /// DB 는 쓰는 도중의 사본이 될 수 있지만, 못 여는 것보다 낫다.</para>
    /// </summary>
    internal static class LogPackage
    {
        /// <summary>한 파일의 상한 — 넘으면 넣지 않고 info.txt 에 적는다(엔진 로그가 수 GB 로 자란 경우).</summary>
        private const long MaxEntryBytes = 300L * 1024 * 1024;

        public sealed record Result(int Files, long Bytes, IReadOnlyList<string> Skipped);

        /// <param name="zipPath">만들 zip 경로. 있으면 덮어쓴다.</param>
        /// <param name="recentDays">텍스트 로그를 며칠치 넣나.</param>
        public static Result Create(string zipPath, int recentDays)
        {
            var skipped = new List<string>();
            int files = 0;
            long bytes = 0;

            using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);

            void Add(string source, string entryName)
            {
                try
                {
                    var fi = new FileInfo(source);
                    if (!fi.Exists) return;
                    if (fi.Length > MaxEntryBytes)
                    {
                        skipped.Add($"{source} — {fi.Length / 1024 / 1024}MB, 상한 {MaxEntryBytes / 1024 / 1024}MB 초과로 뺌");
                        return;
                    }

                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = fi.LastWriteTime;
                    using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                    files++;
                    bytes += fi.Length;
                }
                catch (Exception ex)
                {
                    skipped.Add($"{source} — {ExceptionText.Summary(ex)}");
                }
            }

            // ── logs/ ──
            string logDir = AppConstants.LogFolder;
            if (Directory.Exists(logDir))
            {
                var since = DateTime.Now.AddDays(-Math.Max(1, recentDays));
                foreach (var f in Directory.EnumerateFiles(logDir, "*.txt", SearchOption.TopDirectoryOnly)
                                           .Where(f => File.GetLastWriteTime(f) >= since))
                    Add(f, $"logs/{Path.GetFileName(f)}");
                foreach (var f in Directory.EnumerateFiles(logDir, "*.db", SearchOption.TopDirectoryOnly))
                    Add(f, $"logs/{Path.GetFileName(f)}");
            }

            // ── config/ ──
            string cfgDir = "";
            try { cfgDir = Path.GetDirectoryName(PathUtils.GetConfigPath("AppConfig.json")) ?? ""; } catch { }
            if (Directory.Exists(cfgDir))
            {
                foreach (var pattern in new[] { "*.json", "*.ini", "*.cfg", "*.db" })
                    foreach (var f in Directory.EnumerateFiles(cfgDir, pattern, SearchOption.TopDirectoryOnly))
                        Add(f, $"config/{Path.GetFileName(f)}");
            }

            // ── meteor/ ──
            try
            {
                string cfgPath = PathUtils.ResolveConfigPath(AppSettingsService.Current?.MeteorConfigPath,
                                                             AppConstants.MeteorConfigFile);
                var cfg = MeteorConfigFile.Load(cfgPath);
                if (cfg.Exists)
                {
                    Add(cfgPath, $"meteor/{Path.GetFileName(cfgPath)}");
                    if (!string.IsNullOrEmpty(cfg.LogFilePath))
                        Add(cfg.LogFilePath, $"meteor/{Path.GetFileName(cfg.LogFilePath)}");
                }
            }
            catch (Exception ex)
            {
                skipped.Add($"Meteor cfg — {ExceptionText.Summary(ex)}");
            }

            // ── info.txt ── 마지막에 쓴다 — 무엇이 빠졌는지까지 적으려고.
            var info = zip.CreateEntry("info.txt", CompressionLevel.Optimal);
            using (var w = new StreamWriter(info.Open(), new UTF8Encoding(true)))
                w.Write(Describe(recentDays, files, bytes, skipped));

            return new Result(files, bytes, skipped);
        }

        private static string Describe(int recentDays, int files, long bytes, IReadOnlyList<string> skipped)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.AppendLine(s);

            Line($"IJPSystem 로그 압축 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Line($"호기     : {AppSettingsService.Current?.MachineNoText ?? "호기 미지정"}");
            Line($"빌드     : {BuildInfo.Stamp}");
            Line($"권한     : {(string.IsNullOrEmpty(SessionUser.Role) ? "-" : SessionUser.Role)}");
            Line($"운전 중  : {RunContext.CurrentId ?? "아니오"}");
            Line($"PC       : {Environment.MachineName} · {Environment.OSVersion} · " +
                 $"{(Environment.Is64BitProcess ? "64" : "32")}비트 프로세스");
            Line($"실행 폴더: {AppContext.BaseDirectory}");
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(AppConstants.LogFolder) ?? "C:\\");
                Line($"디스크   : {drive.Name} 여유 {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:F1}GB / " +
                     $"{drive.TotalSize / 1024.0 / 1024 / 1024:F1}GB");
            }
            catch { }
            Line($"담은 파일: {files}개 · {bytes / 1024.0 / 1024:F1}MB (텍스트 로그는 최근 {recentDays}일)");
            Line("");

            Line("── 어셈블리 ──");
            try { foreach (var s in BuildInfo.DescribeLoaded()) Line(s); }
            catch (Exception ex) { Line($"(실패: {ExceptionText.Summary(ex)})"); }
            Line("");

            Line("── 설정 스냅샷 ──");
            try { foreach (var s in ConfigSnapshot.Lines()) Line(s); }
            catch (Exception ex) { Line($"(실패: {ExceptionText.Summary(ex)})"); }
            Line("");

            Line("── 빠진 파일 ──");
            if (skipped.Count == 0) Line("없음");
            foreach (var s in skipped) Line(s);

            return sb.ToString();
        }
    }
}
