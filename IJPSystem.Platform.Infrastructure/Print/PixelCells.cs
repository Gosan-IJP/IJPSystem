using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 화면 좌표를 <b>이미지 픽셀 칸</b>으로 바꾸는 계산.
    ///
    /// <para>
    /// 편집 캔버스는 실제 이미지(예: 4724×4724px)를 760px 로 줄여 보여 준다. 그 위에서 그은 선을
    /// 그대로 저장하면 어느 픽셀이 켜지는지는 확대 배율에 딸린 우연이 된다. 패턴은 "이 픽셀을
    /// 쏜다/안 쏜다"가 전부이므로, 그리는 순간에 칸으로 확정해야 한다.
    /// </para>
    /// <para>
    /// WPF 를 쓰지 않는다 — 이 계산이 맞는지는 화면 없이 확인할 수 있어야 한다.
    /// </para>
    /// </summary>
    public static class PixelCells
    {
        /// <summary>점 (x,y) 가 속한 칸. 캔버스 밖이면 음수/초과 값이 그대로 나온다(거르는 것은 호출부).</summary>
        public static (int X, int Y) At(double x, double y, double cellW, double cellH)
        {
            if (cellW <= 0 || cellH <= 0) throw new ArgumentOutOfRangeException(nameof(cellW), "칸 크기가 0 이하다.");
            return ((int)Math.Floor(x / cellW), (int)Math.Floor(y / cellH));
        }

        /// <summary>
        /// 칸 (cx,cy) 를 중심으로 <paramref name="size"/>×<paramref name="size"/> 붓이 덮는 칸들.
        /// 이미지 밖은 빼고 돌려준다.
        /// </summary>
        public static IEnumerable<(int X, int Y)> Brush(int cx, int cy, int size, int width, int height)
        {
            int n = Math.Max(1, size);
            int half = n / 2;                    // 짝수 붓은 오른쪽·아래로 한 칸 치우친다 — 픽셀 편집의 관례
            for (int dy = 0; dy < n; dy++)
                for (int dx = 0; dx < n; dx++)
                {
                    int x = cx - half + dx, y = cy - half + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    yield return (x, y);
                }
        }

        /// <summary>
        /// (x0,y0)→(x1,y1) 를 붓으로 훑어 켜지는 칸들. 같은 칸은 한 번만 나온다.
        ///
        /// <para>
        /// 반 칸 간격으로 훑는다. 마우스 이동 이벤트는 띄엄띄엄 오기 때문에 끝점만 찍으면
        /// 빠르게 그을 때 선이 점선이 된다.
        /// </para>
        /// </summary>
        /// <param name="seen">여러 번 호출에 걸쳐 중복을 막을 집합. null 이면 이번 호출 안에서만 막는다.</param>
        public static IReadOnlyList<(int X, int Y)> Stroke(
            double x0, double y0, double x1, double y1,
            double cellW, double cellH, int size, int width, int height,
            ISet<long>? seen = null)
        {
            if (cellW <= 0 || cellH <= 0) throw new ArgumentOutOfRangeException(nameof(cellW), "칸 크기가 0 이하다.");

            seen ??= new HashSet<long>();
            var result = new List<(int X, int Y)>();

            double step = Math.Max(cellW, cellH) / 2;
            double dist = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int n = Math.Max(1, (int)Math.Ceiling(dist / step));

            for (int i = 0; i <= n; i++)
            {
                double x = x0 + (x1 - x0) * i / n;
                double y = y0 + (y1 - y0) * i / n;
                var (cx, cy) = At(x, y, cellW, cellH);

                foreach (var c in Brush(cx, cy, size, width, height))
                    if (seen.Add((long)c.Y * width + c.X))
                        result.Add(c);
            }
            return result;
        }

        /// <summary>칸의 왼쪽 위 모서리 캔버스 좌표. 그린 사각형이 픽셀 경계에 정확히 앉는다.</summary>
        public static (double X, double Y) Origin(int cx, int cy, double cellW, double cellH)
            => (cx * cellW, cy * cellH);
    }
}
