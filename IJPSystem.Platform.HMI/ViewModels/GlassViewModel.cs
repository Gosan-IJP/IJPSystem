using Dapper;
using IJPSystem.Platform.Application.Sequences;   // PointNames — GLASS ALIGN 티칭 포인트 이름
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Models.Motion;    // TeachLimitCheck — 티칭 화면과 같은 범위 검사
using IJPSystem.Platform.HMI.Common;              // Dialogs
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Windows;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;   // iCore 조명 컨트롤러(글라스뷰 조명)
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class GlassViewModel : ViewModelBase, IDisposable
    {
        // 글라스뷰 카메라 ID. 표준은 CAM_GV 이고, 옛 표기(CAM_02)로 적힌 config 도 받아준다.
        //   CameraId 는 이름이 아니라 드라이버 조회 키(CaptureAsync/GetStatus/SetLight)라
        //   코드 상수와 그 PC 의 VisionConfig 가 정확히 같아야 한다. 저장소만 바꾸면 아직
        //   옛 ID 로 적힌 장비(0호기)가 조용히 '미연결'로 뜬다 — 그래서 실행 시점에 해석한다.
        //   ※ 두 장비 config 를 모두 CAM_GV 로 갱신하면 NewCamId 만 남기고 폴백을 지울 것.
        private const string NewCamId = "CAM_GV";
        private const string OldCamId = "CAM_02";

        private readonly string CamId;   // 실제 config 에서 찾은 ID (없으면 NewCamId)

        private readonly IVisionDriver _vision;
        private readonly MainViewModel _mainVM;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _liveTimer;

        private CancellationTokenSource? _liveCts;

        // ── 조그 (글라스를 카메라 시야에 맞추는 용도) ──────────────────────────
        // 모터 제어 화면과 같은 축 인스턴스(SharedAxisList)를 그대로 쓴다 —
        // 화면이 달라도 서보/알람 상태와 속도 프로파일이 하나로 유지된다.
        public AxisViewModel? AxisX => FindAxis("X");
        public AxisViewModel? AxisY => FindAxis("Y");
        public AxisViewModel? AxisZ => FindAxis("Z");
        public AxisViewModel? AxisT => FindAxis("T");   // 회전축(모션 드라이버 식별자 "T"). 없으면 null → 버튼 비활성

        private AxisViewModel? FindAxis(string prefix) =>
            _mainVM.SharedAxisList.FirstOrDefault(a =>
                (a.Info?.Name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // 조그 단위 — 0=연속(누르는 동안 이동), 0.01=10µm, 0.1=100µm, 1=1000µm (AxisViewModel.JogUnit 규약).
        private double _jogUnit;
        public double JogUnit
        {
            get => _jogUnit;
            set
            {
                if (!SetProperty(ref _jogUnit, value)) return;
                foreach (var ax in _mainVM.SharedAxisList) ax.JogUnit = value;   // 선택 축과 무관하게 전 축 동기
                OnPropertyChanged(nameof(IsUnitContinuity));
                OnPropertyChanged(nameof(IsUnit10um));
                OnPropertyChanged(nameof(IsUnit100um));
            }
        }

        public bool IsUnitContinuity { get => JogUnit == 0;    set { if (value) JogUnit = 0; } }
        public bool IsUnit10um       { get => JogUnit == 0.01; set { if (value) JogUnit = 0.01; } }
        public bool IsUnit100um      { get => JogUnit == 0.1;  set { if (value) JogUnit = 0.1; } }

        // 조그 단위 콤보(이미지 레이아웃) — 0=Continuous, 1=10µm, 2=100µm, 3=1000µm. JogUnit 으로 환산.
        private int _jogUnitIndex;
        public int JogUnitIndex
        {
            get => _jogUnitIndex;
            set
            {
                if (!SetProperty(ref _jogUnitIndex, value)) return;
                JogUnit = value switch { 1 => 0.01, 2 => 0.1, 3 => 1.0, _ => 0.0 };
            }
        }

        // ── 카메라 상태 ────────────────────────────────────────────────────────
        private CameraStatus? _camStatus;
        public CameraStatus? CamStatus
        {
            get => _camStatus;
            private set
            {
                if (SetProperty(ref _camStatus, value))
                    OnPropertyChanged(nameof(CaptureTimeText));
            }
        }

        public string CaptureTimeText => CamStatus?.LastCaptureTime == null
            ? "-"
            : CamStatus.LastCaptureTime.Value.ToString("HH:mm:ss.fff");

        // ── 라이브 모드 ────────────────────────────────────────────────────────
        private bool _isLiveMode;
        public bool IsLiveMode
        {
            get => _isLiveMode;
            private set
            {
                if (SetProperty(ref _isLiveMode, value))
                {
                    OnPropertyChanged(nameof(IsNotLiveMode));
                    OnPropertyChanged(nameof(LiveStatusText));
                }
            }
        }
        public bool   IsNotLiveMode  => !IsLiveMode;
        public string LiveStatusText => IsLiveMode ? "LIVE" : "STOP";

        // ── FPS 표시 ──────────────────────────────────────────────────────────
        //
        // 200ms(5fps)로 두었더니 "라이브 느낌이 안 난다"는 실장 피드백(2026-08-27). 카메라는
        // MVS 벤더 SDK 로 38fps 까지 나오므로 5fps 는 카메라가 아니라 이 타이머가 만든 상한이었다.
        //
        // 66ms(≈15fps)로 내린다. 더 내리지 않는 이유는 프레임 하나가 1280×1024 = 1.31MB 라
        // 32비트 프로세스에서 초당 넘기는 양이 그대로 부담이 되기 때문이다(2026-08-27 0x80070008).
        private int _liveIntervalMs = 66;
        public int LiveIntervalMs
        {
            get => _liveIntervalMs;
            set
            {
                if (SetProperty(ref _liveIntervalMs, Math.Clamp(value, 50, 2000)))
                {
                    _liveTimer.Interval = TimeSpan.FromMilliseconds(_liveIntervalMs);
                    OnPropertyChanged(nameof(FpsText));
                }
            }
        }
        public string FpsText => $"{1000.0 / LiveIntervalMs:F1} fps";

        // ── 현재 표시 이미지 경로 ──────────────────────────────────────────────
        // 디스크에 있는 마지막 이미지 경로(캡쳐/열기). 라이브 프레임은 파일이 없으므로 갱신하지 않는다.
        private string? _currentImagePath;
        public string? CurrentImagePath
        {
            get => _currentImagePath;
            private set
            {
                if (!SetProperty(ref _currentImagePath, value)) return;
                CurrentFrame = string.IsNullOrEmpty(value) ? null : LoadFrozen(value);
            }
        }

        // 화면에 그려지는 프레임. 라이브는 픽셀 버퍼에서 직접(파일 없음), 그 외는 파일에서 로드.
        private BitmapSource? _currentFrame;
        public BitmapSource? CurrentFrame
        {
            get => _currentFrame;
            private set
            {
                if (!SetProperty(ref _currentFrame, value)) return;
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(HasNoImage));
            }
        }

        public bool HasImage   => CurrentFrame != null;
        public bool HasNoImage => CurrentFrame == null;

        // 파일 잠금을 피하려고 전부 읽어들인 뒤 Freeze
        private static BitmapSource? LoadFrozen(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption   = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource     = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // ── 총 캡쳐 카운트 ────────────────────────────────────────────────────
        private int _captureCount;
        public int CaptureCount
        {
            get => _captureCount;
            private set => SetProperty(ref _captureCount, value);
        }

        // ── 조명 강도 ──────────────────────────────────────────────────────────
        //
        // ※ 이 값은 <b>하드웨어까지 가지 않는다</b>. 실장 드라이버의 SetLightIntensity 는 상태
        //   플래그만 바꾼다 — 글라스뷰 조명(iCore iPulse)은 밝기 레지스터가 매뉴얼에 없고,
        //   정격을 넘기면 LED 가 파손되므로 밝기는 iPulse Configurator 로만 세팅한다.
        //   화면에서 밝기를 조절하고 싶으면 <see cref="ExposureMs"/> 쪽을 쓴다.
        private int _lightIntensity = 200;
        public int LightIntensity
        {
            get => _lightIntensity;
            set
            {
                if (SetProperty(ref _lightIntensity, value))
                    _vision.SetLightIntensity(CamId, value);
            }
        }

        // ── 노출(ms) ──────────────────────────────────────────────────────────
        //
        // 조명 밝기를 못 바꾸는 대신 화면 밝기를 여기서 조절한다 — SetExposure 는 MVS SDK 를 타고
        // 카메라 하드웨어까지 실제로 나간다(HikrobotCamera.SetExposureMs → GenICam ExposureTime).
        //
        // 게인이 아니라 노출을 화면에 올린 이유: 게인을 올리면 노이즈가 함께 커져 패턴 매칭
        // 점수가 떨어진다. 정렬 카메라에서는 점수 여유가 곧 안전 여유다(마크2 가 0.764 까지
        // 내려간 적이 있다 — 합격선 0.70).

        /// <summary>노출의 하한·상한[ms]. 상한은 라이브 주기(66ms)를 크게 넘지 않게 잡았다 —
        /// 노출이 주기보다 길면 프레임률이 노출에 묶여 라이브가 뚝뚝 끊긴다.</summary>
        private const double MinExposureMs = 0.05, MaxExposureMs = 100.0;

        /// <summary>VisionConfig 의 DefaultExposureMs — 되돌릴 자리. 이 값이 진짜 주인이다.</summary>
        public double ConfigExposureMs { get; private set; }

        private double _exposureMs;

        /// <summary>
        /// 지금 카메라에 걸린 노출[ms]. <b>이번 실행에서만 산다</b> — 파일에 쓰지 않는다.
        ///
        /// <para>값의 주인은 <c>VisionConfig.json</c> 의 <c>DefaultExposureMs</c> 다. 여기서
        /// 저장까지 하게 두면 "이 장비가 어떤 노출로 도는가"의 답이 두 곳이 되고, 둘이 갈라졌을 때
        /// 어느 쪽이 실제인지 알 길이 없다. 화면은 <b>지금 눈으로 맞춰 보는 자리</b>로만 둔다 —
        /// 쓸 만한 값을 찾으면 config 에 적어 넣는 것은 사람이 한다.</para>
        /// </summary>
        public double ExposureMs
        {
            get => _exposureMs;
            set
            {
                double ms = Math.Clamp(value, MinExposureMs, MaxExposureMs);
                if (!SetProperty(ref _exposureMs, ms))
                {
                    // 범위 밖 값을 잘라 냈는데 결과가 이전과 같으면 알림이 나가지 않는다 →
                    // 입력칸에는 방금 친 "500" 이 그대로 남아, 걸리지도 않은 값을 걸린 것으로 읽는다.
                    if (Math.Abs(ms - value) > 1e-9) OnPropertyChanged(nameof(ExposureMs));
                    return;
                }

                _vision.SetExposure(CamId, ms);
                OnPropertyChanged(nameof(IsExposureOverridden));
                _resetExposure?.RaiseCanExecuteChanged();
                _mainVM.AddLog(
                    $"[VISION] Glass: 노출 {ms:F2}ms — 이번 실행에만 적용됩니다" +
                    $"(VisionConfig 의 DefaultExposureMs 는 {ConfigExposureMs:F2}ms 그대로).",
                    LogLevel.Info);
            }
        }

        /// <summary>설정값과 다른가 — 다르면 되돌리기 버튼이 켜진다.</summary>
        public bool IsExposureOverridden => Math.Abs(ExposureMs - ConfigExposureMs) > 0.005;

        /// <summary>설정값(VisionConfig)으로 되돌린다.</summary>
        private void ResetExposure() => ExposureMs = ConfigExposureMs;

        private RelayCommand? _resetExposure;

        /// <summary>
        /// 시작 노출 — 드라이버가 config 의 <c>DefaultExposureMs</c> 로 채워 둔 상태에서 읽는다.
        ///
        /// <para>속성이 아니라 <b>필드에 직접</b> 넣는다. 속성으로 넣으면 화면을 여는 것만으로
        /// 카메라에 쓰기가 나가고 "노출 변경" 로그가 남는다 — 아무도 바꾸지 않았는데.</para>
        /// </summary>
        private void InitExposure()
        {
            double cfg = CamStatus?.ExposureMs ?? 0;
            if (cfg <= 0) cfg = 10.0;   // config 를 못 읽었을 때의 안전값 — 빈 칸을 보여 주지 않는다

            ConfigExposureMs = Math.Clamp(cfg, MinExposureMs, MaxExposureMs);
            _exposureMs      = ConfigExposureMs;
        }

        // ── 처리 중 상태 ──────────────────────────────────────────────────────
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                // 정렬 버튼도 이 값을 조건으로 쓴다 — 알려 주지 않으면 촬상 중에도 눌린다.
                if (SetProperty(ref _isBusy, value)) RefreshAlignCommands();
            }
        }

        // ── 크로스라인 (기준선) ───────────────────────────────────────────────
        // 패턴인쇄 화면(VisualMonitorViewModel)과 같은 규약: 표시 여부 + 화면 비율 좌표.
        // 글라스 모서리를 이 선에 맞춰 조그하는 용도라, 라이브·정지 어느 상태에서도 유지된다.
        // 기본은 꺼둔다 — 항상 켜져 있으면 캡쳐 이미지를 그냥 보고 싶을 때 방해가 된다.
        private bool _crossLineVisible;
        public bool CrossLineVisible
        {
            get => _crossLineVisible;
            set => SetProperty(ref _crossLineVisible, value);
        }

        private double _crossXRatio = 0.5, _crossYRatio = 0.5;
        public double CrossXRatio
        {
            get => _crossXRatio;
            set => SetProperty(ref _crossXRatio, Math.Clamp(value, 0, 1));
        }
        public double CrossYRatio
        {
            get => _crossYRatio;
            set => SetProperty(ref _crossYRatio, Math.Clamp(value, 0, 1));
        }

        // ── 커맨드 ────────────────────────────────────────────────────────────
        public ICommand StartLiveCommand  { get; }
        public ICommand StopLiveCommand   { get; }
        public ICommand ToggleLiveCommand { get; }
        public ICommand CaptureCommand    { get; }
        public ICommand LightOnCommand    { get; }
        public ICommand LightOffCommand   { get; }
        public ICommand ToggleLightCommand { get; }
        public ICommand ResetExposureCommand { get; }
        public ICommand OpenImageCommand  { get; }
        public ICommand ToggleCrossLineCommand { get; }
        public ICommand CenterCrossCommand     { get; }
        public ICommand AutoAlignCommand       { get; }
        public ICommand StopAutoAlignCommand   { get; }
        public ICommand MoveMark1Command       { get; }
        public ICommand MoveMark2Command       { get; }
        public ICommand SaveCurrentAsMark1Command { get; }

        // 정렬 버튼들은 조건이 바뀌면 <b>직접 흔들어 줘야</b> 다시 판정한다 — 여기 RelayCommand 는
        // CommandManager 를 쓰지 않아서 InvalidateRequerySuggested 로는 꿈쩍도 하지 않는다.
        // (그래서 정렬이 도는 동안 [Stop] 이 계속 꺼져 있었다 — 세울 수가 없었다)
        private readonly RelayCommand _autoAlign, _stopAutoAlign, _moveMark1, _moveMark2, _teachMark1;

        /// <summary>정렬이 시작·종료됐다 — 표시와 버튼을 함께 갱신한다. 둘을 갈라 놓으면
        /// 언젠가 한쪽만 부르는 자리가 생기고, 그 자리에서 [Stop] 이 안 켜진다.</summary>
        private void NotifyAligningChanged()
        {
            OnPropertyChanged(nameof(IsAutoAligning));
            RefreshAlignCommands();
        }

        /// <summary>
        /// 정렬 버튼들의 활성 조건이 바뀌었다고 알린다.
        /// <para><c>?.</c> 는 생성자가 끝나기 전에 불릴 자리(<see cref="IsBusy"/>) 때문이다 —
        /// 이 커맨드들은 생성자 끝에서 만들어진다.</para>
        /// </summary>
        private void RefreshAlignCommands()
        {
            _autoAlign?.RaiseCanExecuteChanged();
            _stopAutoAlign?.RaiseCanExecuteChanged();
            _moveMark1?.RaiseCanExecuteChanged();
            _moveMark2?.RaiseCanExecuteChanged();
            _teachMark1?.RaiseCanExecuteChanged();
            _calibScale?.RaiseCanExecuteChanged();
            _calibT?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 적용된 레시피가 자동 정렬을 쓰는가. 안 쓰면 이 화면의 정렬 버튼은 전부 잠근다.
        ///
        /// <para><b>편집 중인 값이 아니라 적용된(APPLY) 값</b>을 본다 — 이 버튼들이 실제로 부르는
        /// 시퀀스가 같은 값을 보기 때문이다(<c>GlassAlignService.IsEnabled</c>). 편집 버퍼를 보면
        /// 화면은 잠겨 있는데 인쇄는 정렬을 하는, 서로 다른 두 사실이 동시에 참이 된다.</para>
        /// </summary>
        public bool AutoAlignEnabled => _mainVM.RecipeVM?.ActiveAutoAlignEnabled ?? false;

        /// <summary>잠긴 이유를 화면에 한 줄로 남긴다 — 회색 버튼만 있으면 고장으로 읽는다.</summary>
        public bool AutoAlignDisabled => !AutoAlignEnabled;

        // ── 교정 ──────────────────────────────────────────────────────────
        private readonly Services.GlassAlignService _align;

        public ICommand CalibrateScaleCommand { get; }
        public ICommand CalibrateTCommand     { get; }
        private readonly RelayCommand _calibScale, _calibT;

        /// <summary>T 교정에서 시험 삼아 돌려 볼 각[도]. 거절선 안쪽의 작은 값이면 된다.</summary>
        private const double TProbeDeg = 0.05;

        private string _calibrationText = "";
        /// <summary>
        /// 지금 쓰는 배율 한 줄 — 실측 교정인지 사양값인지가 <b>먼저</b> 보여야 한다.
        ///
        /// <para>둘은 믿는 정도가 다르다. 사양값은 배율만 맞고 카메라 기울기는 0 으로 보므로,
        /// 정렬이 자꾸 덜 맞을 때 제일 먼저 확인할 곳이 여기다.</para>
        /// </summary>
        public string CalibrationText
        {
            get => _calibrationText;
            private set => SetProperty(ref _calibrationText, value);
        }

        private bool _isMeasuredCalibration;
        /// <summary>실측 교정인가(아니면 사양값). 화면에서 색으로 가른다.</summary>
        public bool IsMeasuredCalibration
        {
            get => _isMeasuredCalibration;
            private set
            {
                // [Calibrate T] 는 실측 배율이 있어야 켜진다 — 아래 커맨드 조건 참고.
                if (SetProperty(ref _isMeasuredCalibration, value)) RefreshAlignCommands();
            }
        }

        /// <summary>배율 한 줄을 다시 만든다 — 화면에 들어올 때와 교정 직후에 부른다.</summary>
        private void RefreshCalibrationText()
        {
            var measured = Services.GlassAlignService.MeasuredCalibration;
            if (measured != null)
            {
                var k = measured.ToMatrix();
                IsMeasuredCalibration = true;
                CalibrationText =
                    $"{k.MicronPerPxX:F3} / {k.MicronPerPxY:F3} µm/px · 카메라 {k.CameraAngleDeg:+0.00;-0.00}°" +
                    $"   ·   {measured.MeasuredAt:yyyy-MM-dd HH:mm} 실측";
                return;
            }

            IsMeasuredCalibration = false;
            double nominal = _align.NominalMicronPerPx;
            CalibrationText = nominal > 0
                ? $"{nominal:F3} µm/px · 사양값 — 실측 교정 없음(렌즈 공차만큼 오차가 남습니다)"
                : "교정 없음 — VisionConfig 의 NominalMicronPerPx 를 채우거나 교정을 하세요.";
        }

        private async Task RunCalibrationAsync(bool scale)
        {
            if (IsAutoAligning) return;

            _alignCts = new CancellationTokenSource();
            NotifyAligningChanged();

            using var running = Application.Sequences.GlassAlignServices.BeginRun();
            try
            {
                AutoAlignStatus = scale ? "배율 교정 중 — 마크1 자리에서 X·Y 를 밀어 봅니다"
                                        : "T 교정 중 — 마크 두 개로 각을 재고 T 를 돌려 다시 잽니다";

                AutoAlignStatus = scale
                    ? await _align.CalibrateScaleAsync(_alignCts.Token)
                    : await _align.CalibrateTAsync(TProbeDeg, _alignCts.Token);
            }
            catch (OperationCanceledException)
            {
                AutoAlignStatus = "중지 — 교정";
            }
            catch (Exception ex)
            {
                AutoAlignStatus = $"교정 실패 — {ex.Message}";
                _mainVM.AddLog($"[ALIGN] {AutoAlignStatus}", LogLevel.Error);
            }
            finally
            {
                _alignCts?.Dispose();
                _alignCts = null;
                NotifyAligningChanged();
                RefreshCalibrationText();
            }
        }

        /// <summary>
        /// VisionConfig 에 실제로 들어 있는 글라스뷰 카메라 ID 를 고른다 — CAM_GV 우선, 없으면 옛 CAM_02.
        /// 둘 다 없으면 표준 ID 를 그대로 쓴다(미연결로 표시되고, 로그로 원인을 알 수 있게 한다).
        /// </summary>
        /// <summary>글라스뷰 카메라 ID 해석. 화면 없이 찍는 쪽(정렬 시퀀스)도 같은 규칙을 써야 한다.</summary>
        public static string ResolveCamId(IVisionDriver vision, MainViewModel mainVM)
        {
            var ids = vision?.GetAllStatus()?.Select(s => s.CameraId).ToList();
            if (ids == null || ids.Count == 0) return NewCamId;

            if (ids.Any(id => string.Equals(id, NewCamId, StringComparison.OrdinalIgnoreCase)))
                return NewCamId;

            if (ids.Any(id => string.Equals(id, OldCamId, StringComparison.OrdinalIgnoreCase)))
            {
                mainVM.AddLog(
                    $"[VISION] 글라스뷰 카메라를 옛 ID '{OldCamId}' 로 찾았습니다 — " +
                    $"VisionConfig.json 의 CameraId 를 '{NewCamId}' 로 갱신하세요.", LogLevel.Warning);
                return OldCamId;
            }

            mainVM.AddLog(
                $"[VISION] VisionConfig 에 글라스뷰 카메라('{NewCamId}'/'{OldCamId}')가 없습니다 — 미연결로 표시됩니다.",
                LogLevel.Warning);
            return NewCamId;
        }

        public GlassViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            _vision = mainVM.GetController().GetMachine().Vision;
            CamId   = ResolveCamId(_vision, mainVM);

            StartLiveCommand  = new RelayCommand(_ => StartLive(),              _ => !IsLiveMode && !IsBusy);
            StopLiveCommand   = new RelayCommand(_ => StopLive(),               _ => IsLiveMode);
            ToggleLiveCommand = new RelayCommand(_ => { if (IsLiveMode) StopLive(); else StartLive(); });
            // 라이브 중에도 단발 캡쳐 허용(실장 피드백 2026-07-23) — IsBusy 게이트가 라이브 틱과
            // 겹침을 막아주고, 캡쳐 순간의 프레임이 파일로 저장된 뒤 라이브는 그대로 이어진다.
            CaptureCommand    = new RelayCommand(async _ => await ExecuteCaptureAsync(), _ => !IsBusy);
            LightOnCommand   = new RelayCommand(_ => ExecuteLight(true),  _ => !IsBusy);
            LightOffCommand  = new RelayCommand(_ => ExecuteLight(false), _ => !IsBusy);
            // 상단 버튼은 토글 하나 — 두 버튼으로 나누면 지금 켜져 있는지 화면에서 알 수 없다.
            ToggleLightCommand = new RelayCommand(_ => ExecuteLight(!IsLightOn), _ => !IsBusy);
            ResetExposureCommand = _resetExposure =
                new RelayCommand(_ => ResetExposure(), _ => IsExposureOverridden);
            OpenImageCommand = new RelayCommand(_ => ExecuteOpenImage(),  _ => !IsLiveMode);
            // 크로스라인은 카메라와 무관한 화면 오버레이라 조건 없이 언제나 조작 가능하다.
            ToggleCrossLineCommand = new RelayCommand(_ => CrossLineVisible = !CrossLineVisible);
            CenterCrossCommand     = new RelayCommand(_ => { CrossXRatio = 0.5; CrossYRatio = 0.5; });

            // 카메라 상태 폴링 (500ms)
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => CamStatus = _vision.GetStatus(CamId);
            _statusTimer.Start();

            // 라이브 캡쳐 타이머.
            //
            // 우선순위를 Render 로 올린다 — DispatcherTimer 의 기본값은 Background(4) 라
            // 입력(5)·렌더(7)보다 뒤로 밀린다. 조그 버튼을 누르고 있거나 로그가 흐르는 동안
            // 정확히 그때 화면이 굳는데, 라이브를 보는 이유가 바로 그 순간이다.
            // 촬상 자체는 await 로 백그라운드에서 도니 UI 를 붙잡지 않는다.
            _liveTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(_liveIntervalMs),
            };
            _liveTimer.Tick += async (_, _) => await LiveTickAsync();

            CamStatus = _vision.GetStatus(CamId);
            InitExposure();          // 드라이버가 config 값으로 채워 둔 뒤라야 읽힌다

            // 정렬(패턴 매칭)은 별도 VM 이 맡는다 — 카메라·조명·조그와 성격이 다르고,
            // 화면 없이 값만 검증할 수 있어야 한다.
            Align = new Vision.PatternAlignViewModel(() => CurrentFrame, _mainVM.AddLog);
            BindMinScoreToRecipe();

            // 정렬 시퀀스가 쓸 서비스를 여기서 갈아 끼운다 — 이 화면에서 고른 패턴을 알려 줄 수 있어
            // 등록된 패턴이 여러 개여도 어느 것으로 찾을지가 정해진다.
            // 세 번째 인자 — 정렬이 사진을 찍는 순간에만 라이브를 비키게 하는 잠금.
            // 화면 없이 도는 시퀀스에서는 이 서비스가 없거나 잠금이 걸려도 라이브 자체가 없다.
            // 교정은 시퀀스가 부르는 일이 아니라 이 화면에서 사람이 누르는 일이라, 인터페이스가
            // 아니라 구체 타입을 들고 있는다 — IGlassAlignService 는 단계가 부르는 것만 담는다.
            _align = new Services.GlassAlignService(_mainVM, () => Align.SelectedPattern, HoldLiveForCapture);
            Application.Sequences.GlassAlignServices.Current = _align;

            // 자동 정렬 — 도는 동안에는 다시 누르지 못하게 하고, 세울 버튼을 따로 둔다.
            AutoAlignCommand = _autoAlign = new RelayCommand(async _ => await RunAutoAlignAsync(),
                                                    _ => AutoAlignEnabled && !IsAutoAligning && !IsBusy);

            // [Stop] 만은 레시피 설정을 보지 않는다 — 이미 돌고 있는 것을 세우는 일이라,
            // 설정이 무엇이든 세울 수 있어야 한다. 잠글 수 있는 것은 시작하는 쪽뿐이다.
            StopAutoAlignCommand = _stopAutoAlign = new RelayCommand(_ => _alignCts?.Cancel(),
                                                    _ => IsAutoAligning);

            // 정렬 자리로 눈으로 확인하러 갈 수 있어야 한다. 마크가 시야에 들어오는지,
            // 간격이 맞는지는 결국 렌즈로 봐야 안다 — 자동 정렬을 돌려 실패 메시지로
            // 알아내는 것보다 여기서 한 번 가 보는 편이 빠르다.
            MoveMark1Command = _moveMark1 = new RelayCommand(async _ => await MoveToMarkAsync(1),
                                                _ => AutoAlignEnabled && !IsAutoAligning && !IsBusy);
            MoveMark2Command = _moveMark2 = new RelayCommand(async _ => await MoveToMarkAsync(2),
                                                _ => AutoAlignEnabled && !IsAutoAligning && !IsBusy);

            SaveCurrentAsMark1Command = _teachMark1 = new RelayCommand(_ => SaveCurrentAsMark1(),
                                                _ => AutoAlignEnabled && !IsAutoAligning && !IsBusy);

            // 교정 — 배율이 먼저다. 각도 계산이 배율 행렬을 쓰므로, 배율이 틀린 채로 T 를 재면
            // 틀린 각도로 부호를 판정한다. 그래서 T 쪽은 실측 교정이 서기 전에는 잠가 둔다.
            CalibrateScaleCommand = _calibScale = new RelayCommand(
                async _ => await RunCalibrationAsync(scale: true),
                _ => AutoAlignEnabled && !IsAutoAligning && !IsBusy);
            CalibrateTCommand = _calibT = new RelayCommand(
                async _ => await RunCalibrationAsync(scale: false),
                _ => AutoAlignEnabled && !IsAutoAligning && !IsBusy && IsMeasuredCalibration);

            RefreshCalibrationText();
        }

        // ── 현재 위치를 GLASS ALIGN 티칭 자리로 저장 ──────────────────────────
        //
        // 마크가 시야 한가운데 오도록 조그로 맞춰 놓고 그 자리를 그대로 굳히는 버튼이다.
        // 티칭 화면까지 가서 포인트를 고르고 [현재값 적용] → [저장] 을 누르는 길이 이미 있지만,
        // 정렬을 맞추는 동안에는 눈이 화면에 붙어 있어서 그 왕복이 실제로 잘 안 일어난다.

        /// <summary>
        /// 지금 축 위치를 활성 레시피의 <c>GLASS ALIGN</c> 포인트에 덮어쓴다.
        ///
        /// <para><b>이미 있는 축만 고친다.</b> 포인트에 없는 축을 새로 넣지 않는 이유는,
        /// 이 버튼이 정하는 것은 "어디로 갈지"이지 "어느 축이 참여할지"가 아니기 때문이다 —
        /// 축 구성을 바꾸는 일은 티칭 화면이 할 일이다.</para>
        ///
        /// <para>저장 범위 검사는 티칭 화면과 <b>같은 검사</b>를 쓴다. 여기만 무르면
        /// 범위 밖 좌표가 이 문으로 들어와 앉는다.</para>
        /// </summary>
        private void SaveCurrentAsMark1()
        {
            var recipe = _mainVM.RecipeVM;
            string? name = recipe?.ActiveRecipeName;
            if (recipe == null || string.IsNullOrEmpty(name))
            {
                _mainVM.AddLog("[ALIGN] 현재 위치 저장 실패 — 적용된 레시피가 없습니다.", LogLevel.Warning);
                Dialogs.Show("적용된 레시피가 없습니다.", "GLASS ALIGN 저장",
                             MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var axes = _mainVM.SharedAxisList;
            var positions = axes.ToDictionary(a => a.Info.Name, a => a.Status?.CurrentPos ?? 0.0);

            // 티칭 화면과 같은 범위 검사 — 통과 못 하면 저장하지 않는다.
            var outOfRange = TeachLimitCheck.Find(
                new[] { (PointNames.GlassAlign,
                         (IReadOnlyDictionary<string, double>)positions,
                         (IReadOnlyDictionary<string, bool>?)null) },
                axes.Select(a => a.Info));

            if (outOfRange.Count > 0)
            {
                Dialogs.Show(TeachLimitCheck.Message(outOfRange, recipe.CurrentLanguage == "EN"),
                             "GLASS ALIGN 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
                _mainVM.AddLog($"[ALIGN] 현재 위치 저장 거부 — 허용 범위 밖 {outOfRange.Count}건", LogLevel.Warning);
                return;
            }

            string preview = string.Join(", ", positions.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value:F3}"));
            if (Dialogs.Show($"[{name}] 의 {PointNames.GlassAlign} 자리를 지금 위치로 바꿉니다.\n\n{preview}\n\n계속할까요?",
                             "GLASS ALIGN 저장", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            try
            {
                int changed = WriteGlassAlignPoint(recipe, name!, positions);
                if (changed == 0)
                {
                    _mainVM.AddLog($"[ALIGN] {PointNames.GlassAlign} 포인트에 저장할 축이 없습니다 — " +
                                   "티칭 화면에서 먼저 포인트를 만드세요.", LogLevel.Warning);
                    return;
                }

                // 두 화면(레시피·티칭)이 같은 표를 각자 메모리에 들고 있다 — 둘 다 다시 읽혀야
                // 방금 바꾼 값이 화면에 보이고, 스냅샷이 갱신돼야 시퀀스가 새 자리로 간다.
                recipe.ReloadTeachingPoints();
                recipe.RefreshActivePointsSnapshot();

                _mainVM.AddLog($"[ALIGN] {PointNames.GlassAlign} 저장 완료({changed}축) — {preview}", LogLevel.Success);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[ALIGN] {PointNames.GlassAlign} 저장 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show("저장하지 못했습니다.\n" + ex.Message, "GLASS ALIGN 저장",
                             MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>이미 있는 행만 UPDATE 한다. 바꾼 축 수를 돌려준다(0 이면 포인트가 없다).</summary>
        private static int WriteGlassAlignPoint(
            RecipeViewModel recipe, string recipeName, IReadOnlyDictionary<string, double> positions)
        {
            using var db = new SqliteConnection(recipe.DbConnectionString);
            db.Open();

            int recipeId = db.QueryFirstOrDefault<int>(
                "SELECT Id FROM Recipes WHERE Name = @name", new { name = recipeName });
            if (recipeId == 0) throw new InvalidOperationException($"레시피를 찾을 수 없습니다: {recipeName}");

            using var trans = db.BeginTransaction();
            int changed = 0;
            foreach (var p in positions)
            {
                changed += db.Execute(
                    @"UPDATE RecipeDetails_Position SET PosValue = @val
                       WHERE RecipeId = @recipeId AND PointName = @pName AND AxisName = @aName",
                    new { val = p.Value, recipeId, pName = PointNames.GlassAlign, aName = p.Key }, trans);
            }
            trans.Commit();
            return changed;
        }

        /// <summary>글라스 정렬 — 패턴 등록·저장·찾기.</summary>
        public Vision.PatternAlignViewModel Align { get; }

        /// <summary>
        /// 합격 점수는 레시피가 주인이다 — 여기로 밀어 넣고, 레시피에서 바뀌면 따라간다.
        ///
        /// <para>글라스 화면에서 고칠 수 있게 두면 어느 기준으로 찾은 결과인지가 레시피에
        /// 남지 않는다. 그래서 화면은 보여 주기만 하고, 값은 이 한 방향으로만 흐른다.</para>
        ///
        /// <para>※ <b>Dispose 에서 반드시 떼어낸다.</b> RecipeViewModel 은 프로그램이 뜨는 동안
        /// 계속 살아 있고, 이 화면은 들어올 때마다 새로 만들어진다(MainViewModel 의 네비게이션은
        /// <c>new GlassViewModel(this)</c>). 핸들러가 <c>Align</c> 을 통해 <c>this</c> 를 잡으므로,
        /// 떼지 않으면 <b>화면에 들어온 횟수만큼 VM 이 통째로 살아남는다</b> — VM 하나에
        /// 라이브 버퍼(1280×1024 WriteableBitmap 2장 ≈ 2.6MB)가 붙어 있어 32비트 프로세스의
        /// 주소공간이 금세 마른다(2026-08-27 정렬 시험 중 0x80070008).</para>
        /// </summary>
        private void BindMinScoreToRecipe()
        {
            var recipe = _mainVM.RecipeVM;
            if (recipe == null) return;   // 초기화 순서에 따라 아직 없을 수 있다

            Align.MinScore = recipe.PatternMinScore;

            _recipeForMinScore = recipe;
            _minScoreHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(RecipeViewModel.PatternMinScore))
                    Align.MinScore = recipe.PatternMinScore;

                // 다른 레시피를 적용하면 자동 정렬 사용 여부도 함께 바뀐다 — 그때 정렬 버튼이
                // 스스로 다시 판정하게 한다. 구독을 하나 더 만들지 않고 여기 얹는 이유는,
                // 떼어낼 곳도 하나여야 위의 주석대로 새지 않기 때문이다.
                if (e.PropertyName == nameof(RecipeViewModel.ActiveAutoAlignEnabled))
                {
                    OnPropertyChanged(nameof(AutoAlignEnabled));
                    OnPropertyChanged(nameof(AutoAlignDisabled));
                    RefreshAlignCommands();
                }
            };
            recipe.PropertyChanged += _minScoreHandler;
        }

        // 떼어내려면 구독한 대상과 핸들러를 둘 다 들고 있어야 한다(익명 람다는 -= 로 못 뗀다).
        private RecipeViewModel? _recipeForMinScore;
        private System.ComponentModel.PropertyChangedEventHandler? _minScoreHandler;


        // ── 자동 정렬 ─────────────────────────────────────────────────────
        //
        // 시퀀스 화면의 GLASS ALIGN 과 같은 동작이다 — 단계 정의를 그대로 가져다 돌린다.
        // 순서를 여기서 다시 쓰면 두 곳이 갈라지고, 갈라진 쪽이 실제로 도는 쪽일 때가 온다.

        private CancellationTokenSource? _alignCts;

        /// <summary>정렬이 도는 중인가.</summary>
        public bool IsAutoAligning => _alignCts != null;

        private string _autoAlignStatus = "";

        /// <summary>지금 어느 단계인지. 13단계가 한참 걸려 표시가 없으면 멈춘 줄 안다.</summary>
        public string AutoAlignStatus
        {
            get => _autoAlignStatus;
            private set => SetProperty(ref _autoAlignStatus, value);
        }

        /// <summary>마크1 을 재 두었나. 마크2 의 정렬 확인은 이 값이 있어야 성립한다.</summary>
        private bool _mark1Measured;

        /// <summary>
        /// 정렬 마크 자리로 이동하고, 거기서 마크를 한 번 잰다.
        ///
        /// <para><b>마크1</b> 은 GLASS ALIGN 으로 <b>절대 이동</b>한다 — 어디에 서 있든 같은 자리에서
        /// 시작해야 결과가 서 있던 자리에 따라 달라지지 않는다. 이동한 뒤 마크를 재서 기억한다.</para>
        ///
        /// <para><b>마크2</b> 는 <b>지금 자리에서 -Y 로만</b> 간다(절대 이동을 하지 않는다).
        /// X 가 고정이라야 <b>마크2 가 화면에서 X 로 벗어난 양이 곧 글라스 기울기</b>가 되고,
        /// 그래서 눌러 본 것만으로 정렬 여부를 알 수 있다. 절대 이동을 끼우면 조그로 맞춰 둔 X 가
        /// 날아가서 그 비교가 성립하지 않는다. 부호는 <c>GlassAlign.StageMoveToMark2</c>
        /// 한 곳에서만 정하므로 여기서 다시 쓰지 않는다.</para>
        ///
        /// <para>재는 데 실패해도(패턴 미등록·카메라 오류) <b>이동은 실패로 만들지 않는다</b> —
        /// 마크를 눈으로 보러 가는 길이 사진 한 장 때문에 막히면 안 된다.</para>
        /// </summary>
        private async Task MoveToMarkAsync(int slot)
        {
            var align = Application.Sequences.GlassAlignServices.Current;
            if (align == null) { AutoAlignStatus = "정렬 서비스가 연결되지 않았습니다."; return; }

            // 라이브는 켜 둔다 — 마크 자리로 가는 동안을 보라고 만든 버튼이다.
            // 재는 순간만 CaptureGrayAsync 가 잠깐 비키게 한다(HoldLiveForCapture).

            _alignCts = new CancellationTokenSource();
            NotifyAligningChanged();
            try
            {
                var ct = _alignCts.Token;

                AutoAlignStatus = slot == 1 ? "마크1 자리로 이동" : "마크2 자리로 이동(-Y)";
                string msg = slot == 1
                    ? await align.MoveToMark1Async(ct)
                    : await align.MoveToMark2Async(ct);

                _mainVM.AddLog($"[ALIGN] {msg}", LogLevel.Info);

                AutoAlignStatus = slot == 1
                    ? $"{msg} · {await MeasureMark1Async(align, ct)}"
                    : $"{msg} · {await CheckAlignedAsync(align, ct)}";
            }
            catch (OperationCanceledException)
            {
                AutoAlignStatus = $"중지 — 마크{slot} 이동";
            }
            catch (Exception ex)
            {
                AutoAlignStatus = $"마크{slot} 이동 실패 — {ex.Message}";
                _mainVM.AddLog($"[ALIGN] {AutoAlignStatus}", LogLevel.Error);
            }
            finally
            {
                _alignCts?.Dispose();
                _alignCts = null;
                NotifyAligningChanged();
            }
        }

        /// <summary>
        /// 마크1 을 재서 기억한다. 실패는 이동을 실패로 만들지 않고 한 줄로만 알린다 —
        /// 패턴을 아직 안 등록했어도 마크 자리로 가 보는 일은 되어야 한다.
        /// </summary>
        private async Task<string> MeasureMark1Async(
            Application.Sequences.IGlassAlignService align, CancellationToken ct)
        {
            try
            {
                string read = await align.MeasureAsync(1, ct);
                _mark1Measured = true;
                return $"{read} · 이제 [마크2]로 정렬을 확인할 수 있습니다";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _mark1Measured = false;
                _mainVM.AddLog($"[ALIGN] 마크1 읽기 실패 — {ex.Message}", LogLevel.Warning);
                return $"마크1 읽기 실패({ex.Message}) — 이동은 끝났습니다";
            }
        }

        /// <summary>
        /// 마크2 자리에서 <b>글라스가 정렬돼 있는지</b> 본다.
        ///
        /// <para>X 를 안 움직이고 -Y 로만 왔으므로, 글라스가 반듯하면 마크2 는 마크1 과 <b>같은
        /// 화면 X</b> 에 와야 한다. 벗어난 만큼이 기울기다 — 판정은 <c>VerifyAngleAsync</c> 가
        /// 레시피 허용 오차로 내린다(자동 정렬 마지막 단계와 같은 잣대).</para>
        ///
        /// <para>마크1 을 안 쟀으면 비교 대상이 없다. 그때 각도를 내면 "마크1 을 못 찾았습니다"
        /// 같은 엉뚱한 이유가 뜨므로, 먼저 무엇을 해야 하는지 그대로 말한다.</para>
        /// </summary>
        private async Task<string> CheckAlignedAsync(
            Application.Sequences.IGlassAlignService align, CancellationToken ct)
        {
            if (!_mark1Measured)
                return "정렬 확인 건너뜀 — [마크1]을 먼저 눌러 기준을 잡으세요";

            try
            {
                var (ok, message) = await align.VerifyAngleAsync(ct);
                _mainVM.AddLog($"[ALIGN] {message}", ok ? LogLevel.Success : LogLevel.Warning);
                return ok ? $"정렬됨 — {message}" : $"틀어짐 — {message}";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[ALIGN] 정렬 확인 실패 — {ex.Message}", LogLevel.Warning);
                return $"정렬 확인 실패({ex.Message}) — 이동은 끝났습니다";
            }
        }

        /// <summary>
        /// 자동 정렬 한 판. 단계는 <c>GlassAlignSequence</c> 가 정하고 여기서는 차례로 돌리기만 한다.
        ///
        /// <para>첫 단계가 준비를 확인하고 못 갖춰졌으면 <b>아무것도 움직이기 전에</b> 세운다 —
        /// 반쯤 움직인 뒤 멈추면 글라스를 다시 놔야 한다.</para>
        /// </summary>
        private async Task RunAutoAlignAsync()
        {
            var machine = _mainVM.GetController()?.GetMachine();
            if (machine == null)
            {
                AutoAlignStatus = "장비가 초기화되지 않았습니다.";
                return;
            }

            // 라이브는 켜 둔다 — 16단계 20초 동안 화면이 굳어 있으면 마크가 시야로 들어오는지,
            // 글라스가 제대로 놓였는지를 볼 수 없다. 재는 순간만 CaptureGrayAsync 가 비키게 한다.

            _alignCts = new CancellationTokenSource();
            NotifyAligningChanged();

            var steps = Application.Sequences.GlassAlignSequence.Build(
                machine, new Services.MotionServiceAdapter(_mainVM));
            string where = "";

            // 대시보드 애니메이션이 이 판을 따라오게 한다 — 여기서 돌리는 정렬도 자동 인쇄와
            // 같은 이동을 하는데, 알려 주지 않으면 메인 화면의 글라스는 파킹 자리에 붙어 있다.
            using var running = Application.Sequences.GlassAlignServices.BeginRun();

            try
            {
                foreach (var step in steps)
                {
                    where = $"{step.Number}/{steps.Count} · {StepText(step.Name)}";
                    AutoAlignStatus = where;
                    await step.Action(_alignCts.Token);
                }

                AutoAlignStatus = "정렬 완료";
                _mainVM.AddLog("[ALIGN] 자동 정렬 완료", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                AutoAlignStatus = $"중지 — {where}";
                _mainVM.AddLog($"[ALIGN] 자동 정렬 중지({where})", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                // 어느 단계에서 멈췄는지가 함께 남아야 한다 — 메시지만 남으면 재현이 안 된다.
                AutoAlignStatus = $"{where} — {ex.Message}";
                _mainVM.AddLog($"[ALIGN] 자동 정렬 실패: {AutoAlignStatus}", LogLevel.Error);
            }
            finally
            {
                _alignCts?.Dispose();
                _alignCts = null;
                NotifyAligningChanged();
            }
        }

        /// <summary>단계 이름은 번역 키다. 못 찾으면 키를 그대로 보여 준다 — 빈 칸보다 낫다.</summary>
        private static string StepText(string key)
            => System.Windows.Application.Current?.TryFindResource(key) as string ?? key;
        // ── 화면 진입 / 이탈 ──────────────────────────────────────────────────
        //
        // 이 화면은 결국 렌즈로 보려고 들어온다 — 들어와서 라이브를 한 번 더 눌러야 하면
        // 그 한 박자가 매번 붙는다. 그래서 진입하면 켜고, 나가면 끈다.
        //
        // ※ 네비게이션으로 나갈 때는 MainViewModel.CurrentView 가 Dispose 를 불러 타이머를 세운다.
        //   Deactivate 는 그 경로를 타지 않는 경우(창 닫힘 등)를 위한 안전망이다 — 화면에서 내려간
        //   뒤에도 라이브가 돌면 보이지도 않는 프레임을 초당 5장씩 찍는다.
        private bool _isViewActive = true;   // 화면 없이 쓰는 경로(시퀀스·테스트)는 항상 활성으로 본다

        /// <summary>화면에 올라왔다 — 라이브를 켠다. 정렬이 도는 중이면 그쪽이 프레임의 주인이라 두고 본다.</summary>
        public void Activate()
        {
            _isViewActive = true;
            if (IsLiveMode || IsAutoAligning) return;
            StartLive();
        }

        /// <summary>화면을 벗어났다 — 라이브를 끈다(정렬은 진행 중이면 그대로 둔다).</summary>
        public void Deactivate()
        {
            _isViewActive = false;
            if (IsLiveMode) StopLive();
        }

        // ── 라이브 시작 / 정지 ────────────────────────────────────────────────
        private void StartLive()
        {
            if (IsLiveMode) return;   // 두 번 켜면 앞의 CTS 가 취소되지 않은 채 버려진다

            _liveCts = new CancellationTokenSource();
            IsLiveMode = true;
            _liveTimer.Start();
            RaiseAllCanExecute();
            _mainVM.AddLog("[VISION] Glass: 라이브 모드 시작", LogLevel.Info);
        }

        private void StopLive()
        {
            _liveTimer.Stop();
            _liveCts?.Cancel();
            _liveCts = null;
            IsLiveMode = false;
            RaiseAllCanExecute();
            _mainVM.AddLog("[VISION] Glass: 라이브 모드 정지", LogLevel.Info);
        }

        // 라이브 틱 재진입 방지 플래그 — IsBusy 와 분리한 이유: IsBusy 는 "⏳ 처리 중" 표시에
        // 바인딩되어 있어, 틱마다 켜고 끄면 초당 5회 깜빡인다(실장 피드백 2026-07-23).
        private bool _liveTicking;
        private readonly Vision.LiveFrameBuffer _liveBuffer = new();

        // ── 재는 동안만 라이브를 비켜 준다 ────────────────────────────────────
        //
        // 예전에는 정렬이 도는 내내 라이브를 껐다. 근거는 "라이브 틱이 정렬이 쓸 프레임을
        // 가로챈다" 였는데, 그건 MVS 취류 전략이 FIFO(OneByOne)이던 시절 이야기다 — 지금은
        // LatestImageOnly 라 누가 먼저 꺼내든 각자 최신 프레임을 받는다.
        //
        // 그래서 이동 중에는 라이브를 켜 둔다. 16단계 20초 동안 화면이 정지 프레임으로 굳어
        // 있으면 마크가 시야로 들어오는지, 글라스가 제대로 놓였는지를 볼 수가 없다.
        //
        // 다만 <b>재는 순간</b>만은 비켜 준다 — 소비자가 하나뿐이어야 "그 사진이 라이브와
        // 겹친 것 아니냐"는 의심을 나중에 배제할 필요가 없다.
        //
        // ※ StopLive/StartLive 를 쓰지 않는 이유: 그쪽은 로그를 남기고 CTS 를 갈아 끼운다.
        //   한 판에 여덟 번 넘게 재므로 "라이브 모드 시작/정지"가 열여섯 줄 쌓여 정렬 로그를 덮는다.
        private int _liveHold;

        /// <summary>재는 동안 라이브 틱을 건너뛰게 한다. <c>using</c> 으로 감싸 쓴다.</summary>
        public IDisposable HoldLiveForCapture() => new LiveHold(this);

        private sealed class LiveHold : IDisposable
        {
            private readonly GlassViewModel _vm;
            private int _released;

            public LiveHold(GlassViewModel vm)
            {
                _vm = vm;
                Interlocked.Increment(ref vm._liveHold);
            }

            // 두 번 Dispose 해도 카운트가 음수로 내려가지 않게 한다 — 음수가 되면
            // 다음 잠금이 걸려도 0 을 넘지 못해 라이브가 안 비켜 준다.
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    Interlocked.Decrement(ref _vm._liveHold);
            }
        }

        private async Task LiveTickAsync()
        {
            // 단발 캡쳐(IsBusy) 중에도, 정렬이 재는 중(_liveHold)에도 건너뛴다.
            if (_liveTicking || IsBusy || Volatile.Read(ref _liveHold) > 0) return;
            _liveTicking = true;
            try
            {
                // saveToDisk:false — 라이브는 연속 캡쳐라 파일로 남기면 디스크가 순식간에 찬다.
                // 픽셀 버퍼를 그대로 화면에 그린다(파일이 없으므로 CurrentImagePath 는 건드리지 않음).
                var image = await _vision.CaptureAsync(CamId, saveToDisk: false);
                if (image.IsValid)
                {
                    // 같은 버퍼에 덮어쓴다 — 프레임마다 새 비트맵을 만들면 대형 객체 힙이 불어나
                    // Gen2 수집으로 화면이 끊긴다. (DispatcherTimer 틱이라 여기는 UI 스레드다)
                    var frame = _liveBuffer.Write(image);
                    if (frame != null) CurrentFrame = frame;
                    CaptureCount++;
                }
            }
            catch (Exception ex)
            {
                // 라이브 중 오류는 화면 로그 노출 없이 파일에만 기록
                LoggerService.WriteToFile("DEBUG", $"[GLASS_LIVE] capture failed: {ex.Message}");
            }
            finally { _liveTicking = false; }
        }

        // ── 단일 캡쳐 ──────────────────────────────────────────────────────────
        private async Task ExecuteCaptureAsync()
        {
            IsBusy = true;
            RaiseAllCanExecute();
            try
            {
                var image = await _vision.CaptureAsync(CamId);
                if (image.IsValid)
                {
                    // 라이브 중이면 화면은 다음 틱에 라이브 프레임으로 되돌아간다 — 파일 저장이 목적.
                    if (!IsLiveMode) CurrentImagePath = image.FilePath;
                    CaptureCount++;
                    _mainVM.AddLog($"[VISION] Glass: 캡쳐 완료 ({image.Width}×{image.Height})" +
                                   (string.IsNullOrEmpty(image.FilePath) ? "" : $" → {image.FilePath}"),
                                   LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] Glass: 캡쳐 실패: {ex.Message}", LogLevel.Error);
            }
            finally { IsBusy = false; RaiseAllCanExecute(); }
        }

        // ── 조명 ON/OFF ───────────────────────────────────────────────────────
        // 글라스뷰 조명은 iCore iPulse LED 컨트롤러(COM12, sID 는 StrobeConfig)가 켠다.
        // Operation(0x300)=1(Continuous) 로 상시 점등 — 외부 트리거(0호기의 NI 카운터 펄스)가 필요 없다.
        //
        // ※ 이전에는 _vision.SetLight() 만 불렀는데, 실장 드라이버(eBUS/Hikrobot)의 SetLight 는
        //   상태 플래그만 바꾸고 하드웨어로 나가지 않는다 — 화면 LED 만 켜지고 조명은 그대로였다.
        //   카메라 상태 표시는 유지하되(화면 LED), 실제 점등은 컨트롤러가 담당한다.
        private bool _isLightOn;
        /// <summary>
        /// 조명이 켜져 있나. 하드웨어에 되물을 수 없어 <b>우리가 보낸 마지막 명령</b>을 기억한다 —
        /// 화면 밖에서 꺼지면 어긋날 수 있지만, 상태 표시가 아예 없는 것보다는 낫다.
        /// </summary>
        public bool IsLightOn
        {
            get => _isLightOn;
            private set => SetProperty(ref _isLightOn, value);
        }

        private void ExecuteLight(bool on)
        {
            IsLightOn = on;
            _vision.SetLight(CamId, on);
            if (on) _vision.SetLightIntensity(CamId, LightIntensity);
            CamStatus = _vision.GetStatus(CamId);

            if (!EnsureStrobe()) return;
            try
            {
                _strobe!.Enable(on);
                _mainVM.AddLog($"[VISION] Glass: 조명 {(on ? "ON" : "OFF")} (sID {(_strobe as ICoreStrobe)?.UnitId})",
                               LogLevel.Info);
            }
            catch (Exception ex)
            {
                _strobeReady = false;
                _mainVM.AddLog($"[VISION] Glass: 조명 {(on ? "ON" : "OFF")} 실패 — {ex.Message}", LogLevel.Error);
            }
        }

        // 조명 컨트롤러 지연 연결. COM 포트가 없거나 다른 프로그램(iPulse Configurator)이 점유 중이어도
        // 화면은 떠야 하므로 실패를 허용하고, 다음 조작에서 다시 시도한다.
        private IStrobeController? _strobe;
        private bool _strobeReady;

        private bool EnsureStrobe()
        {
            if (_strobeReady && _strobe?.IsConnected == true) return true;
            try
            {
                if (_strobe == null)
                {
                    // 가상 비전이면 조명도 가상 — 개발 PC 에서 COM 포트를 찾지 않는다.
                    if (_vision is IJPSystem.Drivers.Vision.VirtualVisionDriver)
                        _strobe = new VirtualStrobe();
                    else
                        _strobe = new ICoreStrobe(
                            new ConfigLoader().LoadStrobeConfig(PathUtils.GetConfigPath("StrobeConfig.json")),
                            CamId);
                }
                _strobe.Init();
                _strobeReady = true;
                return true;
            }
            catch (Exception ex)
            {
                _strobeReady = false;
                _mainVM.AddLog(
                    $"[VISION] Glass: 조명 컨트롤러 연결 실패 — {ex.Message} " +
                    "(iPulse Configurator 가 포트를 점유 중이면 Port Close 후 재시도)", LogLevel.Warning);
                return false;
            }
        }

        // ── 이미지 파일 열기 ──────────────────────────────────────────────────
        private void ExecuteOpenImage()
        {
            string defaultDir = Path.Combine(@"C:\Logs\Vision", CamId);
            if (!Directory.Exists(defaultDir)) defaultDir = @"C:\Logs\Vision";
            if (!Directory.Exists(defaultDir)) defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var dlg = new OpenFileDialog
            {
                Title            = "이미지 파일 선택",
                Filter           = "이미지 파일|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|모든 파일|*.*",
                InitialDirectory = defaultDir,
                Multiselect      = false,
            };

            if (dlg.ShowDialog() == true)
            {
                CurrentImagePath = dlg.FileName;
                _mainVM.AddLog($"[VISION] Glass: 이미지 로드: {Path.GetFileName(dlg.FileName)}", LogLevel.Info);
            }
        }

        private void RaiseAllCanExecute()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ((RelayCommand)StartLiveCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopLiveCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleLiveCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CaptureCommand).RaiseCanExecuteChanged();
                ((RelayCommand)LightOnCommand).RaiseCanExecuteChanged();
                ((RelayCommand)LightOffCommand).RaiseCanExecuteChanged();
                ((RelayCommand)OpenImageCommand).RaiseCanExecuteChanged();
            });
        }

        public void Dispose()
        {
            _statusTimer.Stop();
            _liveTimer.Stop();
            _liveCts?.Cancel();
            _liveCts?.Dispose();
            _liveCts = null;

            // 레시피 구독을 떼야 이 VM 과 라이브 버퍼가 수거된다(BindMinScoreToRecipe 참고).
            if (_recipeForMinScore != null && _minScoreHandler != null)
                _recipeForMinScore.PropertyChanged -= _minScoreHandler;
            _recipeForMinScore = null;
            _minScoreHandler   = null;
        }
    }
}
