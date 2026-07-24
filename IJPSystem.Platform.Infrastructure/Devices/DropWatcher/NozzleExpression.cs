using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// Meteor "Nozzle Select" 화면의 노즐 선택 문법을 노즐 번호 집합으로 해석한다.
    /// (Set Use Nozzle 입력창의 ADD()/DEL() 미니 문법 그대로)
    ///
    /// <b>문법</b> — 문장은 <c>;</c> 로 구분, 앞에서 뒤로 순서대로 적용한다:
    ///   <list type="bullet">
    ///     <item><c>ADD(...)</c> : 집합에 추가</item>
    ///     <item><c>DEL(...)</c> : 집합에서 제거</item>
    ///   </list>
    /// 괄호 안은 <c>,</c> 로 구분한 항목의 나열이며, 각 항목은:
    ///   <list type="bullet">
    ///     <item>단일 번호   — <c>5</c></item>
    ///     <item>범위        — <c>1~100</c></item>
    ///     <item>홀/짝 필터   — <c>ODD:1~100</c>, <c>EVEN:20~40</c></item>
    ///     <item>홀/짝 전체   — <c>ODD</c>, <c>EVEN</c>  (전체 노즐 범위에 적용)</item>
    ///   </list>
    /// 예) <c>ADD(1~100); DEL(40~45)</c> → 1..100 중 40..45 제외.
    ///
    /// <b>주의</b>: 반환값은 화면·레시피와 같은 1-based 실번호 집합이다
    /// (<see cref="SpitSettings.Nozzles"/>, <see cref="NozzleGrid"/> 의 노즐 번호와 동일 기준).
    /// 스핏 패턴 배열의 0-based 컬럼 변환은 <see cref="S800SingleSpitPatternBuilder"/> 가 담당한다.
    /// 이 클래스는 <b>UI 문법 → 번호 집합</b>까지만이며, Meteor API 전달(스핏바 버퍼/SIG_SPIT)은 별개 층이다.
    /// </summary>
    public static class NozzleExpression
    {
        /// <summary>화면 입력창의 노즐 번호 허용 범위(ADD(0~999)).</summary>
        public const int DefaultMinNozzle = 0;
        public const int DefaultMaxNozzle = 999;

        /// <summary>
        /// 노즐 선택식을 오름차순·중복없는 번호 목록으로 해석한다.
        /// </summary>
        /// <param name="expression">예: "ADD(1~100); DEL(40~45)". 빈 문자열이면 빈 목록.</param>
        /// <param name="minNozzle">허용 최소 번호. ODD/EVEN 전체의 하한이기도 하다.</param>
        /// <param name="maxNozzle">허용 최대 번호. ODD/EVEN 전체의 상한이기도 하다.</param>
        /// <exception cref="FormatException">문법 오류나 허용 범위 이탈 시. 메시지에 문제 토큰을 담는다.</exception>
        public static IReadOnlyList<int> Parse(
            string expression, int minNozzle = DefaultMinNozzle, int maxNozzle = DefaultMaxNozzle)
        {
            if (minNozzle > maxNozzle)
                throw new ArgumentException($"minNozzle({minNozzle}) > maxNozzle({maxNozzle})");

            var set = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(expression)) return Array.Empty<int>();

            // 문장(ADD/DEL)을 ';' 로 나눠 순서대로 적용 — DEL 은 그 시점까지 쌓인 집합에서만 뺀다.
            foreach (var raw in expression.Split(';'))
            {
                string stmt = raw.Trim();
                if (stmt.Length == 0) continue;   // 끝의 ';' 나 빈 문장은 무시

                (bool add, string body) = SplitStatement(stmt);

                foreach (var itemRaw in body.Split(','))
                {
                    string item = itemRaw.Trim();
                    if (item.Length == 0) continue;

                    foreach (int n in ExpandItem(item, minNozzle, maxNozzle))
                    {
                        if (add) set.Add(n);
                        else     set.Remove(n);
                    }
                }
            }

            return set.ToArray();
        }

        /// <summary>예외 없이 해석한다. 성공 시 true, 실패 시 <paramref name="error"/> 에 사유.</summary>
        public static bool TryParse(
            string expression, out IReadOnlyList<int> nozzles, out string? error,
            int minNozzle = DefaultMinNozzle, int maxNozzle = DefaultMaxNozzle)
        {
            try
            {
                nozzles = Parse(expression, minNozzle, maxNozzle);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                nozzles = Array.Empty<int>();
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 번호 집합을 화면 문법의 압축 표현으로 되돌린다(연속 구간은 a~b 로 접음). 로그·표시용.
        /// 예: {1,2,3,4,5,10,11} → "ADD(1~5,10~11)". 비어 있으면 "".
        /// </summary>
        public static string Describe(IEnumerable<int> nozzles)
        {
            var list = (nozzles ?? Enumerable.Empty<int>()).Distinct().OrderBy(n => n).ToList();
            if (list.Count == 0) return string.Empty;

            var parts = new List<string>();
            int start = list[0], prev = list[0];
            for (int i = 1; i <= list.Count; i++)
            {
                if (i < list.Count && list[i] == prev + 1) { prev = list[i]; continue; }
                parts.Add(start == prev ? start.ToString(CultureInfo.InvariantCulture)
                                        : $"{start}~{prev}");
                if (i < list.Count) { start = prev = list[i]; }
            }
            return $"ADD({string.Join(",", parts)})";
        }

        // ── 내부 ──────────────────────────────────────────────────────────

        /// <summary>"ADD(...)" / "DEL(...)" 을 (추가여부, 괄호안) 으로 분해.</summary>
        private static (bool add, string body) SplitStatement(string stmt)
        {
            int lp = stmt.IndexOf('(');
            int rp = stmt.LastIndexOf(')');
            if (lp < 0 || rp < 0 || rp < lp)
                throw new FormatException($"'{stmt}' — ADD(...) 또는 DEL(...) 형식이 아닙니다.");

            string op = stmt.Substring(0, lp).Trim();
            bool add = op.Equals("ADD", StringComparison.OrdinalIgnoreCase);
            bool del = op.Equals("DEL", StringComparison.OrdinalIgnoreCase);
            if (!add && !del)
                throw new FormatException($"'{op}' — 알 수 없는 명령입니다. ADD 또는 DEL 만 지원합니다.");

            return (add, stmt.Substring(lp + 1, rp - lp - 1));
        }

        /// <summary>항목 하나(단일/범위/홀짝)를 번호들로 전개하고 허용 범위를 검증한다.</summary>
        private static IEnumerable<int> ExpandItem(string item, int min, int max)
        {
            // 홀/짝 접두 분리 — "ODD:1~100", "EVEN", "ODD"
            int? parity = null;   // 1=홀, 0=짝
            string rest = item;
            int colon = item.IndexOf(':');
            string head = (colon >= 0 ? item.Substring(0, colon) : item).Trim();

            if (head.Equals("ODD", StringComparison.OrdinalIgnoreCase))  parity = 1;
            else if (head.Equals("EVEN", StringComparison.OrdinalIgnoreCase)) parity = 0;

            if (parity != null)
                // "ODD"/"EVEN" 단독이면 전체 범위, "ODD:a~b" 면 뒤쪽 범위에 필터.
                rest = colon >= 0 ? item.Substring(colon + 1).Trim() : string.Empty;
            else if (colon >= 0)
                throw new FormatException($"'{item}' — ':' 앞은 ODD 또는 EVEN 이어야 합니다.");

            int lo, hi;
            if (rest.Length == 0)
            {
                if (parity == null)
                    throw new FormatException($"'{item}' — 빈 항목입니다.");
                (lo, hi) = (min, max);   // 홀/짝 단독 = 전체 범위
            }
            else
            {
                (lo, hi) = ParseRange(rest, item);
            }

            if (lo < min || hi > max)
                throw new FormatException(
                    $"'{item}' — 노즐 번호가 허용 범위({min}~{max})를 벗어났습니다.");

            for (int n = lo; n <= hi; n++)
            {
                if (parity == 1 && (n & 1) == 0) continue;   // 홀수만
                if (parity == 0 && (n & 1) == 1) continue;   // 짝수만
                yield return n;
            }
        }

        /// <summary>"5" → (5,5), "1~100" → (1,100). 역순이면 정규화한다.</summary>
        private static (int lo, int hi) ParseRange(string range, string itemForError)
        {
            int tilde = range.IndexOf('~');
            if (tilde < 0)
            {
                int one = ParseInt(range, itemForError);
                return (one, one);
            }

            int a = ParseInt(range.Substring(0, tilde), itemForError);
            int b = ParseInt(range.Substring(tilde + 1), itemForError);
            return a <= b ? (a, b) : (b, a);
        }

        private static int ParseInt(string s, string itemForError)
        {
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            throw new FormatException($"'{itemForError}' — 숫자가 아닌 값 '{s.Trim()}' 이 있습니다.");
        }
    }
}
