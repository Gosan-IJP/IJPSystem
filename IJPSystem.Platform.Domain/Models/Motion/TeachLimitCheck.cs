using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Domain.Models.Motion
{
    /// <summary>
    /// 티칭 좌표를 레시피에 저장하기 전 범위 검사.
    ///
    /// <para>
    /// <b>왜 이동이 아니라 저장을 막는가</b>: 조그·수동이동까지 막으면 정비나 복구 때 범위 밖으로
    /// 나갈 수가 없다. 실제로 위험한 것은 그 값이 <b>공정 좌표로 굳어지는 순간</b>이라,
    /// 레시피에 들어갈 때만 막는다. 범위는 MotorConfig.json 의 축별
    /// <see cref="AxisDeviceInfo.TeachLimit"/> 이 정한다.
    /// </para>
    /// <para>
    /// 저장 경로가 둘(레시피 화면 · 위치 티칭 화면)이라 여기 한 곳에 둔다 —
    /// 한쪽에만 넣으면 다른 화면으로 저장해 그대로 빠져나간다.
    /// </para>
    /// </summary>
    public static class TeachLimitCheck
    {
        /// <summary>범위를 벗어난 항목 하나.</summary>
        public readonly struct Violation
        {
            public Violation(string pointName, AxisDeviceInfo axis, double value)
            {
                PointName = pointName;
                Axis = axis;
                Value = value;
            }

            public string PointName { get; }
            public AxisDeviceInfo Axis { get; }
            public double Value { get; }

            public override string ToString() =>
                $"{PointName} · {Axis.Name} = {Value:0.###}{Axis.Unit}  (허용 {Axis.TeachLimitText})";
        }

        /// <summary>
        /// 범위를 벗어난 티칭 값을 모두 찾는다. 없으면 빈 목록.
        /// 사용 안 함(<c>AxisUsed=false</c>)으로 꺼 둔 축은 검사하지 않는다 — 저장은 되지만 쓰이지 않는 값이다.
        /// </summary>
        /// <param name="points">(포인트 이름, 축이름→값, 축이름→사용여부) 형태의 티칭 포인트들.</param>
        /// <param name="axes">축 설정. Positions 의 키는 축 <see cref="AxisDeviceInfo.Name"/> 이다.</param>
        public static IReadOnlyList<Violation> Find(
            IEnumerable<(string PointName, IReadOnlyDictionary<string, double> Positions,
                         IReadOnlyDictionary<string, bool>? AxisUsed)> points,
            IEnumerable<AxisDeviceInfo> axes)
        {
            var bad = new List<Violation>();

            // 범위가 걸린 축만 본다. 대부분의 축은 제한이 없어 매 저장마다 훑을 이유가 없다.
            var limited = new Dictionary<string, AxisDeviceInfo>();
            foreach (var a in axes.Where(a => a.TeachLimit != null))
                limited[a.Name] = a;
            if (limited.Count == 0) return bad;

            foreach (var (pointName, positions, axisUsed) in points)
            {
                if (positions == null) continue;
                foreach (var kv in positions)
                {
                    if (!limited.TryGetValue(kv.Key, out var axis)) continue;
                    if (axisUsed != null && axisUsed.TryGetValue(kv.Key, out bool used) && !used) continue;
                    if (axis.IsWithinTeachLimit(kv.Value)) continue;
                    bad.Add(new Violation(pointName, axis, kv.Value));
                }
            }
            return bad;
        }

        /// <summary>경고창에 띄울 문구. 항목이 많아도 창이 화면을 넘지 않게 앞부분만 적는다.</summary>
        public static string Message(IReadOnlyList<Violation> violations, bool english, int maxLines = 8)
        {
            string head = english
                ? "The following taught positions are out of the allowed range and cannot be saved:"
                : "아래 티칭 값이 허용 범위를 벗어나 저장할 수 없습니다:";

            string body = string.Join("\n", violations.Take(maxLines).Select(v => "  • " + v));
            if (violations.Count > maxLines)
                body += english
                    ? $"\n  … and {violations.Count - maxLines} more"
                    : $"\n  … 외 {violations.Count - maxLines}건";

            return head + "\n\n" + body;
        }
    }
}
