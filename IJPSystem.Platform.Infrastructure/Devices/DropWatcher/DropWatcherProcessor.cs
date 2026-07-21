using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using IJPSystem.Platform.Domain.Models.Vision;
using OpenCvSharp;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 드랍와쳐 액적 측정 파라미터(사이트 캘리브레이션).
    /// ※ MicronsPerPixel 은 반드시 실장에서 교정할 것 — 부피/직경/속도의 절대값이 여기에 비례한다.
    /// </summary>
    public sealed class DropWatcherProcessorConfig
    {
        /// <summary>픽셀→물리 스케일(µm/px). 노즐 피치·치수 지그 등 기준물로 실장 교정. (기본은 임시값)</summary>
        public double MicronsPerPixel { get; set; } = 5.0;

        /// <summary>백라이트 실루엣(어두운 액적 위 밝은 배경) 여부. true 면 어두운 액적을 검출.</summary>
        public bool DropletsAreDark { get; set; } = true;

        /// <summary>이진화 임계값. 0 이하면 Otsu 자동 임계.</summary>
        public double ThresholdValue { get; set; } = 0;

        /// <summary>잡음/오검 제거용 면적 하한(px²). 이보다 작은 blob 은 무시.</summary>
        public double MinAreaPx { get; set; } = 12;

        /// <summary>면적 상한(px²). 이보다 크면(반사/그림자 등) 무시.</summary>
        public double MaxAreaPx { get; set; } = 200000;

        /// <summary>
        /// 배경 억제(BlackHat/TopHat) 커널 크기[px]. 액적보다 충분히 커야 한다(액적 직경의 2~3배).
        /// 3 미만이면 비활성 → 전역 임계. 실측 Raw(배경 그라디언트 有)에서 전역 Otsu 는 배경을
        /// 거대 전경 덩어리로 잡아 과검출을 유발하므로 기본 활성(81)로 둔다.
        /// </summary>
        public int BackgroundKernel { get; set; } = 81;

        /// <summary>
        /// 모폴로지 open 커널 크기(px). 3 미만이면 스킵.
        /// ※ 기본 0(비활성): 배경 억제를 쓰면 불필요하고, 전역 임계와 함께 쓰면 배경 덩어리를
        ///   조각내 오히려 과검출을 만든다(실측: 액적 15개 → 24개 오검출).
        /// </summary>
        public int MorphKernel { get; set; } = 0;

        // 관심영역(ROI) — 픽셀. Width/Height 가 0이면 전체 프레임 사용. (OpenCvSharp 타입을 config 에 노출하지 않도록 평면 int)
        public int RoiX { get; set; }
        public int RoiY { get; set; }
        public int RoiWidth { get; set; }
        public int RoiHeight { get; set; }

        /// <summary>측정창(Measure Area X) 폭[µm]. 몽타주 컬럼에 측정 ROI 박스로 표시.</summary>
        public double MeasureAreaXUm { get; set; } = 60.0;

        /// <summary>
        /// 노즐면(토출 시작) 의 Y 픽셀 좌표. 다중노즐 프레임에서 액적 낙하거리 = (중심Y − 이 값).
        /// 오버레이의 녹색 기준선이 이 위치에 그려진다. 실장에서 노즐면이 화면 상단이 아니면 조정.
        /// </summary>
        public double NozzleYPixel { get; set; } = 0;

        /// <summary>위상 스윕 캡쳐 프레임 수(몽타주 컬럼 수).</summary>
        public int SweepFrames { get; set; } = 15;

        // ── 이미지 품질 판정 ──────────────────────────────────────────────────
        // 나쁜 이미지도 "그럴듯한 숫자"를 만들어내는 게 가장 위험하다. 특히 초점 이탈은
        // 액적 경계를 번지게 해 직경을 부풀리는데, 부피는 직경의 3제곱이라 직경 10% 오차가
        // 부피 33% 오차가 된다 — 화면상으론 그냥 "부피가 좀 크네"로 보인다.

        /// <summary>
        /// 초점 기준 선명도(Laplacian 분산). 캘리브레이션 시 "초점 기준 저장"으로 기록한다.
        /// <b>절대 임계값을 쓰지 않는 이유</b>: 선명도 값은 렌즈·배율·조명에 따라 자릿수가 달라져
        /// 고정 숫자를 박으면 현장에서 반드시 틀린다. 기준 대비 비율로만 판정한다.
        /// 0 이면 초점 검사 비활성.
        /// </summary>
        public double ReferenceSharpness { get; set; } = 0;

        /// <summary>기준 선명도 대비 허용 하한 비율. 이보다 떨어지면 초점 이탈로 본다.</summary>
        public double MinSharpnessRatio { get; set; } = 0.6;

        /// <summary>포화(0 또는 255) 픽셀 허용 비율. 넘으면 노출 과다/과소.</summary>
        public double MaxSaturatedRatio { get; set; } = 0.02;

        /// <summary>액적과 배경의 최소 명암차(8bit 레벨). 0 이면 대비 검사 비활성.</summary>
        public double MinContrast { get; set; } = 20;
    }

    /// <summary>프레임 품질 측정 결과. 측정을 막지는 않고 결과에 꼬리표를 붙이는 용도.</summary>
    public sealed class FrameQualityResult
    {
        /// <summary>Laplacian 분산 — 클수록 선명. 절대값이 아니라 기준 대비로 해석할 것.</summary>
        public double Sharpness { get; set; }

        /// <summary>기준 대비 선명도 비율. 기준 미설정이면 NaN.</summary>
        public double SharpnessRatio { get; set; } = double.NaN;

        /// <summary>255 포화 픽셀 비율.</summary>
        public double SaturatedHighRatio { get; set; }

        /// <summary>0 포화 픽셀 비율.</summary>
        public double SaturatedLowRatio { get; set; }

        /// <summary>전체 평균 밝기.</summary>
        public double MeanLevel { get; set; }

        /// <summary>액적 영역과 배경의 평균 명암차.</summary>
        public double Contrast { get; set; }

        /// <summary>발견된 문제(없으면 빈 목록).</summary>
        public IReadOnlyList<string> Issues { get; set; } = Array.Empty<string>();

        public bool IsAcceptable => Issues.Count == 0;

        /// <summary>화면 표시용 한 줄 요약. 문제 없으면 null.</summary>
        public string? Summary => Issues.Count == 0 ? null : string.Join(", ", Issues);
    }

    /// <summary>검출된 액적 1개의 기하 정보(단일 프레임 내 다중 액적 분석용).</summary>
    public sealed class DropletInfo
    {
        public double CentroidXPixel  { get; set; }
        public double CentroidYPixel  { get; set; }
        public double AreaPx          { get; set; }
        public double DiameterMicron  { get; set; }
        public double VolumePicoLiter { get; set; }
    }

    /// <summary>
    /// OpenCvSharp 기반 드랍와쳐 액적 측정기(<see cref="DwMeasurerStub"/> 대체).
    /// 파이프라인: 그레이 변환 → (ROI) → 이진화(Otsu/실루엣) → 모폴로지 open → 외곽 컨투어 →
    ///            면적 게이트 → 주(main) 액적(최대 면적) 선정 → 등가원 직경/구형 부피/중심 산출.
    /// 단일 프레임으로는 속도를 알 수 없어 <see cref="DwReading.VelocityMetersPerSecond"/> 는 NaN 이며,
    /// 위상 스윕 결과로 <see cref="DropletKinematics.ComputeVelocityMps"/> 에서 산출한다.
    /// (x86/32비트 빌드 — OpenCvSharp4.runtime.win 의 win-x86 네이티브 사용. Mat 는 즉시 Dispose.)
    /// </summary>
    public sealed class DropWatcherProcessor : IDwMeasurer
    {
        private readonly DropWatcherProcessorConfig _cfg;

        public DropWatcherProcessor(DropWatcherProcessorConfig? cfg = null)
            => _cfg = cfg ?? new DropWatcherProcessorConfig();

        public DwReading Measure(VisionImage frame)
        {
            if (frame == null || !frame.IsValid) return new DwReading();

            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty()) return new DwReading();

            // ROI 적용(범위 밖 좌표는 클램프).
            Rect roi = ClampRoi(new Rect(_cfg.RoiX, _cfg.RoiY, _cfg.RoiWidth, _cfg.RoiHeight), gray.Width, gray.Height);
            using var work = (roi.Width > 0 && roi.Height > 0) ? new Mat(gray, roi) : gray.Clone();

            // 1~2) 배경 억제 + 이진화(+선택적 open).
            using var bin = Segment(work);

            // 3) 외곽 컨투어.
            Cv2.FindContours(bin, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // 4) 면적 게이트 통과 입자 카운트 + 주 액적(최대 면적) 선정.
            int count = 0;
            double bestArea = 0;
            Point[]? best = null;
            foreach (var c in contours)
            {
                double areaPx = Cv2.ContourArea(c);
                if (areaPx < _cfg.MinAreaPx || areaPx > _cfg.MaxAreaPx) continue;
                count++;
                if (areaPx > bestArea) { bestArea = areaPx; best = c; }
            }
            if (best == null) return new DwReading();

            // 5) 주 액적 기하: 등가원 직경 → µm, 구형 가정 부피 → pL.
            var m = Cv2.Moments(best);
            double cyLocal = m.M00 > 0 ? m.M01 / m.M00 : 0;
            double cy = cyLocal + roi.Y;   // 전체 프레임 기준 Y (위상 스윕 궤적용)

            double diaPx = 2.0 * Math.Sqrt(bestArea / Math.PI);
            double diaUm = diaPx * _cfg.MicronsPerPixel;
            double rUm = diaUm / 2.0;
            double volUm3 = 4.0 / 3.0 * Math.PI * rUm * rUm * rUm;
            double volPl = volUm3 * 1e-3;   // 1 µm³ = 1e-3 pL

            return new DwReading
            {
                ParticleCount           = count,      // 주 액적 + 새틀라이트 수(>1 이면 위성 존재)
                DiameterMicron          = diaUm,
                VolumePicoLiter         = volPl,
                CentroidYPixel          = cy,
                VelocityMetersPerSecond = double.NaN, // 위상 스윕에서 산출
            };
        }

        /// <summary>
        /// 배경 억제 + 이진화 → 액적이 255 인 마스크. Measure/DetectDroplets 공통.
        /// BlackHat(어두운 액적)/TopHat(밝은 액적)으로 배경 그라디언트를 제거한 뒤 임계를 잡으면
        /// 조명 불균일에 강건해진다(실측 Raw: 전역 Otsu 는 노이즈 2750개 → BlackHat 은 액적 15개만).
        /// </summary>
        private Mat Segment(Mat work)
        {
            var bin = new Mat();
            double thr = _cfg.ThresholdValue;

            if (_cfg.BackgroundKernel >= 3)
            {
                // 배경(저주파)을 제거해 액적만 밝게 남긴다 → 항상 Binary 로 임계.
                int ks = _cfg.BackgroundKernel | 1;   // 홀수 보정
                using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ks, ks));
                using var hat = new Mat();
                Cv2.MorphologyEx(work, hat, _cfg.DropletsAreDark ? MorphTypes.BlackHat : MorphTypes.TopHat, k);

                var t = ThresholdTypes.Binary;
                if (thr <= 0) { t |= ThresholdTypes.Otsu; thr = 0; }
                Cv2.Threshold(hat, bin, thr, 255, t);
            }
            else
            {
                var t = _cfg.DropletsAreDark ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
                if (thr <= 0) { t |= ThresholdTypes.Otsu; thr = 0; }
                Cv2.Threshold(work, bin, thr, 255, t);
            }

            if (_cfg.MorphKernel >= 3)
            {
                using var k2 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(_cfg.MorphKernel, _cfg.MorphKernel));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, k2);
            }
            return bin;
        }

        /// <summary>
        /// 프레임 안의 모든 액적을 검출해 X 오름차순으로 반환.
        /// 실측 DW Raw 는 한 장에 액적이 가로로 늘어서 있으므로(스트로브 위상별 위치),
        /// 각 액적이 곧 하나의 측정 컬럼(=시간 스텝)이 된다.
        /// </summary>
        public IReadOnlyList<DropletInfo> DetectDroplets(VisionImage frame)
        {
            var list = new List<DropletInfo>();
            if (frame == null || !frame.IsValid) return list;

            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty()) return list;

            Rect roi = ClampRoi(new Rect(_cfg.RoiX, _cfg.RoiY, _cfg.RoiWidth, _cfg.RoiHeight), gray.Width, gray.Height);
            using var work = (roi.Width > 0 && roi.Height > 0) ? new Mat(gray, roi) : gray.Clone();
            using var bin = Segment(work);

            Cv2.FindContours(bin, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            foreach (var c in contours)
            {
                double areaPx = Cv2.ContourArea(c);
                if (areaPx < _cfg.MinAreaPx || areaPx > _cfg.MaxAreaPx) continue;
                var m = Cv2.Moments(c);
                if (m.M00 <= 0) continue;

                double diaUm = 2.0 * Math.Sqrt(areaPx / Math.PI) * _cfg.MicronsPerPixel;
                double rUm = diaUm / 2.0;
                list.Add(new DropletInfo
                {
                    CentroidXPixel  = m.M10 / m.M00 + roi.X,
                    CentroidYPixel  = m.M01 / m.M00 + roi.Y,
                    AreaPx          = areaPx,
                    DiameterMicron  = diaUm,
                    VolumePicoLiter = 4.0 / 3.0 * Math.PI * rUm * rUm * rUm * 1e-3,
                });
            }
            list.Sort((a, b) => a.CentroidXPixel.CompareTo(b.CentroidXPixel));
            return list;
        }

        /// <summary>
        /// 단일 다중노즐 프레임에 LabVIEW 'Sample DW' 스타일 오버레이를 그린다.
        /// 액적 사이를 마젠타 분할선으로 나눠 컬럼(=노즐)을 만들고, 각 컬럼에 측정창(시안) ·
        /// 액적 중심(녹색 십자) · 하단 속도(m/s) · 상단 위치 라벨을 얹는다.
        /// 녹색 기준선 = 노즐면(NozzleYPixel). 반환: 저장 경로(실패 시 null).
        /// </summary>
        /// <param name="delayUs">스트로브 지연[µs] — 낙하시간. 속도 = 낙하거리/지연.</param>
        public string? SaveAnnotatedFrame(string filePath, VisionImage frame,
                                          IReadOnlyList<DropletInfo> drops, double delayUs)
        {
            if (frame == null || string.IsNullOrEmpty(filePath)) return null;
            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty()) return null;

            const int topH = 18, botH = 30;
            var green   = new Scalar(0, 255, 0);
            var magenta = new Scalar(255, 0, 255);
            var cyan    = new Scalar(220, 200, 0);

            int W = gray.Width, H = gray.Height;
            var canvas = new Mat(H + topH + botH, W, MatType.CV_8UC3, new Scalar(12, 12, 14));
            try
            {
                using (var bgr = new Mat())
                {
                    Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
                    using var dst = new Mat(canvas, new Rect(0, topH, W, H));
                    bgr.CopyTo(dst);
                }

                int n = drops?.Count ?? 0;
                int boxW = Math.Max(8, (int)(_cfg.MeasureAreaXUm / Math.Max(0.01, _cfg.MicronsPerPixel)));

                for (int i = 0; i < n; i++)
                {
                    var d = drops![i];
                    int cx = (int)d.CentroidXPixel;

                    // 컬럼 경계: 인접 액적의 중점(양 끝은 프레임 경계)
                    int left  = i == 0     ? 0 : (int)((drops[i - 1].CentroidXPixel + d.CentroidXPixel) / 2);
                    int right = i == n - 1 ? W - 1 : (int)((d.CentroidXPixel + drops[i + 1].CentroidXPixel) / 2);
                    Cv2.Line(canvas, new Point(right, 0), new Point(right, canvas.Height - 1), magenta, 1);

                    // 측정창(Measure Area X) — 액적 중심 기준 세로 밴드
                    int bx = Math.Clamp(cx - boxW / 2, 0, Math.Max(0, W - boxW));
                    Cv2.Rectangle(canvas, new Rect(bx, topH + 4, Math.Min(boxW, W - bx), H - 8), cyan, 1);

                    // 액적 중심 마커
                    Cv2.DrawMarker(canvas, new Point(cx, topH + (int)d.CentroidYPixel), green, MarkerTypes.Cross, 14, 1);

                    // 상단 위치 라벨(µm) / 하단 속도(m/s)
                    Cv2.PutText(canvas, ((int)(d.CentroidXPixel * _cfg.MicronsPerPixel)).ToString(),
                                new Point(Math.Max(2, left + 3), topH - 4), HersheyFonts.HersheySimplex, 0.34, magenta, 1);

                    double vel = VelFromFall(d.CentroidYPixel, delayUs);
                    Cv2.PutText(canvas, double.IsNaN(vel) ? "-" : vel.ToString("F2"),
                                new Point(Math.Max(2, left + 3), canvas.Height - 9),
                                HersheyFonts.HersheySimplex, 0.42, green, 1);
                }

                // 노즐면 기준선(녹색) — 낙하거리의 기준.
                int nozzleY = topH + (int)Math.Clamp(_cfg.NozzleYPixel, 0, H - 1);
                Cv2.Line(canvas, new Point(0, nozzleY), new Point(W - 1, nozzleY), green, 1);
                Cv2.ImWrite(filePath, canvas);
                return filePath;
            }
            catch { return null; }
            finally { canvas.Dispose(); }
        }

        /// <summary>
        /// 프레임 품질을 측정한다 — 분석 전에 "이 이미지를 믿어도 되는가"를 확인하기 위함.
        /// 측정을 막지 않고 지표와 문제 목록만 돌려준다(설정이 조금 어긋났다고 측정 자체를
        /// 못 하게 되면 현장에서 더 곤란하다). 호출부가 결과에 꼬리표를 붙인다.
        /// </summary>
        public FrameQualityResult AnalyzeQuality(VisionImage frame)
        {
            var r = new FrameQualityResult();
            var issues = new List<string>();

            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty())
            {
                r.Issues = new[] { "이미지를 읽을 수 없습니다" };
                return r;
            }

            Rect roi = ClampRoi(new Rect(_cfg.RoiX, _cfg.RoiY, _cfg.RoiWidth, _cfg.RoiHeight), gray.Width, gray.Height);
            using var work = (roi.Width > 0 && roi.Height > 0) ? new Mat(gray, roi) : gray.Clone();

            // ── 선명도: Laplacian 분산(초점 지표) ──
            using (var lap = new Mat())
            {
                Cv2.Laplacian(work, lap, MatType.CV_64F);
                Cv2.MeanStdDev(lap, out _, out Scalar sd);
                r.Sharpness = sd.Val0 * sd.Val0;
            }

            if (_cfg.ReferenceSharpness > 0)
            {
                r.SharpnessRatio = r.Sharpness / _cfg.ReferenceSharpness;
                if (r.SharpnessRatio < _cfg.MinSharpnessRatio)
                    issues.Add($"초점 저하(기준의 {r.SharpnessRatio * 100:F0}%)");
            }

            // ── 포화/평균 밝기 ──
            long total = work.Rows * (long)work.Cols;
            if (total > 0)
            {
                using var hi = new Mat();
                using var lo = new Mat();
                Cv2.Threshold(work, hi, 254, 255, ThresholdTypes.Binary);      // 255 근처
                Cv2.Threshold(work, lo, 1, 255, ThresholdTypes.BinaryInv);     // 0 근처
                r.SaturatedHighRatio = Cv2.CountNonZero(hi) / (double)total;
                r.SaturatedLowRatio  = Cv2.CountNonZero(lo) / (double)total;

                if (r.SaturatedHighRatio > _cfg.MaxSaturatedRatio)
                    issues.Add($"노출 과다(포화 {r.SaturatedHighRatio * 100:F1}%)");
                if (r.SaturatedLowRatio > _cfg.MaxSaturatedRatio)
                    issues.Add($"노출 부족(흑포화 {r.SaturatedLowRatio * 100:F1}%)");
            }

            Cv2.MeanStdDev(work, out Scalar mean, out _);
            r.MeanLevel = mean.Val0;

            // ── 대비: 액적 마스크 내부 vs 외부 평균차 ──
            using (var bin = Segment(work))
            {
                int fg = Cv2.CountNonZero(bin);
                if (fg > 0 && fg < total)
                {
                    using var inv = new Mat();
                    Cv2.BitwiseNot(bin, inv);
                    Scalar mIn  = Cv2.Mean(work, bin);
                    Scalar mOut = Cv2.Mean(work, inv);
                    r.Contrast = Math.Abs(mOut.Val0 - mIn.Val0);

                    if (_cfg.MinContrast > 0 && r.Contrast < _cfg.MinContrast)
                        issues.Add($"대비 부족({r.Contrast:F0} < {_cfg.MinContrast:F0})");
                }
                else if (fg == 0)
                {
                    issues.Add("액적 영역이 검출되지 않음");
                }
            }

            r.Issues = issues;
            return r;
        }

        /// <summary>
        /// 현재 프레임의 선명도를 초점 기준값으로 삼는다(캘리브레이션용).
        /// 작업자가 초점이 맞았다고 확인한 시점에 호출할 것.
        /// </summary>
        public double CaptureSharpnessReference(VisionImage frame)
        {
            var q = AnalyzeQuality(frame);
            if (q.Sharpness > 0) _cfg.ReferenceSharpness = q.Sharpness;
            return q.Sharpness;
        }

        /// <summary>
        /// 검출된 액적들의 평균 X 픽셀 피치와 실제 노즐 피치(µm)로 µm/px 스케일을 산출한다.
        /// 정상 토출 프레임(노즐당 액적 1개, 노즐 순서대로 가로 정렬) 가정에 기반한 현장 교정법이다.
        /// 유효 액적 &lt; 2 또는 knownPitchUm ≤ 0 또는 픽셀 피치 ≤ 0 이면 NaN.
        /// </summary>
        /// <param name="drops">DetectDroplets 결과(X 오름차순). 노즐당 1개여야 피치가 정확하다.</param>
        /// <param name="knownPitchUm">헤드 사양상 인접 노즐 간 실제 거리[µm].</param>
        public static double CalibrateMicronsPerPixel(IReadOnlyList<DropletInfo> drops, double knownPitchUm)
        {
            if (drops == null || drops.Count < 2 || knownPitchUm <= 0) return double.NaN;

            var xs = drops.Select(d => d.CentroidXPixel).OrderBy(x => x).ToList();
            double sum = 0; int n = 0;
            for (int i = 1; i < xs.Count; i++) { sum += xs[i] - xs[i - 1]; n++; }
            if (n == 0) return double.NaN;

            double pixelPitch = sum / n;                 // 인접 액적 평균 X 간격(px)
            if (pixelPitch <= 0) return double.NaN;
            return knownPitchUm / pixelPitch;            // µm/px
        }

        /// <summary>
        /// 액적(=노즐 컬럼)별 속도[m/s] — 오버레이 하단 숫자와 동일 계산(차트 공유용).
        /// </summary>
        /// <param name="delayUs">스트로브 지연[µs] = 낙하시간.</param>
        public double[] ComputeDropletVelocities(IReadOnlyList<DropletInfo> drops, double delayUs)
        {
            int n = drops?.Count ?? 0;
            var v = new double[n];
            for (int i = 0; i < n; i++) v[i] = VelFromFall(drops![i].CentroidYPixel, delayUs);
            return v;
        }

        /// <summary>
        /// 다중노즐 프레임의 액적 속도[m/s] = 낙하거리(노즐면→중심) / 스트로브 지연.
        /// ※ 인접 액적 ΔY 차분을 쓰면 안 된다 — 컬럼은 서로 다른 노즐이라 Y 편차가 노이즈처럼 보여
        ///   음수·발산 값이 나온다(실측 확인). 낙하거리/지연 모델이 물리적으로 옳다.
        /// 단위: µm/µs == m/s.
        /// </summary>
        private double VelFromFall(double centroidY, double delayUs)
        {
            if (delayUs <= 0) return double.NaN;
            return (centroidY - _cfg.NozzleYPixel) * _cfg.MicronsPerPixel / delayUs;
        }

        /// <summary>
        /// 위상 스윕 프레임들을 좌→우 컬럼으로 이어 붙여 LabVIEW 'Sample DW' 스타일 몽타주를 만든다.
        /// 각 컬럼 = 해당 지연의 액적 프레임(리사이즈) + 마젠타 구분선 + 상단 지연(µs) 라벨 + 하단 녹색 속도(m/s).
        /// 상단엔 녹색 기준선. 반환: 저장 경로(실패 시 null).
        /// </summary>
        public string? SaveSweepMontage(string filePath, IReadOnlyList<VisionImage> frames,
                                        IReadOnlyList<DwReading> readings, double stepUs,
                                        double topStartUm = 0, double topStepUm = 0)
        {
            if (frames == null || frames.Count == 0 || string.IsNullOrEmpty(filePath)) return null;

            int n = frames.Count;
            const int colH = 440, colW = 116, botH = 30, topH = 16;
            int H = topH + colH + botH;
            var green   = new Scalar(0, 255, 0);
            var magenta = new Scalar(255, 0, 255);
            var cyan    = new Scalar(220, 200, 0);   // 측정창(Measure Area X) 박스

            var canvas = new Mat(H, colW * n, MatType.CV_8UC3, new Scalar(12, 12, 14));
            try
            {
                for (int i = 0; i < n; i++)
                {
                    int ow = frames[i].Width  > 0 ? frames[i].Width  : 1;
                    int oh = frames[i].Height > 0 ? frames[i].Height : 1;
                    double sx = (double)colW / ow, sy = (double)colH / oh;   // 원본→컬럼 스케일

                    using (var g = ToGrayMat(frames[i]))
                    {
                        if (g != null && !g.Empty())
                        {
                            using var col = new Mat();
                            Cv2.Resize(g, col, new Size(colW, colH));
                            using var bgr = new Mat();
                            Cv2.CvtColor(col, bgr, ColorConversionCodes.GRAY2BGR);
                            using var dst = new Mat(canvas, new Rect(i * colW, topH, colW, colH));
                            bgr.CopyTo(dst);
                        }
                    }

                    // 측정창(Measure Area X) ROI 박스 — 컬럼 중앙에 세로 밴드로 표시.
                    int boxW = Math.Clamp((int)(_cfg.MeasureAreaXUm / Math.Max(0.01, _cfg.MicronsPerPixel) * sx), 8, colW - 6);
                    int bx = i * colW + (colW - boxW) / 2;
                    Cv2.Rectangle(canvas, new Rect(bx, topH + 8, boxW, colH - 16), cyan, 1);

                    // 검출 액적 중심 마커(십자, 녹색).
                    var r = (readings != null && i < readings.Count) ? readings[i] : null;
                    if (r != null && r.IsValid)
                    {
                        int mx = i * colW + (int)(ow / 2.0 * sx);          // cx≈중앙(합성 액적 기준)
                        int my = topH + (int)(r.CentroidYPixel * sy);
                        Cv2.DrawMarker(canvas, new Point(mx, my), green, MarkerTypes.Cross, 10, 1);
                    }

                    // 컬럼 구분선(마젠타, 전체 높이)
                    Cv2.Line(canvas, new Point((i + 1) * colW - 1, 0), new Point((i + 1) * colW - 1, H - 1), magenta, 1);

                    // 상단 눈금 라벨: topStep 이 있으면 측정위치(µm), 없으면 지연(µs)
                    string top = topStepUm > 0 ? ((int)(topStartUm + i * topStepUm)).ToString()
                                               : ((int)(i * stepUs)).ToString();
                    Cv2.PutText(canvas, top, new Point(i * colW + 4, topH - 3),
                                HersheyFonts.HersheySimplex, 0.32, magenta, 1);

                    // 하단 속도(m/s)
                    double vel = ColumnVelocity(readings, i, stepUs);
                    string vtxt = double.IsNaN(vel) ? "-" : vel.ToString("F2");
                    Cv2.PutText(canvas, vtxt, new Point(i * colW + 6, H - 9),
                                HersheyFonts.HersheySimplex, 0.42, green, 1);
                }
                // 상단 기준선(녹색)
                Cv2.Line(canvas, new Point(0, topH + 6), new Point(canvas.Width - 1, topH + 6), green, 1);

                Cv2.ImWrite(filePath, canvas);
                return filePath;
            }
            catch { return null; }
            finally { canvas.Dispose(); }
        }

        /// <summary>
        /// 컬럼별 속도[m/s] 배열 — 몽타주 하단에 찍히는 값과 동일한 계산.
        /// 차트가 몽타주와 같은 수치를 쓰도록 공개한다(계산 중복 방지).
        /// </summary>
        public double[] ComputeColumnVelocities(IReadOnlyList<DwReading> readings, double stepUs)
        {
            int n = readings?.Count ?? 0;
            var v = new double[n];
            for (int i = 0; i < n; i++) v[i] = ColumnVelocity(readings!, i, stepUs);
            return v;
        }

        // 컬럼 i 의 속도(m/s): 인접 프레임 중심 Y 변화 / 지연 스텝. (i=0 은 0↔1 사용)
        // null 허용 — 내부에서 처리한다(호출부에서 ! 로 억지 억제하지 않도록 시그니처에 명시).
        private double ColumnVelocity(IReadOnlyList<DwReading>? readings, int i, double stepUs)
        {
            if (readings == null || stepUs <= 0) return double.NaN;
            int a = i > 0 ? i - 1 : 0;
            int b = i > 0 ? i : 1;
            if (b >= readings.Count) return double.NaN;
            var ra = readings[a]; var rb = readings[b];
            if (ra == null || rb == null || !ra.IsValid || !rb.IsValid) return double.NaN;
            return (rb.CentroidYPixel - ra.CentroidYPixel) * _cfg.MicronsPerPixel / stepUs;   // µm/µs == m/s
        }

        // ── VisionImage → 8bit 그레이 Mat ─────────────────────────────
        // PixelData(원본 버퍼) 우선, 없으면 저장된 파일을 로드. 실패 시 null.
        private static Mat? ToGrayMat(VisionImage f)
        {
            if (f.PixelData != null && f.Width > 0 && f.Height > 0)
            {
                bool is16 = f.BitsPerPixel >= 16;
                int bytesPerPixel = is16 ? 2 : 1;
                int expected = f.Width * f.Height * bytesPerPixel;
                if (f.PixelData.Length >= expected)
                {
                    var raw = new Mat(f.Height, f.Width, is16 ? MatType.CV_16UC1 : MatType.CV_8UC1);
                    Marshal.Copy(f.PixelData, 0, raw.Data, expected);
                    if (!is16) return raw;

                    // Mono16 → 8bit 스케일 다운(0.255e).
                    var m8 = new Mat();
                    raw.ConvertTo(m8, MatType.CV_8UC1, 1.0 / 256.0);
                    raw.Dispose();
                    return m8;
                }
            }

            if (!string.IsNullOrEmpty(f.FilePath) && System.IO.File.Exists(f.FilePath))
            {
                var m = Cv2.ImRead(f.FilePath, ImreadModes.Grayscale);
                return m.Empty() ? null : m;
            }
            return null;
        }

        private static Rect ClampRoi(Rect r, int w, int h)
        {
            if (r.Width <= 0 || r.Height <= 0) return default;
            int x = Math.Clamp(r.X, 0, Math.Max(0, w - 1));
            int y = Math.Clamp(r.Y, 0, Math.Max(0, h - 1));
            int rw = Math.Clamp(r.Width, 1, w - x);
            int rh = Math.Clamp(r.Height, 1, h - y);
            return new Rect(x, y, rw, rh);
        }
    }

    /// <summary>
    /// 위상 스윕 결과에서 액적 운동학(수직 속도)을 산출하는 헬퍼.
    /// 스트로브 지연(us)을 훑으며 잡은 주 액적 중심 Y(px)의 최소자승 기울기(dY/dDelay)로 속도를 얻는다.
    /// </summary>
    public static class DropletKinematics
    {
        /// <summary>
        /// 위상 스윕(지연 us ↔ 중심 Y px)에서 액적 수직 속도[m/s] 산출.
        /// 단위 정리: (px·µm/px)/µs = µm/µs = m/s (환산계수 1). 유효 점 &lt; 2 이면 NaN.
        /// </summary>
        public static double ComputeVelocityMps(
            IReadOnlyList<(double delayUs, DwReading reading)> sweep, double micronsPerPixel)
        {
            if (sweep == null) return double.NaN;
            var pts = sweep
                .Where(s => s.reading != null && s.reading.ParticleCount > 0)
                .Select(s => (x: s.delayUs, y: s.reading.CentroidYPixel))
                .ToList();
            if (pts.Count < 2) return double.NaN;

            double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var (x, y) in pts) { sx += x; sy += y; sxx += x * x; sxy += x * y; }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) return double.NaN;

            double slopePxPerUs = (n * sxy - sx * sy) / denom;   // px/µs
            return slopePxPerUs * micronsPerPixel;               // µm/µs == m/s
        }
    }
}
