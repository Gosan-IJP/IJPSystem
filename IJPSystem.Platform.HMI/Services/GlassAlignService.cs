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

        public GlassAlignService(MainViewModel mainVM, Func<string?>? selectedPattern = null)
        {
            _mainVM = mainVM ?? throw new ArgumentNullException(nameof(mainVM));
            _motion = new MotionServiceAdapter(mainVM);
            _selectedPattern = selectedPattern;
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
                if (r.FiducialPitchXMm <= 0 && r.FiducialPitchYMm <= 0)
                    return "레시피에 피듀셜 마크 간격이 없습니다 — 글라스 정보에 X/Y 를 넣으세요.";

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
                double baseline = Math.Sqrt((r?.FiducialPitchXMm ?? 0) * (r?.FiducialPitchXMm ?? 0)
                                          + (r?.FiducialPitchYMm ?? 0) * (r?.FiducialPitchYMm ?? 0));

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
        /// 마크2 자리로. 마크2 는 글라스에서 마크1 보다 -Y 쪽이므로 스테이지는 +Y 로 간다.
        /// 부호는 <see cref="GlassAlign.StageMoveToMark2"/> 한 곳에서만 정한다.
        /// </summary>
        public async Task<string> MoveToMark2Async(CancellationToken ct)
        {
            var r = _mainVM.RecipeVM!;
            var move = GlassAlign.StageMoveToMark2(r.FiducialPitchXMm, r.FiducialPitchYMm);

            if (Math.Abs(move.Dx) > 1e-6) await _motion.MoveAxisRelativeAsync("X", move.Dx, ct);
            if (Math.Abs(move.Dy) > 1e-6) await _motion.MoveAxisRelativeAsync("Y", move.Dy, ct);

            return $"마크2 이동 ΔX {move.Dx:+0.000;-0.000} · ΔY {move.Dy:+0.000;-0.000} mm";
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
            // 벌어지므로 여기서 멈춘다 — 보정 한 번 값으로 방향이 반대라는 것을 알아낸다.
            var prog = TrackProgress(refX, refY);
            if (!prog.Ok) throw new InvalidOperationException(prog.Message);
            if (prog.Verdict == ProgressVerdict.Stalled) Log(prog.Message, LogLevel.Warning);

            var res = GlassAlign.SolveShift(_mark1, refX, refY, Calibration, Limits);

            if (!res.Ok) throw new InvalidOperationException(res.Message);

            Log(res.Message);
            if (!res.NeedsMove) return res.Message;

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
            if (!prog.Ok)
            {
                Log(prog.Message, LogLevel.Error);
                return (false, prog.Message);
            }

            var res = GlassAlign.SolveShift(_mark1, refX, refY, Calibration, Limits);

            if (!res.Ok) return (false, res.Message);

            string msg = res.WithinTolerance
                ? $"정렬 완료 — {res.Message}"
                : $"허용 오차 안으로 들어오지 못했습니다 — {res.Message}";

            Log(msg, res.WithinTolerance ? LogLevel.Success : LogLevel.Error);
            return (res.WithinTolerance, msg);
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

        private async Task<GrayImage> CaptureGrayAsync(CancellationToken ct)
        {
            var machine = _mainVM.GetController()?.GetMachine()
                          ?? throw new InvalidOperationException("장비가 초기화되지 않았습니다.");

            var img = await machine.Vision.CaptureAsync(GlassViewModel.ResolveCamId(machine.Vision, _mainVM), saveToDisk: false);
            ct.ThrowIfCancellationRequested();

            if (!img.IsValid || img.PixelData == null || img.Width <= 0 || img.Height <= 0)
                throw new InvalidOperationException("카메라에서 이미지를 받지 못했습니다.");

            if (img.BitsPerPixel != 8)
                throw new InvalidOperationException($"8비트 그레이가 아닙니다({img.BitsPerPixel}bit).");

            return new GrayImage(img.PixelData, img.Width, img.Height);
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
