using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>
    /// 차트(LiveCharts/Skia) 축 글자용 글꼴.
    ///
    /// <para>
    /// <b>왜 파일에서 직접 읽는가</b>: 제어 PC 는 Skia 의 글꼴 <b>조회</b>가 깨져 있다 — 격자선은
    /// 그려지는데 축 숫자만 통째로 안 나온다(2026-08-07 확인). WPF/DirectWrite 는 멀쩡해서
    /// 화면의 다른 한글은 정상이므로, 깨진 것은 OS 글꼴이 아니라 Skia 의 시스템 글꼴 관리자다.
    /// </para>
    /// <para>
    /// <b>★기본은 꺼져 있다(ChartFontFile="none").</b> 2026-07-23 에
    /// <c>SKTypeface.FromFamilyName("맑은 고딕")</c> 로 지정했다가 제어 PC 첫 렌더에서 네이티브
    /// 즉사했고, 2026-08-07 에 그것이 글꼴 <b>조회</b>의 문제라고 보고 <see cref="SKTypeface.FromFile"/>
    /// 로 파일 바이트를 직접 읽어 관리자를 건너뛰어 봤지만 <b>드랍와처 화면에서 똑같이 죽었다</b>.
    /// 즉 깨진 것은 글꼴 조회가 아니라 그 PC 의 <b>Skia 텍스트 렌더 경로 자체</b>이고, 글꼴을
    /// 어떻게 주든 지정하는 순간 죽는다. 축 글자를 되살리려면 차트를 WPF(DirectWrite)로
    /// 직접 그리는 수밖에 없다 — 눈금자(ImageScaleRuler)가 그 방식으로 잘 동작한다.
    /// </para>
    /// <para>
    /// AppConfig.json 의 <c>ChartFontFile</c> 에 파일명/경로를 넣으면 다시 시도한다(다른 PC 나
    /// OS 수리 후 검증용). 해석 실패는 조용히 null 로 떨어져 글꼴 미지정이 된다.
    /// </para>
    /// </summary>
    public static class ChartFont
    {
        // 우선순위: 한글이 필요한 축 제목이 있으므로 한글 글꼴을 먼저 본다.
        private static readonly string[] Fallbacks = { "malgun.ttf", "segoeui.ttf", "arial.ttf", "tahoma.ttf" };

        private static string _preferred = "";
        private static bool _resolved;
        private static SKTypeface? _typeface;

        /// <summary>앱 시작 시 1회. 빈 값이면 자동 탐색, "none" 이면 글꼴 지정을 하지 않는다.</summary>
        public static void Configure(string? fileOrPath)
        {
            _preferred = fileOrPath ?? "";
            _resolved  = false;
            _typeface  = null;
        }

        /// <summary>해석된 글꼴. 실패했거나 꺼져 있으면 null.</summary>
        public static SKTypeface? Typeface
        {
            get
            {
                if (_resolved) return _typeface;
                _resolved = true;
                _typeface = Resolve();
                return _typeface;
            }
        }

        /// <summary>어떤 글꼴을 쓰게 됐는지(로그용).</summary>
        public static string Description { get; private set; } = "미해석";

        private static SKTypeface? Resolve()
        {
            // 값을 명시했을 때만 켠다. 빈 값도 끈 것으로 본다 — 켜는 쪽이 기본이면 설정 파일에
            // 키가 없거나 비어 있는 현장에서 의도치 않게 켜지고, 그러면 차트 첫 렌더에서 앱이 죽는다.
            if (string.IsNullOrWhiteSpace(_preferred) ||
                string.Equals(_preferred, "none", StringComparison.OrdinalIgnoreCase))
            {
                Description = "사용 안 함 — 축 글자 없음(제어 PC Skia 텍스트 경로 손상)";
                return null;
            }

            foreach (string path in Candidates())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var tf = SKTypeface.FromFile(path);
                    if (tf == null) continue;
                    Description = path;
                    return tf;
                }
                catch { /* 이 글꼴은 건너뛴다 — 다음 후보로 */ }
            }

            Description = "찾지 못함 — 축 글자가 안 보일 수 있음";
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            string fontsDir;
            try { fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
            catch { fontsDir = @"C:\Windows\Fonts"; }

            if (!string.IsNullOrWhiteSpace(_preferred))
            {
                // 절대경로면 그대로, 파일명만 주면 글꼴 폴더에서 찾는다.
                yield return Path.IsPathRooted(_preferred) ? _preferred : Path.Combine(fontsDir, _preferred);
            }

            foreach (string name in Fallbacks) yield return Path.Combine(fontsDir, name);
        }
    }
}
