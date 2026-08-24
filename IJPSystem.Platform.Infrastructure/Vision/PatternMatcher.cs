using System;
using OpenCvSharp;

namespace IJPSystem.Platform.Infrastructure.Vision
{
    /// <summary>8비트 그레이 이미지 한 장. 행 우선, 여백(stride) 없음.</summary>
    public sealed class GrayImage
    {
        public GrayImage(byte[] pixels, int width, int height)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (pixels.Length < (long)width * height)
                throw new ArgumentException("픽셀 수가 폭×높이보다 적습니다.", nameof(pixels));

            Pixels = pixels;
            Width  = width;
            Height = height;
        }

        public byte[] Pixels { get; }
        public int    Width  { get; }
        public int    Height { get; }

        /// <summary>일부 영역만 잘라낸 새 이미지.</summary>
        public GrayImage Crop(int x, int y, int w, int h)
        {
            x = Math.Clamp(x, 0, Width  - 1);
            y = Math.Clamp(y, 0, Height - 1);
            w = Math.Clamp(w, 1, Width  - x);
            h = Math.Clamp(h, 1, Height - y);

            var outPix = new byte[w * h];
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(Pixels, (y + row) * Width + x, outPix, row * w, w);

            return new GrayImage(outPix, w, h);
        }
    }

    /// <summary>찾기 조건.</summary>
    public sealed class PatternSearchOptions
    {
        /// <summary>이 점수 미만이면 못 찾은 것으로 본다. 정규화 상관계수(-1~1).</summary>
        public double MinScore { get; set; } = 0.70;

        /// <summary>
        /// 검색 범위를 기준 위치 주변으로 좁힌다(픽셀 반경). 0 이면 화면 전체.
        ///
        /// <para>좁히면 빨라지는 것보다 <b>엉뚱한 곳을 잡지 않는 것</b>이 크다 —
        /// 반복 패턴(점 격자)에서는 어디를 잡아도 점수가 비슷하게 나온다.</para>
        /// </summary>
        public int SearchRadiusPx { get; set; }

        /// <summary>검색을 시작할 기준 위치(중심, 픽셀). <see cref="SearchRadiusPx"/> 가 0 이면 무시.</summary>
        public double ExpectedX { get; set; }
        public double ExpectedY { get; set; }

        /// <summary>피라미드 단계. 0 이면 축소 없이 원본에서만 찾는다.</summary>
        public int PyramidLevels { get; set; } = 3;
    }

    /// <summary>찾기 결과. 좌표는 <b>장면 이미지의 픽셀</b>이며 패턴의 중심이다.</summary>
    public readonly record struct PatternMatch(
        bool Found, double Score, double CenterX, double CenterY)
    {
        public static PatternMatch Fail(double score = 0) => new(false, score, 0, 0);
    }

    /// <summary>
    /// 정규화 상관(NCC) 패턴 매칭.
    ///
    /// <para><b>왜 NCC 인가</b>: 조명이 밝아지거나 어두워져도 점수가 유지된다(밝기·대비에 정규화).
    /// 글라스 화면은 좌우 밝기 차가 큰데, 차 영상 방식은 그 기울기에 그대로 끌려간다.
    /// 그리고 결정적이다 — 같은 이미지면 언제나 같은 답이 나와 현장에서 재현이 된다.</para>
    ///
    /// <para><b>한계</b>: 회전에 약하다. ±1° 정도는 버티지만 그 이상 틀어지면 점수가 떨어진다.
    /// 각도 보정이 필요해지면 각도 스윕을 얹어야 한다(지금은 넣지 않았다).</para>
    ///
    /// <para>거친 탐색(축소본) → 정밀 탐색(원본 일부) → 서브픽셀 보간의 3단이다.
    /// 축소본에서만 찾고 끝내면 위치가 2^n 픽셀 단위로 튄다.</para>
    /// </summary>
    public static class PatternMatcher
    {
        /// <summary>축소를 멈추는 최소 패턴 변 길이. 더 줄이면 형상이 뭉개져 엉뚱한 곳을 잡는다.</summary>
        private const int MinTemplateSide = 16;

        /// <summary>정밀 탐색 창의 여유. 축소 단계에서 생긴 오차(±2^n)에 조금 더 둔다.</summary>
        private const int RefinePad = 4;

        public static PatternMatch Find(GrayImage scene, GrayImage template, PatternSearchOptions? options = null)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (template == null) throw new ArgumentNullException(nameof(template));

            var opt = options ?? new PatternSearchOptions();

            // 패턴이 장면보다 크면 찾을 수 없다. 예외 대신 '못 찾음' — 화면에서 고른 ROI 가
            // 다음 프레임보다 클 수 있고, 그때 화면이 죽으면 안 된다.
            if (template.Width > scene.Width || template.Height > scene.Height) return PatternMatch.Fail();

            // 검색 범위를 좁힌다. 잘라낸 만큼은 마지막에 다시 더해 준다.
            int offX = 0, offY = 0;
            GrayImage area = scene;
            if (opt.SearchRadiusPx > 0)
            {
                int r  = opt.SearchRadiusPx;
                int x0 = (int)Math.Round(opt.ExpectedX) - template.Width  / 2 - r;
                int y0 = (int)Math.Round(opt.ExpectedY) - template.Height / 2 - r;
                int w  = template.Width  + r * 2;
                int h  = template.Height + r * 2;

                offX = Math.Clamp(x0, 0, Math.Max(0, scene.Width  - 1));
                offY = Math.Clamp(y0, 0, Math.Max(0, scene.Height - 1));
                area = scene.Crop(offX, offY, w, h);

                if (template.Width > area.Width || template.Height > area.Height) return PatternMatch.Fail();
            }

            using var sceneMat = ToMat(area);
            using var templMat = ToMat(template);

            int levels = ResolveLevels(opt.PyramidLevels, templMat, sceneMat);

            // ① 거친 탐색 — 축소본에서 대략의 위치를 잡는다.
            Point coarse;
            if (levels > 0)
            {
                using var smallScene = Shrink(sceneMat, levels);
                using var smallTempl = Shrink(templMat, levels);

                if (smallTempl.Width > smallScene.Width || smallTempl.Height > smallScene.Height)
                    return FullSearch(sceneMat, templMat, opt, offX, offY, template);

                var p = PeakOf(smallScene, smallTempl, out _);
                int f = 1 << levels;
                coarse = new Point(p.X * f, p.Y * f);
            }
            else
            {
                return FullSearch(sceneMat, templMat, opt, offX, offY, template);
            }

            // ② 정밀 탐색 — 거친 위치 주변만 원본 해상도로 다시 본다.
            int pad  = (1 << levels) + RefinePad;
            int rx   = Math.Clamp(coarse.X - pad, 0, Math.Max(0, sceneMat.Width  - templMat.Width));
            int ry   = Math.Clamp(coarse.Y - pad, 0, Math.Max(0, sceneMat.Height - templMat.Height));
            int rw   = Math.Min(templMat.Width  + pad * 2, sceneMat.Width  - rx);
            int rh   = Math.Min(templMat.Height + pad * 2, sceneMat.Height - ry);

            if (rw < templMat.Width || rh < templMat.Height)
                return FullSearch(sceneMat, templMat, opt, offX, offY, template);

            using var window = new Mat(sceneMat, new Rect(rx, ry, rw, rh));
            var peak = PeakOf(window, templMat, out double score, out double subX, out double subY);

            return Build(score, rx + peak.X + subX, ry + peak.Y + subY, offX, offY, template, opt);
        }

        // ── 내부 ─────────────────────────────────────────────────────────

        private static PatternMatch FullSearch(Mat sceneMat, Mat templMat, PatternSearchOptions opt,
                                               int offX, int offY, GrayImage template)
        {
            var peak = PeakOf(sceneMat, templMat, out double score, out double subX, out double subY);
            return Build(score, peak.X + subX, peak.Y + subY, offX, offY, template, opt);
        }

        private static PatternMatch Build(double score, double left, double top,
                                          int offX, int offY, GrayImage template, PatternSearchOptions opt)
        {
            double cx = offX + left + (template.Width  - 1) / 2.0;
            double cy = offY + top  + (template.Height - 1) / 2.0;

            return new PatternMatch(score >= opt.MinScore, score, cx, cy);
        }

        /// <summary>패턴이 너무 작아지지 않는 선까지만 축소한다.</summary>
        private static int ResolveLevels(int requested, Mat templ, Mat scene)
        {
            int levels = Math.Max(0, requested);
            while (levels > 0)
            {
                int f = 1 << levels;
                if (templ.Width  / f >= MinTemplateSide &&
                    templ.Height / f >= MinTemplateSide &&
                    scene.Width  / f >  templ.Width  / f &&
                    scene.Height / f >  templ.Height / f) break;
                levels--;
            }
            return levels;
        }

        private static Mat Shrink(Mat src, int levels)
        {
            var cur = src.Clone();
            for (int i = 0; i < levels; i++)
            {
                var next = new Mat();
                Cv2.PyrDown(cur, next);
                cur.Dispose();
                cur = next;
            }
            return cur;
        }

        private static Point PeakOf(Mat scene, Mat templ, out double score)
            => PeakOf(scene, templ, out score, out _, out _);

        /// <summary>
        /// 상관 지도의 최고점. <paramref name="subX"/>/<paramref name="subY"/> 는
        /// 최고점 좌우 값으로 포물선을 맞춰 얻은 서브픽셀 보정이다 —
        /// 정렬 오차를 1픽셀 단위로만 알면 보정이 그만큼 거칠어진다.
        /// </summary>
        private static Point PeakOf(Mat scene, Mat templ, out double score, out double subX, out double subY)
        {
            using var map = new Mat();
            Cv2.MatchTemplate(scene, templ, map, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(map, out _, out double max, out _, out Point loc);

            score = max;
            subX  = Parabola(At(map, loc.X - 1, loc.Y), max, At(map, loc.X + 1, loc.Y));
            subY  = Parabola(At(map, loc.X, loc.Y - 1), max, At(map, loc.X, loc.Y + 1));
            return loc;
        }

        private static double At(Mat map, int x, int y)
            => x < 0 || y < 0 || x >= map.Width || y >= map.Height ? double.NaN : map.At<float>(y, x);

        /// <summary>세 점으로 꼭짓점 위치를 구한다. 가장자리라 옆 값이 없으면 보정하지 않는다.</summary>
        private static double Parabola(double left, double center, double right)
        {
            if (double.IsNaN(left) || double.IsNaN(right)) return 0;

            double denom = left - 2 * center + right;
            if (Math.Abs(denom) < 1e-12) return 0;

            return Math.Clamp(0.5 * (left - right) / denom, -1, 1);
        }

        private static Mat ToMat(GrayImage img)
        {
            var m = new Mat(img.Height, img.Width, MatType.CV_8UC1);
            System.Runtime.InteropServices.Marshal.Copy(img.Pixels, 0, m.Data, img.Width * img.Height);
            return m;
        }
    }
}
