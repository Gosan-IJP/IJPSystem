using System;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 하프톤(디더링) — 연속 농담(0~255)을 헤드가 낼 수 있는 <b>몇 단계 방울</b>로 바꾼다.
    /// (LabVIEW "IMG Dithering" 대응)
    ///
    /// <para>
    /// 헤드는 임의 농도를 못 낸다. 방울 크기가 몇 단계뿐이라, 중간 농도는 "어디에 찍고 어디를
    /// 비울지"로 흉내 낸다. 그래서 <b>반올림이 아니라 오차 확산</b>이어야 한다 — 반올림하면
    /// 잘려나간 농도가 그냥 사라져 밝은 면이 통째로 비고 어두운 면이 뭉갠다.
    /// </para>
    /// <para>
    /// Floyd–Steinberg 로 남은 오차를 이웃 픽셀에 넘긴다. 넘기는 방향을 줄마다 뒤집는
    /// (serpentine) 이유는, 한 방향으로만 밀면 오차가 한쪽으로 흘러 사선 줄무늬가 생기기 때문이다.
    /// </para>
    /// </summary>
    public static class Halftone
    {
        /// <summary>
        /// 오차 확산으로 <paramref name="levels"/> 단계로 낮춘다.
        /// 결과값은 <b>단계 번호</b>(0 ~ levels-1)다 — 0 은 안 쏨.
        /// </summary>
        /// <param name="gray">[row, col] 원본 농담. 값이 클수록 진하다(잉크 많이).</param>
        /// <param name="levels">방울 단계 수(2 이상). 2 면 흑백, 4 면 0~3 단계.</param>
        public static byte[,] ErrorDiffuse(byte[,] gray, int levels)
        {
            if (gray == null) throw new ArgumentNullException(nameof(gray));
            if (levels < 2) throw new ArgumentOutOfRangeException(nameof(levels), "단계는 2 이상이어야 한다.");

            int h = gray.GetLength(0), w = gray.GetLength(1);
            var outLv = new byte[h, w];
            if (h == 0 || w == 0) return outLv;

            // 오차를 담을 실수 버퍼. byte 로 누적하면 오차가 잘려 확산이 되지 않는다.
            var buf = new double[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    buf[y, x] = gray[y, x];

            int    maxLevel = levels - 1;
            double step     = 255.0 / maxLevel;   // 단계 사이의 농도 간격

            for (int y = 0; y < h; y++)
            {
                bool leftward = (y & 1) == 1;                     // 홀수 줄은 반대 방향(serpentine)
                for (int k = 0; k < w; k++)
                {
                    int x = leftward ? w - 1 - k : k;

                    double v  = buf[y, x];
                    int    lv = (int)Math.Round(Math.Clamp(v, 0, 255) / step);
                    lv = Math.Clamp(lv, 0, maxLevel);
                    outLv[y, x] = (byte)lv;

                    double err = v - lv * step;
                    if (err == 0) continue;

                    // Floyd–Steinberg 가중치 7/5/3/1 (합 16). 진행 방향에 맞춰 좌우를 뒤집는다.
                    int dx = leftward ? -1 : 1;
                    Add(buf, y,     x + dx,     err * 7 / 16.0);
                    Add(buf, y + 1, x - dx,     err * 3 / 16.0);
                    Add(buf, y + 1, x,          err * 5 / 16.0);
                    Add(buf, y + 1, x + dx,     err * 1 / 16.0);
                }
            }
            return outLv;
        }

        private static void Add(double[,] buf, int y, int x, double v)
        {
            if (y < 0 || y >= buf.GetLength(0) || x < 0 || x >= buf.GetLength(1)) return;
            buf[y, x] += v;
        }
    }
}
