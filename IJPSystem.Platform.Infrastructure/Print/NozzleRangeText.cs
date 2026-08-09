using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 노즐 번호 목록을 <b>구간</b>으로 요약한다. <c>1,2,3,…,100</c> 대신 <c>1~100</c>.
    ///
    /// <para>
    /// 800개짜리 헤드에서 콤마 목록은 읽을 수 없는 숫자 벽이 된다 — 어디부터 어디까지 쓰는지,
    /// 무엇이 빠졌는지 눈으로 못 읽는다. 구간으로 접으면 한 줄로 파악되고, 기록으로 남기거나
    /// 다시 입력창에 붙여 넣기도 쉽다.
    /// </para>
    /// </summary>
    public static class NozzleRangeText
    {
        /// <summary>연속 구간으로 접어 <c>"1~100, 150, 200~250"</c> 형태로.</summary>
        /// <param name="numbers">노즐 번호(순서·중복 무관).</param>
        /// <param name="rangeMark">구간 기호. 입력 문법과 같은 <c>~</c> 를 기본으로 둔다.</param>
        public static string Summarize(IEnumerable<int>? numbers, string rangeMark = "~")
            => Format(Ranges(numbers), rangeMark);

        /// <summary>연속 구간 목록. 화면에서 구간 단위로 다룰 때 쓴다.</summary>
        public static IReadOnlyList<(int From, int To)> Ranges(IEnumerable<int>? numbers)
        {
            // 항상 정렬·중복제거부터 — 호출자가 정렬해서 줬으리라 믿으면, 안 그런 한 번에
            // 구간이 잘게 쪼개져 조용히 틀린 요약이 나온다.
            var sorted = Normalize(numbers);
            var result = new List<(int, int)>();
            if (sorted.Count == 0) return result;

            int from = sorted[0], prev = sorted[0];
            for (int i = 1; i < sorted.Count; i++)
            {
                int v = sorted[i];
                if (v == prev + 1) { prev = v; continue; }
                result.Add((from, prev));
                from = prev = v;
            }
            result.Add((from, prev));
            return result;
        }

        /// <summary>
        /// 요약을 한 줄에 다 못 넣을 때 앞부분만 보이고 나머지는 개수로. 상태줄·툴팁용.
        /// </summary>
        public static string Summarize(IEnumerable<int>? numbers, int maxRanges, string rangeMark = "~")
        {
            var all = Ranges(numbers);
            if (maxRanges <= 0 || all.Count <= maxRanges) return Format(all, rangeMark);

            return $"{Format(all.Take(maxRanges).ToList(), rangeMark)} … (구간 {all.Count - maxRanges}개 더)";
        }

        private static string Format(IReadOnlyList<(int From, int To)> ranges, string rangeMark)
        {
            var sb = new StringBuilder();
            foreach (var (from, to) in ranges)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(from.ToString(CultureInfo.InvariantCulture));
                // 붙어 있는 번호는 개수와 무관하게 항상 구간으로 — 읽는 규칙을 하나로 유지한다.
                if (to != from) sb.Append(rangeMark).Append(to.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<int> Normalize(IEnumerable<int>? numbers) =>
            numbers == null ? new List<int>() : numbers.Distinct().OrderBy(n => n).ToList();
    }
}
