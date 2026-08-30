using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.HMI.ViewModels;
using IJPSystem.Platform.Infrastructure.Vision;
using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Config;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.HMI.Services
{
    /// <summary>
    /// 글라스 자동 정렬 — 시퀀스가 시키는 일을 실제 카메라·모터로 옮긴다.
    ///
    /// <para>계산과 거절 조건은 전부 <see cref="GlassAlign"/> 에 있다. 여기가 하는 일은
    /// <b>찍고 · 찾고 · 움직이는</b> 것뿐이다 — 그래야 위험한 부분(무엇을 믿고 무엇을 거절할지)이
    /// 장비 없이 검증되는 자리에 남는다.</para>
    ///
    /// <para>화면(GlassViewModel)과 따로 도는 이유: 시퀀스는 글라스 화면을 열지 않고도 돌아야 한다.
    /// 그래서 프레임을 화면에서 받지 않고 카메라에서 직접 찍는다.</para>
    /// </summary>
    public sealed class GlassAlignService : IGlassAlignService
    {
        private readonly MainViewModel _mainVM;
        private readonly IMotionService _motion;
        private readonly PatternRepository _repo = new();

        /// <summary>글라스 화면이 열려 있으면 거기서 고른 패턴 이름. 없으면 null.</summary>
        private readonly Func<string?>? _selectedPattern;

        /// <summary>
        /// 재는 동안 라이브 미리보기를 비키게 하는 잠금(글라스 화면이 열려 있을 때만).
        /// 화면 없이 도는 시퀀스에서는 null — 비킬 라이브가 없다.
        /// </summary>
        private readonly Func<IDisposable>? _holdLive;

        public GlassAlignService(MainViewModel mainVM,
                                 Func<string?>? selectedPattern = null,
                                 Func<IDisposable>? holdLive = null)
        {
            _mainVM = mainVM ?? throw new ArgumentNullException(nameof(mainVM));
            _motion = new MotionServiceAdapter(mainVM);
            _selectedPattern = selectedPattern;
            _holdLive = holdLive;
        }

        // 측정 결과는 단계 사이에 남아야 한다 — 마크1 을 찍고 옮긴 뒤 마크2 를 찍어 둘을 합친다.
        private MarkReading _mark1, _mark2;
        private PatternEntry? _pattern;

        // ── 준비 확인 ────────────────────────────────────────────────────

        public string? NotReadyReason
        {
            get
            {
                if (_mainVM.HasActiveAlarm) return "미해제 알람이 있습니다.";
                if (string.IsNullOrEmpty(_mainVM.RecipeVM?.ActiveRecipeName))
                    return "적용된 레시피가 없습니다.";

                // 레시피가 "미사용"이면 여기서 끝낸다 — 피듀셜 마크가 없는 품종이라
                // 어차피 마크를 못 찾고, 못 찾은 것을 고장으로 읽게 되기 때문이다.
                if (!IsEnabled)
                    return "레시피에서 자동 정렬이 미사용입니다 — 기본 설정에서 [사용]으로 바꾸세요.";

                if (Calibration == null)
                    return "픽셀 → mm 교정이 없습니다 — VisionConfig 의 NominalMicronPerPx 와 " +
                           $"PixelUAxis/PixelVAxis 를 채우거나 {AlignCalibrationStore.FileName} 을(를) 두세요.";

                if (!StageAxis.TryParseRotation(GlassCamera()?.TAxisPositiveDir, out _))
                    return "T 축의 + 방향이 없습니다 — VisionConfig 의 TAxisPositiveDir 에 " +
                           "CW(시계) 또는 CCW(반시계)를 넣으세요.";

                var r = _mainVM.RecipeVM!;
                // 마크2 로 가는 이동은 순수 -Y 이므로 Y 간격이 없으면 아예 못 움직인다.
                // X 간격은 이동에도 각도 계산에도 쓰지 않는다(GlassAlign.StageMoveToMark2 참고).
                if (r.FiducialPitchYMm <= 0)
                    return "레시피에 피듀셜 마크 간격 Y 가 없습니다 — 글라스 정보에 Y 를 넣으세요.";

                // 가상 모드에서는 패턴이 없어도 된다 — 카메라 대신 계산으로 재기 때문이다.
                if (!IsVirtualVision && ResolvePattern() == null)
                    return _repo.List().Count == 0
                        ? "등록된 정렬 패턴이 없습니다 — 글라스 화면에서 패턴을 등록하세요."
                        : "정렬 패턴이 여러 개입니다 — 글라스 화면에서 쓸 패턴을 고르세요.";

                // 미원점 상태의 절대좌표 이동은 위험 — 인쇄 시작과 같은 기준으로 막는다.
                var all = _mainVM.GetController()?.GetMachine()?.Motion?.GetAllStatus();
                if (all == null || all.Count == 0) return "축 정보가 없습니다 — 모션 드라이버를 확인하세요.";

                var notHomed = all.Where(a => !a.IsHomeDone).Select(a => a.AxisNo).ToList();
                if (notHomed.Count > 0)
                    return $"INITIALIZE 가 필요합니다 — 미원점 축: {string.Join(", ", notHomed)}";

                return null;
            }
        }

        public int MaxPasses => Limits.MaxPasses;

        /// <summary>
        /// 적용된(APPLY) 레시피의 [기본 설정 → 자동 정렬]. 레시피가 없으면 false — 모르면 안 돈다.
        ///
        /// <para>편집 중인 값이 아니라 <b>적용된</b> 값을 본다. 인쇄가 도는 기준은 화면에서
        /// 만지고 있는 레시피가 아니라 장비에 적용된 레시피다.</para>
        /// </summary>
        public bool IsEnabled => _mainVM.RecipeVM?.ActiveAutoAlignEnabled ?? false;

        /// <summary>읽어 둔 교정. 실측 교정을 저장하면 <see cref="ReloadCalibration"/> 로 비운다.</summary>
        private PixelToStage? _cal;

        /// <summary>바로 앞 측정에서 기준 자리와 벌어져 있던 픽셀 거리. NaN 이면 비교 대상 없음.</summary>
        private double _lastErrorPx = double.NaN;

        /// <summary>
        /// 가상 비전인가(AppConfig 의 DriverMode.Vision). 가상이면 카메라 대신 계산으로 잰다.
        ///
        /// <para>Virtual 은 "이 PC 에는 하드웨어가 없다"는 선언이다 — 그 선언을 따르는 자리를
        /// 여기 하나로 모아 둔다.</para>
        /// </summary>
        private static bool IsVirtualVision =>
            string.Equals(AppSettingsService.Current?.DriverMode?.Vision?.Trim(),
                          "Virtual", StringComparison.OrdinalIgnoreCase);

        /// <summary>회전 보정 직전에 잰 기울기[도]. 보정 뒤와 견줘 방향이 반대인지 본다.</summary>
        private double _angleBefore;

        /// <summary>T 축의 + 방향. 없으면 <see cref="NotReadyReason"/> 이 먼저 막는다.</summary>
        private RotationSense TSense =>
            StageAxis.TryParseRotation(GlassCamera()?.TAxisPositiveDir, out var s)
                ? s
                : RotationSense.CounterClockwise;

        /// <summary>
        /// 픽셀 → mm 변환. <b>실측 교정이 있으면 그것, 없으면 사양값으로 만든 것.</b>
        ///
        /// <para>사양 µm/px(VisionConfig 의 NominalMicronPerPx)은 배율을 그대로 말한다 — 렌즈와
        /// 작동거리가 정해지면 배율도 정해져 있다. 사양이 말하지 않는 것은 <b>방향</b>뿐이고,
        /// 그건 PixelUAxis/PixelVAxis 두 줄이 채운다. 그래서 실측 교정이 서기 전에도 정렬이 돈다.</para>
        ///
        /// <para>실측 교정은 렌즈 공차·작동거리 차이만큼 남은 배율 오차와 카메라 기울기를 마저
        /// 없애는 일이다 — 있으면 그쪽이 이긴다.</para>
        /// </summary>
        private PixelToStage? Calibration
        {
            get
            {
                if (_cal != null) return _cal;

                _cal = AlignCalibrationStore.Load()?.ToMatrix();
                if (_cal != null)
                {
                    Log($"실측 교정 사용 — {_cal.MicronPerPxX:F3} / {_cal.MicronPerPxY:F3} µm/px");
                    return _cal;
                }

                _cal = NominalCalibration();
                if (_cal != null)
                    Log($"사양값 교정 사용 — {_cal.MicronPerPxX:F3} µm/px · 실측 교정이 없어 " +
                        "배율에 렌즈 공차만큼 오차가 남을 수 있습니다.", LogLevel.Warning);
                return _cal;
            }
        }

        /// <summary>교정을 다시 읽게 한다 — 실측 교정을 저장한 직후에 부른다.</summary>
        public void ReloadCalibration() => _cal = null;

        /// <summary>지금 쓰고 있는 교정(없으면 null). 화면 표시용 — 계산은 <see cref="Calibration"/> 이 쓴다.</summary>
        public PixelToStage? CurrentCalibration => Calibration;

        /// <summary>실측 교정 파일이 있으면 그것, 없으면 null. 잰 날짜를 보여 주려고 따로 읽는다.</summary>
        public static AlignCalibration? MeasuredCalibration => AlignCalibrationStore.Load();

        /// <summary>사양 µm/px — 실측값과 견줄 기준. 0 이면 사양 미입력.</summary>
        public double NominalMicronPerPx => GlassCamera()?.NominalMicronPerPx ?? 0.0;

        /// <summary>사양 µm/px + 설치 방향으로 만든 교정. 둘 중 하나라도 없으면 null 이다.</summary>
        private PixelToStage? NominalCalibration()
        {
            var cam = GlassCamera();
            if (cam == null) return null;
            if (!StageAxis.TryParse(cam.PixelUAxis, out var u)) return null;
            if (!StageAxis.TryParse(cam.PixelVAxis, out var v)) return null;

            return PixelToStage.FromNominal(cam.NominalMicronPerPx, u, v);
        }

        /// <summary>정렬에 쓰는 카메라의 설정. 설정을 못 읽는다고 화면이 죽으면 안 된다.</summary>
        private CameraDeviceInfo? GlassCamera()
        {
            try
            {
                var list = new ConfigLoader()
                    .LoadVisionConfig(PathUtils.GetConfigPath(AppConstants.VisionConfigFile))
                    .VisionCameraList;

                var machine = _mainVM.GetController()?.GetMachine();
                string id = machine != null
                    ? GlassViewModel.ResolveCamId(machine.Vision, _mainVM)
                    : "CAM_GV";

                return list.FirstOrDefault(c => c.CameraId == id)
                    ?? list.FirstOrDefault(c => c.CameraId == "CAM_GV");
            }
            catch { return null; }
        }

        /// <summary>
        /// 거절선과 허용 오차.
        ///
        /// <para><b>거절선은 카메라 시야에서 나온다</b> — 화면 밖에 있는 마크는 잴 수 없다.
        /// 손으로 적은 값이 광학과 어긋나면 "너무 많이 돌아 있습니다" 대신 "마크를 못
        /// 찾았습니다"가 떠서 원인을 못 짚는다. <b>허용 오차는 레시피</b>가 정한다 —
        /// 어디까지 맞춰야 하느냐는 품종이 정하는 값이다.</para>
        /// </summary>
        private AlignLimits Limits
        {
            get
            {
                var r   = _mainVM.RecipeVM;
                var cam = GlassCamera();
                // 기선은 두 마크 사이 거리다. 마크는 Y 로만 떨어져 있으므로 Y 간격이 곧 기선이다.
                double baseline = r?.FiducialPitchYMm ?? 0;

                var lim = AlignLimits.ForCamera(
                    cam?.NominalMicronPerPx ?? 0,
                    cam?.PixelWidth  ?? 0,
                    cam?.PixelHeight ?? 0,
                    baseline);

                lim.MinScore          = r?.PatternMinScore   ?? 0.70;
                lim.AngleToleranceDeg = r?.AlignToleranceDeg ?? 0.010;
                lim.ShiftToleranceXMm = (r?.AlignToleranceXUm ?? 20.0) / 1000.0;
                lim.ShiftToleranceYMm = (r?.AlignToleranceYUm ?? 20.0) / 1000.0;
                return lim;
            }
        }

        // ── 이동 ─────────────────────────────────────────────────────────

        public async Task<string> MoveToMark1Async(CancellationToken ct)
        {
            _lastErrorPx = double.NaN;              // 새 글라스 — 앞 판의 오차와 비교하지 않는다
            await _motion.MoveToPointAsync(PointNames.GlassAlign, ct);
            return $"{PointNames.GlassAlign} 이동";
        }

        /// <summary>
        /// 회전 보정 뒤 마크1 자리로 복귀 — <b>X·Y 만</b> 티칭 값으로 되돌린다.
        /// 왜 T 를 빼는지는 <see cref="IGlassAlignService.ReturnToMark1Async"/> 참고.
        /// </summary>
        public async Task<string> ReturnToMark1Async(CancellationToken ct)
        {
            await Task.WhenAll(
                _motion.MoveAxisToPointAsync("X", PointNames.GlassAlign, ct),
                _motion.MoveAxisToPointAsync("Y", PointNames.GlassAlign, ct));

            return $"{PointNames.GlassAlign} 복귀 (X·Y 만 — T 회전 보정 유지)";
        }

        /// <summary>
        /// 마크2 자리로. 마크2 는 글라스에서 마크1 보다 +Y 쪽이므로 스테이지는 -Y 로 간다.
        /// 부호는 <see cref="GlassAlign.StageMoveToMark2"/> 한 곳에서만 정한다.
        ///
        /// <para><b>X 는 건드리지 않는다.</b> 그래서 마크2 가 화면에서 X 로 벗어난 양이 그대로
        /// 글라스 기울기가 된다 — 서 있던 X 를 유지해야 그 비교가 성립한다.</para>
        /// </summary>
        public async Task<string> MoveToMark2Async(CancellationToken ct)
        {
            var r = _mainVM.RecipeVM!;
            var move = GlassAlign.StageMoveToMark2(r.FiducialPitchXMm, r.FiducialPitchYMm);

            if (Math.Abs(move.Dy) > 1e-6) await _motion.MoveAxisRelativeAsync("Y", move.Dy, ct);

            return $"마크2 이동 ΔY {move.Dy:+0.000;-0.000} mm (X 유지)";
        }

        // ── 측정 ─────────────────────────────────────────────────────────

        public async Task<string> MeasureAsync(int slot, CancellationToken ct)
        {
            var reading = IsVirtualVision
                ? MeasureVirtual(slot)
                : await MeasureRealAsync(slot, ct);

            if (slot == 1) _mark1 = reading; else _mark2 = reading;

            if (!reading.Found)
                throw new InvalidOperationException(
                    $"마크{slot} 을(를) 찾지 못했습니다 — 최고 점수 {reading.Score:F3}, 합격 {Limits.MinScore:F2}" +
                    (IsVirtualVision ? " (가상 모드에서는 일어날 수 없습니다)" : ""));

            string msg = $"마크{slot} {reading.Score:F3} @ {reading.PxX:F1}, {reading.PxY:F1} px" +
                         (IsVirtualVision ? " · 가상 — 읽기 건너뜀" : "");
            Log(msg);
            return msg;
        }

        private async Task<MarkReading> MeasureRealAsync(int slot, CancellationToken ct)
        {
            var entry = ResolvePattern() ?? throw new InvalidOperationException("정렬 패턴을 찾지 못했습니다.");
            var scene = await CaptureGrayAsync(ct);

            var fit = entry.Definition.CheckScene(scene.Width, scene.Height);
            if (!fit.CanFind) throw new InvalidOperationException(fit.Message);
            if (fit.Fit == SceneFit.Close) Log(fit.Message, LogLevel.Warning);

            var m = PatternMatcher.Find(scene, entry.Template, new PatternSearchOptions
            {
                MinScore  = Limits.MinScore,
                ExpectedX = entry.Definition.ReferenceX,
                ExpectedY = entry.Definition.ReferenceY,
                // 마크2 는 마크1 과 거의 같은 픽셀 자리에 와야 한다 — 그 주변만 보면
                // 반복 무늬에서 엉뚱한 곳을 잡는 일이 줄어든다.
                SearchRadiusPx = slot == 2 ? 200 : entry.Definition.SearchRadiusPx,
            });

            return new MarkReading(m.Found, m.Score, m.CenterX, m.CenterY);
        }

        /// <summary>
        /// 가상 모드 — 마크 읽기를 <b>건너뛴다</b>. 이미 맞춰져 있는 것으로 본다.
        ///
        /// <para>가상 프레임에는 무늬가 없어 패턴을 찾으면 반드시 실패하고, 실패는 시퀀스 알람이
        /// 된다. 하드웨어가 없다고 선언한 PC 에서 없는 카메라 때문에 알람이 뜨면 나머지 흐름을
        /// 확인할 수가 없다.</para>
        ///
        /// <para>그래서 마크가 <b>기준 자리에 그대로 있다</b>고 답한다. 그러면 각도도 0, 어긋남도
        /// 0 이라 <b>스테이지가 움직이지 않고</b> 단계만 지나간다 — 가상에서 엉뚱한 이동을 만들지
        /// 않는 것이 중요하다.</para>
        ///
        /// <para>정렬 계산 자체가 맞는지는 <c>VirtualGlass</c> 로 시험에서 확인한다 — 화면에서
        /// 눈으로 보는 것보다 그쪽이 훨씬 촘촘하다.</para>
        /// </summary>
        private MarkReading MeasureVirtual(int slot)
        {
            var (refX, refY) = ReferencePx();
            return new MarkReading(true, 1.0, refX, refY);
        }

        /// <summary>기준 픽셀 — 등록된 패턴이 있으면 그 자리, 없으면 화면 한가운데.</summary>
        private (double X, double Y) ReferencePx()
        {
            var entry = ResolvePattern();
            if (entry != null) return (entry.Definition.ReferenceX, entry.Definition.ReferenceY);

            var cam = GlassCamera();
            double w = cam?.PixelWidth  > 0 ? cam.PixelWidth  : 1280;
            double h = cam?.PixelHeight > 0 ? cam.PixelHeight : 1024;
            return (w / 2.0, h / 2.0);
        }

        // ── 보정 ─────────────────────────────────────────────────────────

        public async Task<string> CorrectRotationAsync(CancellationToken ct)
        {
            var r = _mainVM.RecipeVM!;
            var res = GlassAlign.SolveAngleFromPitch(
                _mark1, _mark2, r.FiducialPitchXMm, r.FiducialPitchYMm, Calibration, Limits);

            if (!res.Ok) throw new InvalidOperationException(res.Message);

            _angleBefore = res.AngleDeg;
            Log(res.Message);
            if (!res.NeedsRotation) return res.Message;

            // 잰 각(반시계 +)을 T 축의 + 방향에 맞춰 뒤집는 일은 GlassAlign 한 곳에서만 한다.
            double command = GlassAlign.RotationCommand(res.AngleDeg, TSense);
            await _motion.MoveAxisRelativeAsync("T", command, ct);
            return $"{res.Message} → T {command:+0.000;-0.000}° 보정";
        }

        /// <summary>
        /// 회전이 실제로 펴졌는지 — 마크2 를 다시 재서 각도를 다시 낸다.
        ///
        /// <para><b>왜 한 장 더 찍는가</b>: X·Y 는 회전이 틀어져 있어도 맞출 수 있다. 그래서
        /// 마크1 만 보고 끝내면 T 방향을 반대로 잡아 기울기가 두 배가 된 글라스도
        /// "정렬 완료"로 나간다. 기울어진 채 인쇄된 판은 되돌릴 수 없다.</para>
        ///
        /// <para>이 단계는 마크1 검증 <b>뒤에</b> 마크2 로 옮겨 와서 부른다 — 그래야 두 측정
        /// 사이의 스테이지 이동이 레시피의 피듀셜 간격 그대로다.</para>
        /// </summary>
        public async Task<(bool Ok, string Message)> VerifyAngleAsync(CancellationToken ct)
        {
            await MeasureAsync(2, ct);

            var r = _mainVM.RecipeVM!;
            var res = GlassAlign.SolveAngleFromPitch(
                _mark1, _mark2, r.FiducialPitchXMm, r.FiducialPitchYMm, Calibration, Limits);

            if (!res.Ok) return (false, res.Message);

            if (res.WithinTolerance)
            {
                string ok = $"회전 확인 — {res.Message}";
                Log(ok, LogLevel.Success);
                return (true, ok);
            }

            // 고치기 전보다 더 기울었으면 원인이 분명하다 — T 축의 + 방향이 반대다.
            bool worse = Math.Abs(res.AngleDeg) > Math.Abs(_angleBefore) + Limits.AngleToleranceDeg;
            string msg = worse
                ? $"회전 보정 뒤 더 기울었습니다({_angleBefore:+0.000;-0.000}° → {res.AngleDeg:+0.000;-0.000}°) — " +
                  "VisionConfig 의 TAxisPositiveDir 가 반대일 수 있습니다."
                : $"회전이 허용 오차 안으로 들어오지 못했습니다 — {res.Message}";

            Log(msg, LogLevel.Error);
            return (false, msg);
        }

        public async Task<string> CorrectShiftAsync(CancellationToken ct)
        {
            await MeasureAsync(1, ct);
            var (refX, refY) = ReferencePx();

            // 이번 측정은 바로 앞 보정의 결과이기도 하다. 오차가 늘었다면 되풀이할수록
            // 벌어지므로 멈춘다 — 보정 한 번 값으로 방향이 반대라는 것을 알아낸다.
            // (기록은 지금 남긴다. 판정을 뒤로 미뤄도 이력은 이어져야 한다)
            var prog = TrackProgress(refX, refY);
            var res  = GlassAlign.SolveShift(_mark1, refX, refY, Calibration, Limits);

            if (!res.Ok) throw new InvalidOperationException(res.Message);

            Log(res.Message);

            // ★ 허용 오차 안이면 그것으로 끝이다 — 진행 검사보다 <b>먼저</b> 본다.
            //   진행 검사는 "아직 맞추는 중"일 때 방향이 반대인지 잡으려고 두는 것이지,
            //   다 맞춘 판을 몇 픽셀 흔들렸다고 물릴 자리가 아니다. 실장에서 실제로
            //   허용 오차 안이라 움직이지 않은 판이 2.7px(≈3µm) 흔들렸다고 실패로 섰다
            //   (2026-08-28 09:37, 오차 판정선 2px).
            if (!res.NeedsMove) return res.Message;

            if (!prog.Ok) throw new InvalidOperationException(prog.Message);
            if (prog.Verdict == ProgressVerdict.Stalled) Log(prog.Message, LogLevel.Warning);

            if (Math.Abs(res.DxMm) > 1e-9) await _motion.MoveAxisRelativeAsync("X", res.DxMm, ct);
            if (Math.Abs(res.DyMm) > 1e-9) await _motion.MoveAxisRelativeAsync("Y", res.DyMm, ct);

            return $"{res.Message} → 이동 완료";
        }

        public async Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct)
        {
            await MeasureAsync(1, ct);
            var (refX, refY) = ReferencePx();

            // 마지막 보정이 오차를 줄였는지 — 방향이 반대라면 여기서 드러난다.
            var prog = TrackProgress(refX, refY);
            var res  = GlassAlign.SolveShift(_mark1, refX, refY, Calibration, Limits);

            if (!res.Ok) return (false, res.Message);

            // ★ 허용 오차 안이면 통과다. 진행 검사는 그다음이다 —
            //   맞은 판을 몇 픽셀 흔들렸다고 물리면, 허용 오차를 넓힐수록 오히려 더 자주 실패한다
            //   (판정선은 2px 고정인데 허용 오차가 100µm 면 그 안에서 얼마든지 흔들린다).
            if (res.WithinTolerance)
            {
                string ok = $"정렬 완료 — {res.Message}";
                Log(ok, LogLevel.Success);
                return (true, ok);
            }

            if (!prog.Ok)
            {
                Log(prog.Message, LogLevel.Error);
                return (false, prog.Message);
            }

            string msg = $"허용 오차 안으로 들어오지 못했습니다 — {res.Message}";
            Log(msg, LogLevel.Error);
            return (false, msg);
        }

        // ── 교정 ─────────────────────────────────────────────────────────
        //
        // 사양값(NominalMicronPerPx + PixelUAxis/PixelVAxis)은 <b>교정 전 임시값</b>이다.
        // 렌즈 공차·작동거리 차이만큼 배율이 어긋나 있고, 카메라를 비뚤게 단 각도는 아예 0 으로 본다.
        // 여기서 재면 그 넷(배율·방향·기울기)이 한꺼번에 나온다.

        /// <summary>교정 이동량[mm]. 시야 반폭(0.51mm)의 60% — 크게 잡을수록 잡음에 강하지만
        /// 마크가 시야를 벗어나면 아무것도 못 잰다.</summary>
        private const double CalibMoveMm = 0.30;

        /// <summary>교정 뒤 확인 이동[mm]. 예측과 실측이 이보다 더 어긋나면 그 교정은 버린다.</summary>
        private const double CalibVerifyMm = 0.15;
        private const double CalibVerifyTolPx = 8.0;

        /// <summary>
        /// 배율 교정 — <b>마크 하나</b>로 X·Y 를 각각 밀어 보고 화면에서 몇 픽셀 가는지로 정한다.
        ///
        /// <para><b>왜 마크 하나인가</b>: 재는 것은 "스테이지를 mm 로 밀면 화면에서 몇 px 가는가"다.
        /// 마크는 따라가는 표식일 뿐이라 같은 마크 하나를 계속 보는 편이 정확하다. 마크2 를 쓰면
        /// 왕복 300mm 를 움직여야 하는 데다, 계산에 <b>레시피의 피듀셜 간격</b>이 끼어들어 그 값이
        /// 틀리면 교정이 통째로 틀린다. 두 점이 필요한 것은 <b>각도</b>이지 배율이 아니다.</para>
        ///
        /// <para><b>명령한 거리가 아니라 실측 위치를 쓴다.</b> 300µm 를 명령해 297µm 가 갔다면
        /// 그 1% 가 교정에 그대로 박힌다 — 이동 전후의 엔코더 값을 읽으면 그 항이 사라진다.</para>
        /// </summary>
        public async Task<string> CalibrateScaleAsync(CancellationToken ct)
        {
            var motion = _mainVM.GetController()?.GetMachine()?.Motion
                         ?? throw new InvalidOperationException("모션 드라이버가 없습니다.");

            string? why = NotReadyReason;
            if (why != null) throw new InvalidOperationException(why);

            Log("배율 교정 시작 — 마크1 자리에서 X·Y 를 각각 " + $"{CalibMoveMm * 1000:F0}µm 밀어 봅니다.");
            await MoveToMark1Async(ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 20_000, ct);

            // 되돌릴 자리를 기억해 둔다 — 어디서 실패하든 원래 자리로 세워 놓는다.
            double homeX = motion.GetActualPosition("X");
            double homeY = motion.GetActualPosition("Y");

            try
            {
                var p0 = await MeasureForCalibAsync(ct, "기준");

                var (mx, duX, dvX) = await ProbeAxisAsync(motion, "X", CalibMoveMm, p0, ct);
                await ReturnAxisAsync(motion, "X", homeX, ct);

                var p0b = await MeasureForCalibAsync(ct, "기준(재확인)");
                var (my, duY, dvY) = await ProbeAxisAsync(motion, "Y", CalibMoveMm, p0b, ct);
                await ReturnAxisAsync(motion, "Y", homeY, ct);

                var k = PixelToStage.FromMoves(mx, duX, dvX, my, duY, dvY)
                        ?? throw new InvalidOperationException(
                            "두 이동이 화면에서 같은 방향으로 나왔습니다 — 축이 실제로 움직이지 않았거나 " +
                            "마크를 엉뚱한 곳에서 잡았습니다.");

                // 사양과 크게 다르면 그럴듯한 숫자라도 버린다. 틀린 교정은 사양값보다 나쁘다 —
                // 사양값은 최소한 배율은 맞다.
                double nominal = NominalMicronPerPx;
                if (!k.MatchesNominal(nominal))
                    throw new InvalidOperationException(
                        $"잰 배율 {k.MicronPerPxX:F3} / {k.MicronPerPxY:F3} µm/px 가 사양 {nominal:F3} 에서 " +
                        "15% 넘게 벗어났습니다 — 저장하지 않습니다. 마크를 잘못 잡았거나 축이 덜 움직였습니다.");

                await VerifyCalibrationAsync(motion, k, homeX, homeY, ct);

                AlignCalibrationStore.Save(AlignCalibration.From(k, mx, my, DateTime.Now));
                ReloadCalibration();

                string msg = $"배율 교정 완료 — {k.MicronPerPxX:F3} / {k.MicronPerPxY:F3} µm/px · " +
                             $"카메라 {k.CameraAngleDeg:+0.00;-0.00}° " +
                             $"(사양 {nominal:F3} 대비 {(k.MicronPerPxX / nominal - 1) * 100:+0.0;-0.0}%)";
                Log(msg, LogLevel.Success);
                return msg;
            }
            finally
            {
                // 교정 도중 어디서 멈추든 서 있던 자리로 되돌린다 — 반쯤 밀린 채로 두면
                // 다음에 누르는 [Auto Align] 이 엉뚱한 자리에서 시작한다.
                try { await ReturnAxisAsync(motion, "X", homeX, CancellationToken.None); } catch { }
                try { await ReturnAxisAsync(motion, "Y", homeY, CancellationToken.None); } catch { }
            }
        }

        /// <summary>한 축을 밀고, <b>실제로 간 거리</b>와 마크가 화면에서 간 픽셀을 함께 낸다.</summary>
        private async Task<(double MovedMm, double Du, double Dv)> ProbeAxisAsync(
            IMotionDriver motion, string axis, double deltaMm, MarkReading before, CancellationToken ct)
        {
            double from = motion.GetActualPosition(axis);
            await _motion.MoveAxisRelativeAsync(axis, deltaMm, ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 20_000, ct);

            double moved = motion.GetActualPosition(axis) - from;
            if (Math.Abs(moved) < deltaMm * 0.5)
                throw new InvalidOperationException(
                    $"{axis} 축이 {moved * 1000:F1}µm 밖에 움직이지 않았습니다({deltaMm * 1000:F0}µm 명령).");

            var after = await MeasureForCalibAsync(ct, $"{axis} +{deltaMm * 1000:F0}µm");
            Log($"{axis} 실제 이동 {moved * 1000:F1}µm → 화면 Δu {after.PxX - before.PxX:+0.0;-0.0} · " +
                $"Δv {after.PxY - before.PxY:+0.0;-0.0} px");

            return (moved, after.PxX - before.PxX, after.PxY - before.PxY);
        }

        /// <summary>
        /// 교정이 실제로 맞는지 — 알려진 대각선 이동 하나를 시키고 예측 픽셀과 실측 픽셀을 견준다.
        ///
        /// <para>이동 하나·사진 한 장을 더 쓰는 대신 <b>틀린 교정이 저장되는 일</b>을 없앤다.
        /// 배율이 사양 안에 들어와도 부호가 뒤집혀 있을 수 있는데, 그건 이 단계에서만 걸린다.</para>
        /// </summary>
        private async Task VerifyCalibrationAsync(
            IMotionDriver motion, PixelToStage k, double homeX, double homeY, CancellationToken ct)
        {
            var before = await MeasureForCalibAsync(ct, "확인 전");

            double fx = motion.GetActualPosition("X");
            double fy = motion.GetActualPosition("Y");
            await Task.WhenAll(
                _motion.MoveAxisRelativeAsync("X", CalibVerifyMm, ct),
                _motion.MoveAxisRelativeAsync("Y", CalibVerifyMm, ct));
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 20_000, ct);

            double mx = motion.GetActualPosition("X") - fx;
            double my = motion.GetActualPosition("Y") - fy;

            var after = await MeasureForCalibAsync(ct, "확인 뒤");

            // 스테이지가 (mx,my) 갔으면 마크는 화면에서 그 반대로 보인다.
            var (predU, predV) = k.ToPx(-mx, -my);
            double errU = (after.PxX - before.PxX) - predU;
            double errV = (after.PxY - before.PxY) - predV;
            double err  = Math.Sqrt(errU * errU + errV * errV);

            await ReturnAxisAsync(motion, "X", homeX, ct);
            await ReturnAxisAsync(motion, "Y", homeY, ct);

            if (err > CalibVerifyTolPx)
                throw new InvalidOperationException(
                    $"확인 이동이 예측과 {err:F1}px 어긋났습니다(허용 {CalibVerifyTolPx:F0}px) — 저장하지 않습니다.");

            Log($"교정 확인 — 예측과 {err:F1}px 차이", LogLevel.Success);
        }

        private async Task ReturnAxisAsync(IMotionDriver motion, string axis, double toMm, CancellationToken ct)
        {
            double delta = toMm - motion.GetActualPosition(axis);
            if (Math.Abs(delta) < 1e-4) return;

            await _motion.MoveAxisRelativeAsync(axis, delta, ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 20_000, ct);
        }

        /// <summary>교정용 측정 — 못 찾으면 그 자리에서 세운다. 못 본 사진으로 만든 교정은 없느니만 못하다.</summary>
        private async Task<MarkReading> MeasureForCalibAsync(CancellationToken ct, string where)
        {
            var r = IsVirtualVision ? MeasureVirtual(1) : await MeasureRealAsync(1, ct);
            if (!r.Found)
                throw new InvalidOperationException(
                    $"교정 중 마크를 놓쳤습니다({where}) — 최고 점수 {r.Score:F3}, 합격 {Limits.MinScore:F2}");

            _mark1 = r;
            return r;
        }

        /// <summary>
        /// T 교정 — <b>부호</b>와 <b>눈금비</b>를 잰다. 두 마크로 각도를 재고, T 를 알려진 각만큼
        /// 돌린 뒤 다시 재서 각이 어느 쪽으로 얼마나 움직였는지 본다.
        ///
        /// <para><b>왜 마크가 둘이어야 하나</b>: "글라스가 몇 도 기울었나"는 두 점이 있어야 정의된다.
        /// 마크 하나로는 T 를 돌렸을 때 그 점이 어디로 갔는지만 알 수 있고, 그건 척 회전중심까지의
        /// 거리에 따라 달라지는 값이라 각도로 환산할 수 없다.</para>
        ///
        /// <para><b>눈금비는 지금 어디서도 검증되지 않는다.</b> MotorConfig 의 T 축 단위가 도가
        /// 아니면 모든 회전 보정이 그 배율만큼 틀리는데, 정렬은 그것을 "덜 고쳐졌다"로만 본다.</para>
        ///
        /// <para>배율 교정이 <b>먼저</b>다 — 각도 계산이 그 행렬을 쓰기 때문에, 배율이 틀린 채로
        /// T 를 재면 틀린 각도로 부호를 판정한다.</para>
        /// </summary>
        public async Task<string> CalibrateTAsync(double probeDeg, CancellationToken ct)
        {
            var motion = _mainVM.GetController()?.GetMachine()?.Motion
                         ?? throw new InvalidOperationException("모션 드라이버가 없습니다.");

            string? why = NotReadyReason;
            if (why != null) throw new InvalidOperationException(why);

            if (Math.Abs(probeDeg) < 1e-6 || Math.Abs(probeDeg) > Limits.MaxAngleDeg)
                throw new InvalidOperationException(
                    $"시험 회전각은 0 보다 크고 {Limits.MaxAngleDeg:F3}° 이하여야 합니다(거절선).");

            Log($"T 교정 시작 — 마크 두 개로 각을 재고 T 를 {probeDeg:+0.000;-0.000}° 돌려 다시 잽니다.");

            double angle0 = await MeasurePairAngleAsync(motion, ct);
            var mark1Before = _mark1;

            await _motion.MoveAxisRelativeAsync("T", probeDeg, ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 20_000, ct);

            try
            {
                double angle1 = await MeasurePairAngleAsync(motion, ct);

                double delta = angle1 - angle0;
                double gain  = delta / probeDeg;

                // 잰 각(반시계 +)이 T 명령과 같은 부호로 움직였으면 +T 는 반시계다.
                var sense = gain > 0 ? RotationSense.CounterClockwise : RotationSense.Clockwise;

                // 회전으로 마크1 이 딸려 나간 거리 ÷ 각 = 척 회전중심까지의 거리.
                var pulled = Calibration!.ToMm(_mark1.PxX - mark1Before.PxX, _mark1.PxY - mark1Before.PxY);
                double radiusMm = Math.Sqrt(pulled.X * pulled.X + pulled.Y * pulled.Y)
                                / (Math.Abs(probeDeg) * Math.PI / 180.0);

                string msg = $"T 교정 — 잰 각 {angle0:+0.000;-0.000}° → {angle1:+0.000;-0.000}° " +
                             $"(Δ{delta:+0.000;-0.000}°) · 눈금비 {gain:F3} · " +
                             $"+T = {(sense == RotationSense.Clockwise ? "CW(시계)" : "CCW(반시계)")} · " +
                             $"회전반경 {radiusMm:F0}mm";

                // 눈금비가 1 에서 크게 벗어나면 축 단위가 도가 아니다 — 부호만 맞춰 봐야 소용없다.
                if (Math.Abs(Math.Abs(gain) - 1.0) > 0.20)
                    msg += $"  ★ 눈금비가 1 에서 {Math.Abs(Math.Abs(gain) - 1.0) * 100:F0}% 벗어났습니다 — " +
                           "MotorConfig 의 T 축 단위(도)를 확인하세요.";

                string current = GlassCamera()?.TAxisPositiveDir ?? "";
                bool matches = StageAxis.TryParseRotation(current, out var cfg) && cfg == sense;
                msg += matches
                    ? "  · VisionConfig 의 TAxisPositiveDir 와 일치합니다."
                    : $"  ★ VisionConfig 의 TAxisPositiveDir='{current}' 와 다릅니다 — 바꾸세요.";

                Log(msg, matches ? LogLevel.Success : LogLevel.Warning);
                return msg;
            }
            finally
            {
                // 시험용 회전은 반드시 되돌린다 — 교정하려고 준 기울기를 글라스에 남기면 안 된다.
                try { await _motion.MoveAxisRelativeAsync("T", -probeDeg, CancellationToken.None); } catch { }
            }
        }

        /// <summary>마크1·마크2 를 재서 글라스 기울기를 낸다. 끝나면 마크1 자리로 돌아온다.</summary>
        private async Task<double> MeasurePairAngleAsync(IMotionDriver motion, CancellationToken ct)
        {
            await MoveToMark1Async(ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 20_000, ct);
            await MeasureAsync(1, ct);

            await MoveToMark2Async(ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 30_000, ct);
            await MeasureAsync(2, ct);

            var r = _mainVM.RecipeVM!;
            var res = GlassAlign.SolveAngleFromPitch(
                _mark1, _mark2, r.FiducialPitchXMm, r.FiducialPitchYMm, Calibration, Limits);

            if (!res.Ok) throw new InvalidOperationException(res.Message);

            // 마크1 자리로 되돌린다 — 다음 측정이 같은 조건에서 시작해야 두 각을 견줄 수 있다.
            await ReturnToMark1Async(ct);
            await WaitHelper.ForAllMotionDone(motion, timeoutMs: 30_000, ct);
            await MeasureAsync(1, ct);

            return res.AngleDeg;
        }

        // ── 내부 ─────────────────────────────────────────────────────────

        /// <summary>쓸 패턴 — 화면에서 고른 것, 없으면 하나뿐일 때만 그것. 여러 개면 null(고르게 한다).</summary>
        private PatternEntry? ResolvePattern()
        {
            string? name = _selectedPattern?.Invoke();
            if (string.IsNullOrEmpty(name))
            {
                var all = _repo.List();
                if (all.Count != 1) return null;
                name = all[0];
            }

            if (_pattern?.Definition.Name != name) _pattern = _repo.Load(name!);
            return _pattern;
        }

        /// <summary>
        /// 촬상 전 정착 대기[ms].
        ///
        /// <para><b>왜 필요한가</b>: 모션은 드라이브가 "안 움직인다"고 말하는 순간 끝난 것으로
        /// 보는데(<c>IsInPosition = !IsMoving</c>), 그 시점에 기구는 아직 서고 있다. 실장 로그
        /// (2026-08-27 14:57)에서 "GLASS ALIGN 도달"이 뜬 뒤 <b>이동 명령 없이</b> 마크가
        /// 465 → 538 → 549 → 560 → 580 → 670 → 749px 로 계속 흘렀다 — 0.3mm 가 넘는다.
        /// 그 사진으로 낸 보정값은 이미 지나간 자리를 가리키므로, 보정할수록 오차가 늘고
        /// "화면 축 방향이 반대일 수 있습니다"라는 엉뚱한 진단이 뜬다.</para>
        ///
        /// <para>여기 한 곳에 둔 이유: 이동하는 자리마다 대기를 넣으면 언젠가 한 곳을 빠뜨리고,
        /// 빠뜨린 그 자리가 흔들린 사진을 낸다. <b>찍기 직전</b>은 반드시 지나가는 길목이다.</para>
        /// </summary>
        private const int SettleBeforeCaptureMs = 500;

        private async Task<GrayImage> CaptureGrayAsync(CancellationToken ct)
        {
            var machine = _mainVM.GetController()?.GetMachine()
                          ?? throw new InvalidOperationException("장비가 초기화되지 않았습니다.");

            // 정착 대기와 촬상을 한 잠금 안에 둔다 — 라이브가 비켜 있는 사이에 찍어야
            // "그 사진이 라이브와 겹친 것 아니냐"를 나중에 따질 일이 없다.
            using var hold = _holdLive?.Invoke();

            // 기구가 설 때까지 기다렸다 찍는다 — 흔들린 사진으로 낸 보정은 오차를 키운다.
            await Task.Delay(SettleBeforeCaptureMs, ct).ConfigureAwait(false);

            var img = await machine.Vision.CaptureAsync(GlassViewModel.ResolveCamId(machine.Vision, _mainVM), saveToDisk: false);
            ct.ThrowIfCancellationRequested();

            if (!img.IsValid || img.PixelData == null || img.Width <= 0 || img.Height <= 0)
                throw new InvalidOperationException("카메라에서 이미지를 받지 못했습니다.");

            if (img.BitsPerPixel != 8)
                throw new InvalidOperationException($"8비트 그레이가 아닙니다({img.BitsPerPixel}bit).");

            // ★ 픽셀을 <b>복사해서</b> 들고 간다. 드라이버가 돌려주는 버퍼는 프레임마다 재사용되므로
            //   (HikrobotCamera.Grab), 다음 촬상이 일어나면 그 위에 덮인다. 라이브는 받자마자
            //   비트맵으로 복사하고 끝이라 상관없지만, 정렬은 이 배열을 패턴 매칭이 끝날 때까지
            //   들고 있다 — 매칭 도중 내용이 바뀌면 엉뚱한 자리를 잡고, 그대로 모터가 나간다.
            //   한 판에 여덟 번뿐이라 복사 비용은 문제가 되지 않는다.
            return new GrayImage((byte[])img.PixelData.Clone(), img.Width, img.Height);
        }


        /// <summary>
        /// 이번 측정이 앞선 보정보다 나아졌는지. 첫 측정이면 비교 대상이 없어 언제나 통과다.
        ///
        /// <para>사양값 교정은 배율은 맞아도 방향이 틀릴 수 있다. 그런데 방향이 틀리면 증상이
        /// 분명하다 — 보정 뒤 오차가 늘어난다. 어차피 매 판마다 찍는 사진으로 그것을 보므로
        /// 사람이 설정을 확인해 주기를 기다리지 않는다.</para>
        /// </summary>
        private ProgressCheck TrackProgress(double refPxX, double refPxY)
        {
            double dx = refPxX - _mark1.PxX;
            double dy = refPxY - _mark1.PxY;
            double errPx = Math.Sqrt(dx * dx + dy * dy);

            var check = double.IsNaN(_lastErrorPx)
                ? new ProgressCheck(ProgressVerdict.Improved, errPx, errPx, "")
                : GlassAlign.CheckProgress(_lastErrorPx, errPx);

            // 멈추는 판단을 내렸으면 비워 둔다 — 다음 시도가 옛 값과 견주지 않도록.
            _lastErrorPx = check.Ok ? errPx : double.NaN;
            return check;
        }
        private void Log(string message, LogLevel level = LogLevel.Info)
            => _mainVM.AddLog("[ALIGN] " + message, level);
    }
}
