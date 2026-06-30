using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// 노즐 선택 DSL 파서 (이미지 도움말 문법 구현).
    ///   문법    : ADD(...), DEL(...)
    ///   지원함수: ODD, EVEN   (ODD:1~100 처럼 범위 한정 가능, 단독이면 전체 범위)
    ///   범위    : a~b  (a-b 도 허용)
    ///   구분자  : 명령 사이 ';' , 항목 사이 ','
    ///
    /// 예) "ADD(1~100); DEL(40~45)"  → 1..100 에서 40..45 제외
    ///     "ADD(ODD:1~100)"           → 1..100 의 홀수
    ///     "ADD(EVEN)"                → 전체 범위의 짝수
    ///     "ADD(1,2,3,5~10)"          → 1,2,3,5,6,7,8,9,10
    /// ADD/DEL 래퍼 없이 들어오면 전체를 ADD 로 간주(예: "1,2,5~10").
    /// 명령은 입력 순서대로 ADD(합집합)/DEL(차집합) 적용된다.
    /// </summary>
    public static class NozzleParser
    {
        /// <summary>
        /// 노즐 지정 문자열을 정렬·중복제거된 노즐 번호 리스트로 변환한다.
        /// </summary>
        /// <param name="input">예: "ADD(1~100); DEL(40~45)"</param>
        /// <param name="minNozzle">유효 최소 노즐 번호(포함)</param>
        /// <param name="maxNozzle">유효 최대 노즐 번호(포함)</param>
        /// <param name="invalidTokens">해석 불가/범위 밖 토큰 목록(출력)</param>
        public static List<int> Parse(string input, int minNozzle, int maxNozzle,
            out List<string> invalidTokens)
        {
            invalidTokens = new List<string>();
            var selected = new SortedSet<int>();

            if (string.IsNullOrWhiteSpace(input))
                return new List<int>();

            // 명령 분해 (';')
            foreach (string rawCmd in input.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string cmd = rawCmd.Trim();
                if (cmd.Length == 0) continue;

                bool isDel;
                string inner;

                if (TryExtractFunc(cmd, "ADD", out inner)) isDel = false;
                else if (TryExtractFunc(cmd, "DEL", out inner)) isDel = true;
                else { isDel = false; inner = cmd; } // 래퍼 없음 → 암묵적 ADD

                var nums = ParseSelection(inner, minNozzle, maxNozzle, invalidTokens);
                if (isDel) selected.ExceptWith(nums);
                else       selected.UnionWith(nums);
            }

            return selected.ToList();
        }

        /// <summary>"NAME( ... )" 형태에서 괄호 안 내용을 추출(대소문자 무시).</summary>
        private static bool TryExtractFunc(string cmd, string name, out string inner)
        {
            inner = "";
            if (!cmd.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = cmd.Substring(name.Length).TrimStart();
            if (rest.Length == 0 || rest[0] != '(') return false;
            int close = rest.LastIndexOf(')');
            inner = close < 0 ? rest.Substring(1) : rest.Substring(1, close - 1); // 닫는 괄호 누락 허용
            return true;
        }

        /// <summary>괄호 안 항목들(',' 구분)을 노즐 번호 집합으로 파싱.</summary>
        private static IEnumerable<int> ParseSelection(string selection, int min, int max, List<string> invalid)
        {
            var set = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(selection)) return set;

            foreach (string rawItem in selection.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string item = rawItem.Trim();
                if (item.Length == 0) continue;

                // ODD/EVEN/ALL 필터 판정 (예: "ODD:1~100", "EVEN", "ALL")
                Func<int, bool>? filter = null;
                int colon = item.IndexOf(':');
                string head = (colon >= 0 ? item.Substring(0, colon) : item).Trim();
                string body = colon >= 0 ? item.Substring(colon + 1).Trim() : item;

                if (head.Equals("ODD", StringComparison.OrdinalIgnoreCase))
                { filter = n => (n & 1) == 1; if (colon < 0) body = ""; }
                else if (head.Equals("EVEN", StringComparison.OrdinalIgnoreCase))
                { filter = n => (n & 1) == 0; if (colon < 0) body = ""; }
                else if (head.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                { filter = _ => true; body = ""; }

                int lo, hi;
                if (body.Length == 0)
                {
                    // 필터만 (ODD / EVEN / ALL) → 전체 범위
                    if (filter == null) { invalid.Add(item); continue; }
                    lo = min; hi = max;
                }
                else if (!TryParseRange(body, out lo, out hi))
                {
                    invalid.Add(item);
                    continue;
                }

                int a = Math.Min(lo, hi), b = Math.Max(lo, hi);
                bool any = false;
                for (int n = a; n <= b; n++)
                {
                    if (n < min || n > max) continue;
                    if (filter != null && !filter(n)) continue;
                    set.Add(n);
                    any = true;
                }
                if (!any) invalid.Add(item);
            }
            return set;
        }

        /// <summary>"a~b"/"a-b" 범위 또는 단일 숫자 파싱.</summary>
        private static bool TryParseRange(string body, out int lo, out int hi)
        {
            lo = hi = 0;
            char sep = body.Contains('~') ? '~' : (body.Contains('-') ? '-' : '\0');
            if (sep == '\0')
            {
                if (int.TryParse(body, out int single)) { lo = hi = single; return true; }
                return false;
            }
            string[] parts = body.Split(sep);
            return parts.Length == 2
                && int.TryParse(parts[0].Trim(), out lo)
                && int.TryParse(parts[1].Trim(), out hi);
        }
    }
}
