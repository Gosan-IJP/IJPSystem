using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace IJPSystem.Platform.HMI.Services
{
    /// <summary>
    /// 레시피를 저장할 때 <b>무엇이 무엇으로 바뀌었나</b>를 뽑는다.
    ///
    /// <para><b>왜 필요한가</b>: "어제까지 잘 되다가 오늘부터 안 된다" 는 신고의 대부분은 누가 레시피를
    /// 고친 것이다. 그런데 저장 로그는 "파라미터 저장 완료" 한 줄뿐이었고, 변경 이력 DB 에도
    /// 내용 칸에 늘 "Parameters Updated by User" 가 박혀 있었다(2026-09-13). 무엇이 바뀌었는지는
    /// 어디에도 남지 않았다.</para>
    ///
    /// <para><b>칸 이름을 적어 두지 않는다</b>: 저장 트랜잭션 안에서 앞뒤로 행을 통째로 읽어 견준다.
    /// 칸을 목록으로 들고 있으면 레시피에 칸이 늘어날 때마다 여기도 고쳐야 하고, 잊으면 그 칸의
    /// 변경만 조용히 빠진다.</para>
    /// </summary>
    internal static class RecipeChangeSet
    {
        /// <summary>로그 한 줄에 적을 변경 수의 상한. 전부는 변경 이력 DB 에 남는다.</summary>
        public const int MaxInLogLine = 30;

        /// <summary>레시피 하나의 저장 값 전체 — <c>칸 → 값</c>. 트랜잭션 안에서 부른다(쓰기 전·후).</summary>
        public static Dictionary<string, string> Read(SqliteConnection db, SqliteTransaction trans, int recipeId)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (db.QueryFirstOrDefault("SELECT * FROM Recipes WHERE Id=@id", new { id = recipeId }, trans)
                is IDictionary<string, object> row)
            {
                foreach (var kv in row)
                    if (kv.Key is not ("Id" or "Name")) map[kv.Key] = Fmt(kv.Value);
            }

            foreach (var r in db.Query("SELECT * FROM RecipeDetails_Motor WHERE RecipeId=@id", new { id = recipeId }, trans))
            {
                if (r is not IDictionary<string, object> m) continue;
                string axis = Fmt(m.TryGetValue("AxisNo", out var a) ? a : null);
                foreach (var kv in m)
                    if (kv.Key is not ("Id" or "RecipeId" or "AxisNo"))
                        map[$"Motor[{axis}].{kv.Key}"] = Fmt(kv.Value);
            }

            foreach (var r in db.Query(
                         "SELECT PointName, AxisName, PosValue, IsUsed FROM RecipeDetails_Position WHERE RecipeId=@id",
                         new { id = recipeId }, trans))
            {
                if (r is not IDictionary<string, object> p) continue;
                bool used = p["IsUsed"] is null or DBNull || Convert.ToInt64(p["IsUsed"], CultureInfo.InvariantCulture) != 0;
                map[$"Point[{Fmt(p["PointName"])}].{Fmt(p["AxisName"])}"] = Fmt(p["PosValue"]) + (used ? "" : " (off)");
            }

            return map;
        }

        /// <summary>바뀐 칸만 <c>칸 옛값→새값</c> 으로. 한쪽에만 있는 칸은 <c>-</c> 로 적는다.</summary>
        public static List<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
        {
            var changes = new List<string>();
            foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                before.TryGetValue(key, out var b);
                after.TryGetValue(key, out var a);
                if (!string.Equals(b, a, StringComparison.Ordinal))
                    changes.Add($"{key} {b ?? "-"}→{a ?? "-"}");
            }
            return changes;
        }

        /// <summary>로그 한 줄 요약 — <c>변경 3건: A 1→2 · B …</c>. 많으면 앞부분만 적고 나머지는 이력 DB 로 미룬다.</summary>
        public static string Summarize(IReadOnlyList<string> changes)
        {
            if (changes.Count == 0) return "변경 없음";

            string head = string.Join(" · ", changes.Take(MaxInLogLine));
            return changes.Count <= MaxInLogLine
                ? $"변경 {changes.Count}건: {head}"
                : $"변경 {changes.Count}건: {head} · … 외 {changes.Count - MaxInLogLine}건(RecipeChangeLogs 참고)";
        }

        // 저장된 실수는 0.1 이 0.1000000001 로 읽힐 수 있다 — 소수 넷째 자리까지만 견준다.
        private static string Fmt(object? v) => v switch
        {
            null or DBNull => "-",
            double d       => d.ToString("0.####", CultureInfo.InvariantCulture),
            float f        => f.ToString("0.####", CultureInfo.InvariantCulture),
            _              => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "-",
        };
    }
}
