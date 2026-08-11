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

        /// <summary>
        /// 측정창(Measure Area X) 폭[µm] — 화면의 분홍 박스 가로 폭.
        /// <para>
        /// 액적 직경(수십µm)에 토출 흔들림을 더한 것보다 넉넉해야 한다. 좁으면 옆 노즐을 안 물어
        /// 좋을 것 같지만, ROI 가 조금만 어긋나도 액적이 창 밖으로 빠져 아예 안 잡힌다
        /// (60µm 는 4.0X 에서 88px 로 노즐 간격 371px 의 24%였다 — 2026-08-07 정정).
        /// 간격을 넘는 값은 <see cref="TryBuildWindows"/> 에서 잘리므로 옆 노즐과 겹치지는 않는다.
        /// </para>
        /// </summary>
        public double MeasureAreaXUm { get; set; } = 150.0;

        /// <summary>
        /// 노즐면(토출 시작) 의 Y 픽셀 좌표. 다중노즐 프레임에서 액적 낙하거리 = (중심Y − 이 값).
        /// 오버레이의 녹색 기준선이 이 위치에 그려진다. 실장에서 노즐면이 화면 상단이 아니면 조정.
        /// </summary>
        public double NozzleYPixel { get; set; } = 0;

        /// <summary>위상 스윕 캡쳐 프레임 수(몽타주 컬럼 수).</summary>
        public int SweepFrames { get; set; } = 15;

        // ── 노즐 기하 (LabVIEW 'Sample DW' 방식) ──────────────────────────────
        // 원본 LabVIEW 는 노즐 위치를 미리 알고(피치) 그 자리에만 고정 측정창을 놓는다.
        // 반면 이미지 전체에서 자유 검출하면, 액적이 아닌 사진에서도 얼룩을 액적으로 세어
        // "노즐 62개 · 부피 2544pL" 같은 그럴듯한 쓰레기 값이 나온다(실장 2026-08-06).
        // 고정창 방식은 엉뚱한 이미지에서 '아무것도 안 나오는 것'이 정상 동작이 된다.

        /// <summary>
        /// 물리 노즐 피치[µm] — 열 내 인접 노즐 간격. 측정창을 놓는 기준이다.
        /// ※ PCC .cfg 의 Xdpi 는 <b>인쇄 해상도</b>지 노즐 피치가 아니다(600DPI = 인쇄 픽셀 42.3µm).
        ///   반드시 헤드 사양서 값을 넣을 것.
        /// </summary>
        public double NozzlePitchUm { get; set; } = 254.0;

        /// <summary>1번 노즐(화면 최좌측 측정창) 중심의 X 픽셀. 0 이면 검출된 액적에서 위상을 추정한다.</summary>
        public double NozzleOriginXPx { get; set; } = 0;

        /// <summary>
        /// 측정창 간격을 <b>픽셀로 직접</b> 고정한다. 0 이면 <see cref="NozzlePitchUm"/> ÷ µm/px 로 계산.
        /// <para>
        /// µm 로 계산하면 피치와 스케일 <b>둘 다</b> 맞아야 창이 제자리에 선다. 실장 교정 전이거나
        /// 다른 카메라 샘플을 볼 때는 그 둘이 안 맞아 창이 통째로 어긋난다(실장 2026-08-06: 371px
        /// 로 잡혀 실제 간격 113px 와 3배 차이). 화면에서 눈으로 맞출 수 있는 픽셀값을 직접 주면
        /// 스케일과 무관하게 라인이 고정된다 — LabVIEW 가 ROI 를 픽셀로 들고 있는 것과 같다.
        /// </para>
        /// </summary>
        public double NozzlePitchPx { get; set; } = 0;

        /// <summary>측정창을 픽셀로 줄 때의 창 <b>전체 폭</b>[px] (중심 ±폭/2). 0 이면 MeasureAreaXUm ÷ µm/px 로 계산.</summary>
        public double MeasureAreaXPx { get; set; } = 0;

        /// <summary>세로 추적 구간을 픽셀로 고정(Top/Bottom). 둘 다 0 이면 MeasureStart/EndUm 을 쓴다.</summary>
        public int MeasureTopPx { get; set; } = 0;
        public int MeasureBottomPx { get; set; } = 0;

        /// <summary>
        /// true 면 노즐 피치 기반 고정 측정창 안에서만 찾는다. false 면 전체 자유 검출.
        /// <b>기본은 false</b> — 고정창은 피치·µm/px·노즐면이 모두 실측으로 맞춰진 뒤에야 의미가 있어서,
        /// 그 설정을 갖춘 현장 config(DropWatcherConfig.json)에서 켠다. 실장 9호기는 켜져 있다.
        /// </summary>
        public bool UseFixedNozzleRoi { get; set; } = false;

        /// <summary>측정 추적 구간 시작[µm] — 노즐면(NozzleYPixel) 기준 아래로.</summary>
        public double MeasureStartUm { get; set; } = 130.0;

        /// <summary>측정 추적 구간 끝[µm] — 노즐면 기준. Start 이하면 전체 높이를 쓴다.</summary>
        public double MeasureEndUm { get; set; } = 910.0;

        // ── 프레임 출처 검증 ──────────────────────────────────────────────────
        /// <summary>
        /// 이 카메라가 내놓아야 할 해상도[px]. 0 이면 검사 안 함.
        /// 다른 카메라·다른 호기에서 찍힌 이미지를 열어 측정하면 µm/px 가 통째로 달라
        /// 모든 절대값이 틀린다(실장: 1280×1072 이미지를 2856×2848 스케일로 측정).
        /// </summary>
        public int ExpectedImageWidth { get; set; } = 0;
        public int ExpectedImageHeight { get; set; } = 0;

        /// <summary>
        /// 카메라 시야[µm] — 렌즈·카메라 사양서 값 (FOV = 센서 크기 × 픽셀크기 ÷ 배율). 0 이면 미사용.
        /// <para>
        /// <b>이것이 기준이다.</b> 시야는 광학계가 정하는 고정값이고, 프레임 해상도가 얼마든
        /// µm/px = FOV ÷ 프레임 픽셀수 로 항상 계산된다. µm/px 를 손으로 적어 두면 해상도가
        /// 달라지는 순간(비닝·크롭·가상 드라이버) 조용히 틀린 채로 남고, 눈금이 실제와 다른
        /// 크기를 말하게 된다 — 9호기 사양 1.9564 × 1.9509 mm 인데 화면은 1112 × 849 µm 로
        /// 표시되던 건(2026-08-07)이 그것이다.
        /// </para>
        /// </summary>
        public double FieldOfViewXUm { get; set; } = 0;

        /// <summary>카메라 세로 시야[µm]. 0 이면 가로와 같은 스케일(정사각 픽셀)로 본다.</summary>
        public double FieldOfViewYUm { get; set; } = 0;

        /// <summary>가로 스케일[µm/px] = 시야 ÷ 프레임 폭. FOV 미설정이면 null.</summary>
        public double? ScaleFromFov(int frameWidth) =>
            FieldOfViewXUm > 0 && frameWidth > 0 ? FieldOfViewXUm / frameWidth : (double?)null;

        /// <summary>
        /// 세로 스케일[µm/px] = 시야 ÷ 프레임 높이. 세로 FOV 가 없으면 null(가로 값을 쓴다).
        /// <para>
        /// 눈금 표시 전용이다. 직경·부피·속도는 가로 스케일 하나로 계산한다 — 실장 카메라는
        /// 픽셀이 정사각이라 두 값이 소수 넷째 자리까지 같고, 축마다 다른 스케일로 물리량을
        /// 계산하면 어느 쪽이 쓰였는지 알 수 없는 숫자가 나오기 때문이다.
        /// 두 값이 크게 다르면 그 프레임이 이 카메라의 정상 출력이 아니라는 뜻이고,
        /// 눈금이 가로·세로를 각각 사실대로 보여 주면 그 사실이 화면에 드러난다.
        /// </para>
        /// </summary>
        public double? ScaleYFromFov(int frameHeight) =>
            FieldOfViewYUm > 0 && frameHeight > 0 ? FieldOfViewYUm / frameHeight : (double?)null;

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

        /// <summary>
        /// 이 클래스가 모르는 JSON 키를 그대로 담아 두었다가 저장할 때 되돌려 쓴다.
        /// <para>
        /// 화면의 [교정값 저장]이 이 객체를 통째로 직렬화하므로, 이게 없으면 설정 파일에 적어 둔
        /// <c>_comment</c> 설명(스케일 산출 근거·미검증 항목 경고 등)이 저장 한 번에 전부 사라진다.
        /// 그 메모는 나중에 이 파일만 보고 판단해야 하는 사람에게 필요하다.
        /// </para>
        /// </summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtraKeys { get; set; }
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

        /// <summary>
        /// 액적이 측정창 경계에 닿았다 = 면적이 잘렸다.
        ///
        /// <para>
        /// 검출은 측정창으로 이미지를 <b>잘라낸 뒤</b> 수행하므로, 창 밖으로 나간 부분은 아예
        /// 존재하지 않는 픽셀이 된다. 직경은 √면적, 부피는 직경³ 이라 오차가 증폭된다 —
        /// 실측에서 창 아래끝에 걸린 액적이 직경 26.9µm(실제 35.1) · 부피 10.4pL(실제 22.6)로
        /// 나왔다(2026-08-10). 화면만 보고는 알 수 없어 조용히 절반짜리 값이 남는다.
        /// </para>
        /// </summary>
        public bool ClippedByWindow { get; set; }
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

        /// <summary>
        /// 측정창에 걸린 액적이 있으면 경고 문구, 없으면 null.
        /// 단일프레임 측정과 2점 측정이 같은 문구를 쓰도록 여기 둔다.
        /// </summary>
        public static string? ClippedWarning(IReadOnlyList<DropletInfo>? drops)
        {
            int n = drops?.Count(d => d.ClippedByWindow) ?? 0;
            return n == 0 ? null
                 : $"액적 {n}개가 측정창 경계에 걸림 — 직경·부피가 실제보다 작게 나옵니다(측정창 조정 필요)";
        }

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
        /// <summary>배경 추정 폭[px] 상한 — 이 크기로 줄여서 큰 커널 형태학을 돌린다.</summary>
        private const int BackgroundWorkLongSide = 720;

        /// <summary>
        /// 배경(저주파 성분)을 형태학으로 추정한다. 액적이 어두우면 닫힘, 밝으면 열림.
        ///
        /// <para>
        /// <b>축소본에서 계산하는 이유</b>: 형태학 비용은 O(폭×높이×커널²) 다. 실장 프레임
        /// 2856×2848 에 커널 81 이면 5×10¹⁰ 회 — 분 단위로 멈추고, 32비트 프로세스에서는 내부
        /// 버퍼까지 겹쳐 네이티브 예외로 죽는다(실장 2026-08-07: [격자 자동 맞춤] 누르자 앱 응답 없음
        /// → "External component has thrown an exception"). 배경은 <b>정의상 저주파</b>라 축소본에서
        /// 구해도 같은 그림이 나오고, 커널을 같은 비율로 줄이면 물리적 의미도 그대로다.
        /// 액적 경계는 원본 해상도의 <c>work</c> 에서 빼기 때문에 선명도를 잃지 않는다.
        /// </para>
        /// </summary>
        private Mat EstimateBackground(Mat work, int kernelSize)
        {
            var op = _cfg.DropletsAreDark ? MorphTypes.Close : MorphTypes.Open;
            int longSide = Math.Max(work.Width, work.Height);
            double scale = longSide > BackgroundWorkLongSide ? (double)BackgroundWorkLongSide / longSide : 1.0;

            if (scale >= 1.0)
            {
                var bgFull = new Mat();
                using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
                Cv2.MorphologyEx(work, bgFull, op, k);
                return bgFull;
            }

            int ksSmall = Math.Max(3, (int)Math.Round(kernelSize * scale)) | 1;

            using var small = new Mat();
            Cv2.Resize(work, small, new Size(), scale, scale, InterpolationFlags.Area);

            using var kS = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ksSmall, ksSmall));
            using var smallBg = new Mat();
            Cv2.MorphologyEx(small, smallBg, op, kS);

            var bg = new Mat();
            Cv2.Resize(smallBg, bg, new Size(work.Width, work.Height), 0, 0, InterpolationFlags.Linear);
            return bg;
        }

        private Mat Segment(Mat work)
        {
            var bin = new Mat();
            double thr = _cfg.ThresholdValue;

            if (_cfg.BackgroundKernel >= 3)
            {
                // 배경(저주파)을 제거해 액적만 밝게 남긴다 → 항상 Binary 로 임계.
                int ks = _cfg.BackgroundKernel | 1;   // 홀수 보정
                using var bg = EstimateBackground(work, ks);
                using var hat = new Mat();
                // BlackHat = 닫힘 − 원본, TopHat = 원본 − 열림. 배경을 따로 구해 빼는 형태로 쓰면
                // 배경만 축소본에서 계산할 수 있다(아래 EstimateBackground 참고).
                if (_cfg.DropletsAreDark) Cv2.Subtract(bg, work, hat);
                else                      Cv2.Subtract(work, bg, hat);

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
        /// <summary>
        /// 측정 전 프레임이 이 장비의 것인지, 설정이 물리적으로 말이 되는지 확인한다.
        /// 문제가 없으면 null, 있으면 사유 문자열. <b>측정을 시작하기 전에 부르고, 값이 있으면 측정하지 말 것</b> —
        /// 스케일이 틀린 채로 계산하면 "그럴듯하지만 전부 틀린 숫자"가 나와 오히려 판단을 망친다.
        /// </summary>
        public string? ValidateFrame(VisionImage frame) => ValidateSource(frame) ?? ValidateSetup(frame);

        /// <summary>
        /// 프레임이 <b>이 카메라</b>에서 나온 것인지. 다르면 사유, 같으면 null.
        /// <para>
        /// 라이브 캡쳐가 이걸 어기면 설정이 틀린 것이라 측정을 막아야 한다. 반대로 작업자가
        /// [이미지 열기]로 <b>일부러 연 파일</b>(0호기 샘플 등)은 막으면 안 된다 — 분석하려고 연 것이다.
        /// 대신 그 이미지의 µm/px 는 이 카메라 값과 다르므로 호출부가 경고를 남기고,
        /// 작업자가 CALIBRATION 의 Scale 을 그 이미지에 맞춰 교정한 뒤 측정해야 한다.
        /// </para>
        /// </summary>
        public string? ValidateSource(VisionImage frame)
        {
            if (frame == null || !frame.IsValid) return "유효하지 않은 프레임";

            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty()) return "이미지를 읽지 못했습니다";

            if (_cfg.ExpectedImageWidth > 0 && _cfg.ExpectedImageHeight > 0 &&
                (gray.Width != _cfg.ExpectedImageWidth || gray.Height != _cfg.ExpectedImageHeight))
            {
                return $"이 카메라의 이미지가 아닙니다 — {gray.Width}×{gray.Height} " +
                       $"(기대 {_cfg.ExpectedImageWidth}×{_cfg.ExpectedImageHeight}). " +
                       $"현재 스케일 {_cfg.MicronsPerPixel:F3}µm/px 는 이 이미지의 값이 아닙니다.";
            }
            return null;
        }

        /// <summary>
        /// 설정이 물리적으로 말이 되는지(스케일·피치). 이건 이미지 출처와 무관한 설정 오류라
        /// 파일이든 라이브든 측정을 막아야 한다.
        /// </summary>
        public string? ValidateSetup(VisionImage frame)
        {
            if (_cfg.MicronsPerPixel <= 0) return "µm/px 스케일이 설정되지 않았습니다";
            if (!_cfg.UseFixedNozzleRoi) return null;
            if (_cfg.NozzlePitchUm <= 0) return "노즐 피치가 설정되지 않았습니다";

            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty()) return null;

            // 시야에 노즐이 한 개도 안 들어오면 피치나 스케일이 틀린 것이다.
            double fovUm = gray.Width * _cfg.MicronsPerPixel;
            if (fovUm / _cfg.NozzlePitchUm < 1.0)
            {
                return $"시야({fovUm:F0}µm)가 노즐 피치({_cfg.NozzlePitchUm:F0}µm)보다 좁습니다 — " +
                       "피치 또는 µm/px 설정을 확인하세요.";
            }
            return null;
        }

        /// <summary>
        /// 액적 검출. <see cref="DropWatcherProcessorConfig.UseFixedNozzleRoi"/> 가 켜져 있으면
        /// 노즐 피치 기반 고정 측정창 안에서만 찾고(LabVIEW 방식), 꺼져 있으면 전체 자유 검출.
        /// </summary>
        public IReadOnlyList<DropletInfo> DetectDroplets(VisionImage frame)
        {
            if (frame == null || !frame.IsValid) return new List<DropletInfo>();
            using var g = ToGrayMat(frame);
            if (g == null || g.Empty()) return new List<DropletInfo>();

            return _cfg.UseFixedNozzleRoi ? DetectByNozzleRoi(g) : DetectFree(g);
        }

        /// <summary>노즐 피치로 자리를 잡은 고정 측정창 안에서만 액적을 찾는다(창당 최대 1개).</summary>
        private List<DropletInfo> DetectByNozzleRoi(Mat gray)
        {
            var list = new List<DropletInfo>();
            double upp = _cfg.MicronsPerPixel;
            double pitchPx = EffectivePitchPx();
            if (pitchPx < 4) return list;                    // 창이 겹칠 정도 — 설정 오류

            double originX = ResolveNozzleOriginX(gray, pitchPx);
            if (!TryBuildWindows(gray.Width, gray.Height, originX, pitchPx,
                                 out var centers, out int yTop, out int yBot, out double halfWinPx))
                return list;

            using var band = new Mat(gray, new Rect(0, yTop, gray.Width, yBot - yTop + 1));
            using var bin  = Segment(band);

            foreach (double cx in centers)
            {
                int x0 = (int)Math.Round(cx - halfWinPx);
                int x1 = (int)Math.Round(cx + halfWinPx);
                x0 = Math.Max(0, x0);
                x1 = Math.Min(bin.Width - 1, x1);
                if (x1 - x0 < 2) continue;

                using var win = new Mat(bin, new Rect(x0, 0, x1 - x0 + 1, bin.Height));
                Cv2.FindContours(win, out Point[][] contours, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                // 창당 하나 — 가장 큰 것이 주 액적이다(위성 액적은 버린다).
                Point[]? best = null;
                double bestArea = 0;
                foreach (var c in contours)
                {
                    double a = Cv2.ContourArea(c);
                    if (a < _cfg.MinAreaPx || a > _cfg.MaxAreaPx) continue;
                    if (a > bestArea) { bestArea = a; best = c; }
                }
                if (best == null) continue;                  // 이 노즐은 미토출 — 건너뛴다

                var m = Cv2.Moments(best);
                if (m.M00 <= 0) continue;

                // 창 경계에 닿았으면 면적이 잘린 것이다 — 값 자체는 그대로 두되 표시해 둔다.
                // 여기서 버리면 "노즐이 사라지는" 더 나쁜 증상이 되므로 판단은 화면에 맡긴다.
                var bb = Cv2.BoundingRect(best);
                bool clipped = bb.Y <= 0 || bb.Y + bb.Height >= win.Height
                            || bb.X <= 0 || bb.X + bb.Width  >= win.Width;

                double diaUm = 2.0 * Math.Sqrt(bestArea / Math.PI) * upp;
                double rUm = diaUm / 2.0;
                list.Add(new DropletInfo
                {
                    CentroidXPixel  = m.M10 / m.M00 + x0,
                    CentroidYPixel  = m.M01 / m.M00 + yTop,
                    AreaPx          = bestArea,
                    DiameterMicron  = diaUm,
                    VolumePicoLiter = 4.0 / 3.0 * Math.PI * rUm * rUm * rUm * 1e-3,
                    ClippedByWindow = clipped,
                });
            }
            return list;
        }

        /// <summary>
        /// 1번 측정창의 X 중심. 설정값이 있으면 그대로 쓰고, 없으면 자유 검출 결과에서 위상만 추정한다.
        /// 위상은 원형 평균으로 낸다 — 단순 나머지의 중앙값은 0/피치 경계에서 무너진다.
        /// </summary>
        /// <summary>측정창 간격[px]. 픽셀 지정이 있으면 그것, 없으면 피치[µm] ÷ µm/px.</summary>
        private double EffectivePitchPx()
            => _cfg.NozzlePitchPx > 0
             ? _cfg.NozzlePitchPx
             : _cfg.NozzlePitchUm / Math.Max(0.0001, _cfg.MicronsPerPixel);

        /// <summary>
        /// 이미지에서 실제 노즐 격자(1번 중심 X, 간격)를 읽어낸다. 창을 픽셀로 고정할 값을 얻는 용도.
        /// 이웃 간격의 <b>중앙값</b>을 쓴다 — 평균은 미토출로 한 칸 건너뛴 구간에 끌려간다.
        /// 액적이 2개 미만이면 null.
        /// </summary>
        public (double OriginXPx, double PitchPx, int Count)? EstimateNozzleGrid(VisionImage frame)
        {
            if (frame == null || !frame.IsValid) return null;
            using var gray = ToGrayMat(frame);
            if (gray == null || gray.Empty()) return null;

            var drops = DetectFree(gray);           // X 오름차순
            if (drops.Count < 2) return null;

            var gaps = new List<double>(drops.Count - 1);
            for (int i = 1; i < drops.Count; i++)
                gaps.Add(drops[i].CentroidXPixel - drops[i - 1].CentroidXPixel);
            gaps.Sort();
            double pitch = gaps[gaps.Count / 2];
            if (pitch < 4) return null;

            return (drops[0].CentroidXPixel, pitch, drops.Count);
        }

        private double ResolveNozzleOriginX(Mat gray, double pitchPx)
            => _cfg.NozzleOriginXPx > 0 ? _cfg.NozzleOriginXPx
                                        : PhaseFromDrops(DetectFree(gray), pitchPx);

        /// <summary>액적 X 들의 피치 위상(원형 평균). 단순 나머지의 중앙값은 0/피치 경계에서 무너진다.</summary>
        private static double PhaseFromDrops(IReadOnlyList<DropletInfo> drops, double pitchPx)
        {
            if (drops == null || drops.Count < 2 || pitchPx <= 0) return double.NaN;

            double sx = 0, sy = 0;
            foreach (var d in drops)
            {
                double th = 2.0 * Math.PI * (d.CentroidXPixel / pitchPx);
                sx += Math.Cos(th);
                sy += Math.Sin(th);
            }
            double phase = Math.Atan2(sy, sx) / (2.0 * Math.PI) * pitchPx;
            while (phase < 0) phase += pitchPx;
            return phase;
        }

        /// <summary>
        /// 고정 측정창의 기하(중심 X 목록·세로 밴드·창 반폭). 검출과 오버레이가 같은 값을 써야
        /// 화면의 분홍 박스와 실제 측정 위치가 일치한다. 설정이 안 맞으면 false.
        /// </summary>
        private bool TryBuildWindows(int width, int height, double originX, double pitchPx,
                                     out List<double> centers, out int yTop, out int yBot, out double halfWinPx)
        {
            centers = new List<double>();
            yTop = 0; yBot = height - 1; halfWinPx = 0;
            if (pitchPx < 4 || double.IsNaN(originX)) return false;

            double upp = _cfg.MicronsPerPixel;

            // 창 폭 — 픽셀 지정이 있으면 그것이 우선(스케일과 무관하게 고정).
            double winPx = _cfg.MeasureAreaXPx > 0 ? _cfg.MeasureAreaXPx : _cfg.MeasureAreaXUm / upp;
            halfWinPx = Math.Min(Math.Max(2.0, winPx / 2.0), pitchPx / 2.0);

            if (_cfg.MeasureTopPx > 0 || _cfg.MeasureBottomPx > 0)
            {
                yTop = _cfg.MeasureTopPx;
                yBot = _cfg.MeasureBottomPx > 0 ? _cfg.MeasureBottomPx : height - 1;
            }
            else
            {
                yTop = (int)Math.Round(_cfg.NozzleYPixel + _cfg.MeasureStartUm / upp);
                yBot = (int)Math.Round(_cfg.NozzleYPixel + _cfg.MeasureEndUm   / upp);
            }
            yTop = Math.Clamp(yTop, 0, height - 1);
            yBot = Math.Clamp(yBot, 0, height - 1);
            if (yBot - yTop < 2) { yTop = 0; yBot = height - 1; }

            for (double cx = originX; cx < width; cx += pitchPx) centers.Add(cx);
            return centers.Count > 0;
        }

        /// <summary>이미지 전체에서 액적을 자유 검출(구방식). 위상 추정과 교정에 쓰인다.</summary>
        private List<DropletInfo> DetectFree(Mat gray)
        {
            var list = new List<DropletInfo>();

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
        ///
        /// <para>
        /// <b>★화면 표시에는 쓰지 말 것 — 파일로 남길 때만.</b> 화면은 ImageScaleRuler 가 같은 선을
        /// 벡터로 그리므로, 구워 넣은 이미지를 올리면 선이 <b>두 겹</b>으로 보인다. 단일프레임
        /// 경로에서 한 번(2026-08-06), 2점 측정 경로에서 또 한 번(2026-08-11) 같은 증상이 났다.
        /// 화면에는 원본 프레임을 그대로 올리고 마커만 벡터로 얹는다.
        /// </para>
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

                // 고정 측정창 모드면 '노즐 자리'를 먼저 그린다 — 액적이 없는 창도 보여야
                // 미토출인지 창이 어긋난 것인지 화면에서 바로 구분된다(LabVIEW 의 분홍 박스).
                if (_cfg.UseFixedNozzleRoi && (_cfg.NozzlePitchPx > 0 || _cfg.NozzlePitchUm > 0))
                {
                    double pitchPx = EffectivePitchPx();
                    double originX = _cfg.NozzleOriginXPx > 0
                                   ? _cfg.NozzleOriginXPx
                                   : PhaseFromDrops(drops ?? Array.Empty<DropletInfo>(), pitchPx);

                    if (TryBuildWindows(W, H, originX, pitchPx,
                                        out var centers, out int wTop, out int wBot, out double halfPx))
                    {
                        foreach (double cx in centers)
                        {
                            int x0 = Math.Clamp((int)Math.Round(cx - halfPx), 0, W - 1);
                            int x1 = Math.Clamp((int)Math.Round(cx + halfPx), 0, W - 1);
                            if (x1 - x0 < 2) continue;
                            Cv2.Rectangle(canvas,
                                new Rect(x0, topH + wTop, x1 - x0, Math.Max(2, wBot - wTop)), magenta, 1);
                        }
                    }
                }

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
