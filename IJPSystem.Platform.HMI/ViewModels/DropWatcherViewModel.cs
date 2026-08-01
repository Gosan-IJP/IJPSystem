using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>
    /// Drop Watcher 화면 — LabVIEW 'Sample DW.vi' 레이아웃(좌측 카메라 이미지 + 중앙 파라미터/버튼)으로 재구성.
    /// 현재 단계는 UI 레이아웃 + 파라미터 바인딩이며, 실제 측정 알고리즘은 추후 연결한다(버튼은 placeholder).
    /// </summary>
    public class DropWatcherViewModel : ViewModelBase, IDisposable
    {
        private const string CamId = "CAM_DW";

        private readonly IVisionDriver _vision;
        private readonly MainViewModel _mainVM;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _liveTimer;   // Live View 연속 캡쳐
        private int _liveInvalidCount;                 // 라이브 무효 프레임 연속 횟수
        private const int LiveInvalidStopCount = 15;   // 약 3초(200ms×15) 실패 지속 시 정지+알림
        private bool _liveFirstTickLogged;             // 크래시 지점 특정용 브레드크럼(1회성)
        private bool _liveFirstFrameLogged;
        private bool _liveGrabbing;                     // 캡쳐 중복 방지

        // OpenCV 액적 분석기. 파라미터는 Config/DropWatcherConfig.json 에서 로드(없으면 기본값).
        // MicronsPerPixel 은 실장 교정값 — 부피/직경/속도 절대값이 여기 비례.
        // _procCfg 는 _proc 와 같은 인스턴스를 공유하므로, 캘리브레이션에서 값을 바꾸면 즉시 분석에 반영된다.
        private readonly DropWatcherProcessorConfig _procCfg;
        private readonly DropWatcherProcessor _proc;
        private readonly string _cfgPath;   // 캘리브레이션 저장 대상(JSON)

        // 스트로브 지연 제어 + 2점 측정 시퀀스.
        // Time Interval Measure 는 Delay1/Delay2 두 지연에서 찍은 프레임의 ΔY 로 속도를 낸다
        // → 노즐면(NozzleYPixel) 교정값에 의존하지 않는 측정(단일 프레임 Measure Velocity 와의 차이).
        // 가상 모드에선 가상 카메라에 지연을 흘려보내 낙하 위치를 바꾼다(파이프라인 검증용).
        private readonly IStrobeController _strobe;
        private readonly StrobeConfig? _strobeCfg;   // 실장 설정(로그 표시용) — 가상 모드는 null
        private readonly DropVelocitySequence _twoPoint;
        private bool _strobeReady;

        // 헤드 토출(스핏) — 화면의 "Spit DW" 토글 대상.
        // 실장 Meteor 어댑터가 준비되면 이 생성부만 교체하면 된다.
        // 패턴 생성기를 함께 넘겨, 시작할 때 노즐 번호 유효성이 실제로 검증되게 한다.
        private readonly ISpit _spit = new VirtualSpit(
            new S800SingleSpitPatternBuilder(
                nozzleCount:      AppConstants.HeadNozzleCount,
                firstNozzleIndex: AppConstants.FirstNozzleNumber));

        // 하드웨어 트리거 체인 — 토출 펄스를 분주해 LED/카메라를 동기시킨다.
        // 기동 중에는 측정 촬영이 자유 촬영(CaptureAsync)이 아니라 트리거 동기 프레임을 쓴다.
        private readonly ITriggerChain _trigger;
        private readonly TriggerChainSettings _trigCfg;

        // 진행 중인 측정 취소용. Abort 는 이 토큰을 취소해 촬영/검출 루프를 실제로 끊는다
        // (플래그만 내리면 Task 는 계속 돌아 다음 프레임을 찍는다).
        private CancellationTokenSource? _measureCts;

        // 측정 결과 요약(화면 표시용).
        private string _lastResultText = "측정 전";
        public string LastResultText
        {
            get => _lastResultText;
            private set => SetProperty(ref _lastResultText, value);
        }

        // ── 결과 카드(이미지 아래) 표시용 지표 ────────────────────────────────
        // LastResultText 한 줄로만 두면 값이 길어 잘리고 눈에 안 들어와서, 측정이 성공했을 때만
        // 카드로 펼쳐 보여준다. HasResult=false 면 카드와 오버레이 범례를 모두 숨긴다.
        private bool _hasResult;
        public bool HasResult
        {
            get => _hasResult;
            private set => SetProperty(ref _hasResult, value);
        }

        private string _resultNozzles = "-";
        public string ResultNozzles { get => _resultNozzles; private set => SetProperty(ref _resultNozzles, value); }

        private string _resultDiameter = "-";
        public string ResultDiameter { get => _resultDiameter; private set => SetProperty(ref _resultDiameter, value); }

        private string _resultVolume = "-";
        public string ResultVolume { get => _resultVolume; private set => SetProperty(ref _resultVolume, value); }

        private string _resultVelocity = "-";
        public string ResultVelocity { get => _resultVelocity; private set => SetProperty(ref _resultVelocity, value); }

        private string _resultSpread = "-";
        public string ResultSpread { get => _resultSpread; private set => SetProperty(ref _resultSpread, value); }

        /// <summary>선택된 Vision 드라이버 표기(VIRTUAL/IMAQDX/EBUS). 가상인데 CONNECTED 로만 보이는 혼동 방지.</summary>
        public string DriverModeText =>
            (AppSettingsService.Current?.DriverMode?.Vision ?? "Virtual").Trim().ToUpperInvariant();

        // 드랍와처 검사용 Raw 샘플 이미지 — 이 파일이 있으면 캡쳐 대신 사용한다.
        // 실제 카메라 연동 전, 실측 Raw 이미지로 화면/검사 로직을 확인하기 위함.
        // 파일 위치: Config/Samples/DropWatcher_Raw.png  (Config/Samples/README.md 참고)
        private static readonly string SampleImagePath =
            PathUtils.GetConfigPath(Path.Combine("Samples", "DropWatcher_Raw.png"));

        // ── 카메라 상태 / 이미지 ──────────────────────────────────────────────
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

        // 측정이 분석할 "원본" 이미지 경로 — 화면에 떠 있는 그림의 출처.
        // CurrentImagePath 와 분리한 이유: 측정 후에는 CurrentImagePath 가 오버레이가 그려진 결과 이미지로
        // 바뀌므로, 그대로 두면 재측정 때 오버레이 위에 또 분석하게 된다.
        // 라이브 중에는 null → 측정 시 그 자리에서 1장 캡쳐.
        private string? _measureSourcePath;

        // 디스크에 있는 마지막 이미지 경로(측정 결과/열기/샘플). 라이브 프레임은 파일이 없으므로 갱신하지 않는다.
        // 설정 시 화면 프레임(CurrentFrame)도 함께 파일에서 로드한다.
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

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        // ── Live View ─────────────────────────────────────────────────────────
        private bool _isLiveView;
        public bool IsLiveView
        {
            get => _isLiveView;
            private set
            {
                if (!SetProperty(ref _isLiveView, value)) return;
                OnPropertyChanged(nameof(LiveViewLabel));
                // Live 중에는 연속 캡쳐가 CurrentImagePath 를 계속 덮어써서 연 이미지가 바로 사라진다.
                ((RelayCommand)OpenImageCommand).RaiseCanExecuteChanged();
            }
        }
        public string LiveViewLabel => IsLiveView ? "■ Stop" : "▶ Live";

        // ── Spit DW 상태 ──────────────────────────────────────────────────────
        private bool _isSpitting;
        public bool IsSpitting
        {
            get => _isSpitting;
            private set
            {
                if (!SetProperty(ref _isSpitting, value)) return;
                OnPropertyChanged(nameof(SpitLabel));
            }
        }
        public string SpitLabel => IsSpitting ? "■ Spit DW (ON)" : "Spit DW";

        // ── Drop Watcher Parameter ────────────────────────────────────────────
        private int _frequencyHz = 1000;
        public int FrequencyHz { get => _frequencyHz; set => SetProperty(ref _frequencyHz, value); }

        private double _delayTimeUs = 890.0;
        public double DelayTimeUs { get => _delayTimeUs; set => SetProperty(ref _delayTimeUs, value); }

        private double _durationSec = 1000000;
        public double DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, value); }

        // 적용된 Delay Time (읽기 전용 표시 — Set Delay Time 시 갱신)
        private double _appliedDelayUs = 910.0;
        public double AppliedDelayUs { get => _appliedDelayUs; private set => SetProperty(ref _appliedDelayUs, value); }

        private double _delay1Us = 890.0;
        public double Delay1Us { get => _delay1Us; private set => SetProperty(ref _delay1Us, value); }

        private double _delay2Us = 920.0;
        public double Delay2Us { get => _delay2Us; private set => SetProperty(ref _delay2Us, value); }

        // ── Measure Parameter ─────────────────────────────────────────────────
        private double _measureStartUm = 130.0;
        public double MeasureStartUm { get => _measureStartUm; set => SetProperty(ref _measureStartUm, value); }

        private double _measureEndUm = 910.0;
        public double MeasureEndUm { get => _measureEndUm; set => SetProperty(ref _measureEndUm, value); }

        private double _timeIntervalUs = 5.0;
        public double TimeIntervalUs { get => _timeIntervalUs; set => SetProperty(ref _timeIntervalUs, value); }

        private double _measureAreaXUm = 60.0;
        public double MeasureAreaXUm { get => _measureAreaXUm; set => SetProperty(ref _measureAreaXUm, value); }

        // ── 캘리브레이션 ───────────────────────────────────────────────────────
        // 부피/직경/속도/낙하위치의 절대값이 µm/px 에 비례하므로 실장에서 반드시 교정한다.
        // 교정법: 실제 노즐 피치(µm)를 입력 → 검출 액적들의 평균 픽셀 피치로 µm/px 산출.

        // 헤드 사양상 인접 노즐 간 실제 거리[µm]. (예: 100dpi ≈ 254µm) — 실제 헤드값으로 입력.
        private double _nozzlePitchUm = 254.0;
        public double NozzlePitchUm { get => _nozzlePitchUm; set => SetProperty(ref _nozzlePitchUm, value); }

        // µm/px — _procCfg 와 동기(같은 인스턴스라 분석에 즉시 반영). 교정 버튼으로 자동 산출되거나 직접 입력.
        public double MicronsPerPixel
        {
            get => _procCfg.MicronsPerPixel;
            set
            {
                if (Math.Abs(_procCfg.MicronsPerPixel - value) < 1e-9) return;
                _procCfg.MicronsPerPixel = value;
                OnPropertyChanged();
            }
        }

        // 노즐면(토출 시작) Y[px] — 낙하거리/속도의 기준. 화면 상단이 노즐면이 아니면 조정.
        public double NozzleYPixel
        {
            get => _procCfg.NozzleYPixel;
            set
            {
                if (Math.Abs(_procCfg.NozzleYPixel - value) < 1e-9) return;
                _procCfg.NozzleYPixel = value;
                OnPropertyChanged();
            }
        }

        // ── 측정 그래프 (노즐별 Velocity / Drop Position / Volume) ─────────────
        // 측정 전에는 비어 있고, 측정하면 BuildDropletCharts 가 오버레이와 같은 데이터로 채운다.
        public ISeries[] VelocitySeries { get; private set; } = Array.Empty<ISeries>();
        public ISeries[] PositionSeries { get; private set; } = Array.Empty<ISeries>();
        public ISeries[] SpitRateSeries { get; private set; } = Array.Empty<ISeries>();

        public Axis[] VelocityXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] VelocityYAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] PositionXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] PositionYAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] SpitRateXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] SpitRateYAxes { get; private set; } = Array.Empty<Axis>();

        // 한 프레임에 노즐들이 가로로 늘어선 구조 → X축은 항상 노즐 번호(시간축 아님).
        private const string XAxisName = "Nozzle #";

        // 차트 헤더 제목 — LiveCharts 축 이름은 화면에서 눈에 잘 안 띄어(실장 피드백 2026-07-23)
        // 각 차트 위에 명시적인 제목을 표시한다. 가운데 차트는 측정 방식에 따라 의미가 바뀐다.
        public string VelocityChartTitle => "토출 속도 Velocity (m/s) — X: 노즐 번호";
        public string VolumeChartTitle   => "액적 부피 Volume (pL) — X: 노즐 번호";

        private string _positionChartTitle = "낙하 위치 Drop Position (µm) — X: 노즐 번호";
        public string PositionChartTitle
        {
            get => _positionChartTitle;
            private set => SetProperty(ref _positionChartTitle, value);
        }

        private static readonly SKColor AxisText = new SKColor(0x94, 0xA3, 0xB8);
        private static readonly SKColor AxisGrid = new SKColor(0x33, 0x41, 0x55);

        // ※ 2026-07-23 실장 크래시 이력 — 차트 텍스트는 절대 건드리지 말 것:
        //   축에 SKTypeface(맑은 고딕)를 명시하자 제어 PC 첫 렌더에서 네이티브 즉사.
        //   그 PC 는 Skia 글꼴 스택이 깨져 있어(기본 글꼴 글자도 안 그려짐) 코드로는 해결 불가.
        //   → 오늘 이전의 검증된 축 구성(크기 10, 배율/글꼴 미지정) 그대로 유지한다.
        //   축 글자 표시는 OS 수리(Font Cache 서비스/재부팅) 후 재검토. 차트 의미는 WPF 헤더가 전달한다.
        private static SolidColorPaint TextPaint() => new(AxisText);

        // ── 커맨드 ────────────────────────────────────────────────────────────
        public ICommand SetDelay1Command           { get; }
        public ICommand SetDelay2Command           { get; }
        public ICommand AbortCommand               { get; }
        public ICommand OpenImageCommand           { get; }
        public ICommand MeasureVelocityCommand     { get; }
        public ICommand TimeIntervalMeasureCommand { get; }
        public ICommand ToggleLiveViewCommand      { get; }
        public ICommand CalibrateScaleCommand      { get; }
        public ICommand SaveCalibrationCommand     { get; }
        public ICommand ToggleSpitCommand          { get; }
        public ICommand CaptureFocusReferenceCommand { get; }
        public ICommand ToggleStrobeCommand        { get; }

        public DropWatcherViewModel(MainViewModel mainVM)
        {
            // 실장 크래시(2026-07-23, 화면 진입 즉사) 지점 특정용 브레드크럼 — 원인 확정 후 정리 예정.
            LoggerService.WriteToFile("INFO", "[DW] VM init 시작");
            _mainVM = mainVM;
            _vision = mainVM.GetController().GetMachine().Vision;
            _cfgPath = PathUtils.GetConfigPath("DropWatcherConfig.json");
            _procCfg = new ConfigLoader().LoadDropWatcherConfig(_cfgPath);
            _proc   = new DropWatcherProcessor(_procCfg);

            // 실장은 iCore Modbus, 가상은 지연을 가상 카메라로 흘려보내는 대역.
            if (_vision is IJPSystem.Drivers.Vision.VirtualVisionDriver vvd)
            {
                _strobe = new VirtualStrobe(us => vvd.VirtualStrobeDelayUs = us);
            }
            else
            {
                _strobeCfg = new ConfigLoader().LoadStrobeConfig(PathUtils.GetConfigPath("StrobeConfig.json"));
                _strobe    = new ICoreStrobe(_strobeCfg);
            }

            // 트리거 체인 — 실장은 NI-DAQmx 어댑터, 가상은 가상 카메라의 트리거 시뮬레이션을 구동.
            _trigCfg = new ConfigLoader().LoadTriggerChainConfig(PathUtils.GetConfigPath("TriggerChainConfig.json"));
            _trigger = _vision is IJPSystem.Drivers.Vision.VirtualVisionDriver v
                ? new VirtualTriggerChain(_trigCfg, () => v.SimulateHardwareTrigger(CamId))
                : new NiDaqTriggerChain(_trigCfg,
                        msg => _mainVM.AddLog($"[VISION] DropWatcher: {msg}", LogLevel.Warning));

            LoggerService.WriteToFile("INFO", "[DW] 디바이스 어댑터(스트로브/트리거) 준비 완료");
            _twoPoint = new DropVelocitySequence(_strobe, GrabAsync, _proc, _procCfg);

            SetDelay1Command           = new RelayCommand(_ => ExecuteSetDelay(1));
            SetDelay2Command           = new RelayCommand(_ => ExecuteSetDelay(2));
            AbortCommand               = new RelayCommand(async _ => await ExecuteAbortAsync());
            OpenImageCommand           = new RelayCommand(_ => ExecuteOpenImage(), _ => !IsLiveView);
            MeasureVelocityCommand     = new RelayCommand(async _ => await ExecuteMeasureAsync("Measure Velocity"),      _ => !IsBusy);
            TimeIntervalMeasureCommand = new RelayCommand(async _ => await ExecuteTwoPointMeasureAsync(), _ => !IsBusy);
            ToggleLiveViewCommand      = new RelayCommand(_ => ToggleLiveView());
            CalibrateScaleCommand      = new RelayCommand(async _ => await ExecuteCalibrateScaleAsync(), _ => !IsBusy);
            SaveCalibrationCommand     = new RelayCommand(_ => ExecuteSaveCalibration());
            ToggleSpitCommand          = new RelayCommand(async _ => await ExecuteToggleSpitAsync());
            CaptureFocusReferenceCommand = new RelayCommand(_ => ExecuteCaptureFocusReference(), _ => !IsBusy);
            ToggleStrobeCommand        = new RelayCommand(_ => ExecuteToggleStrobe());

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += (_, _) => CamStatus = _vision.GetStatus(CamId);
            _pollTimer.Start();

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };  // 약 5 fps
            _liveTimer.Tick += async (_, _) => await LiveGrabAsync();

            CamStatus = _vision.GetStatus(CamId);

            bool isVirtual = _vision is IJPSystem.Drivers.Vision.VirtualVisionDriver;
            if (isVirtual && File.Exists(SampleImagePath))
            {
                // 가상 모드: 샘플 Raw 를 바로 표시 — 카메라 없이 측정 연습용(보이는 것 = 측정 대상)
                _measureSourcePath = SampleImagePath;
                CurrentImagePath   = SampleImagePath;
            }
            else if (!isVirtual && CamStatus.IsConnected)
            {
                // 실장: 진입 즉시 Live 자동 시작 — 샘플 정지 이미지가 실영상처럼 보여
                // "카메라가 안 산다"는 혼동을 준 실장 피드백(2026-07-23) 반영. 이탈 시 Dispose 가 정지.
                ToggleLiveView();
            }
            // 실장 + 카메라 미연결: 아무것도 띄우지 않는다(연결 배지가 상태를 말해준다).

            LoggerService.WriteToFile("INFO", "[DW] 차트 구성 시작");
            BuildCharts();
            LoggerService.WriteToFile("INFO", "[DW] VM init 완료");
        }

        // Set Delay Time 버튼 — 현재 Delay Time 을 Delay 1/2 로 저장하고 스트로브에 실제로 적용한다.
        // (스트로브 미연결이면 값만 저장 — 2점 측정 시 다시 시도한다)
        private void ExecuteSetDelay(int which)
        {
            if (which == 1) Delay1Us = DelayTimeUs;
            else            Delay2Us = DelayTimeUs;
            AppliedDelayUs = DelayTimeUs;

            if (!EnsureStrobe())
            {
                _mainVM.AddLog($"[VISION] DropWatcher: Delay {which} = {DelayTimeUs:F1}us (값만 저장 — 스트로브 미연결)",
                               LogLevel.Warning);
                return;
            }

            try
            {
                _strobe.SetDelayMicroseconds(DelayTimeUs);
                _mainVM.AddLog($"[VISION] DropWatcher: Delay {which} = {DelayTimeUs:F1}us 적용", LogLevel.Info);

                // 커미셔닝 검증 — LabVIEW 원본처럼 쓰기 직후 리드백으로 통신/주소를 확인한다.
                var raw = _strobe.TryReadDelayRaw();
                if (raw != null)
                    _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 리드백 raw={raw} " +
                                   $"({(raw == (uint)Math.Round(DelayTimeUs) ? "일치" : "쓴 값과 다름 — 주소/스케일 확인")})",
                                   LogLevel.Info);
                else
                    _mainVM.AddLog("[VISION] DropWatcher: 스트로브 리드백 실패/미지원 — 쓰기 자체는 성공",
                                   LogLevel.Warning);
            }
            catch (Exception ex)
            {
                _strobeReady = false;   // 통신 끊김 → 다음 사용 시 재연결 시도
                // 예외 타입이 진단 키다: TimeoutException=보레이트/UnitId, SlaveException=레지스터 주소.
                _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 지연 적용 실패({ex.GetType().Name}): {ex.Message}",
                               LogLevel.Error);
            }
        }

        // ── 스트로브 조명 수동 온/오프 (커미셔닝용 — 발광 여부를 라이브 배경 밝기로 확인) ──
        private bool _isStrobeOn;
        public bool IsStrobeOn
        {
            get => _isStrobeOn;
            private set
            {
                if (SetProperty(ref _isStrobeOn, value)) OnPropertyChanged(nameof(StrobeLabel));
            }
        }
        // 아이콘은 XAML 의 벡터 Path 가 그린다(이모지는 제어PC 글꼴에 따라 렌더가 달라짐).
        public string StrobeLabel => IsStrobeOn ? "ON" : "OFF";

        private void ExecuteToggleStrobe()
        {
            if (!EnsureStrobe()) return;   // 연결 실패 로그는 EnsureStrobe 가 남긴다
            try
            {
                bool next = !IsStrobeOn;
                _strobe.Enable(next);
                IsStrobeOn = next;
                // EnableRegister 미설정(-1) 장비면 Enable 은 no-op — 커미셔닝 중임을 로그로 상기.
                _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 발광 {(next ? "ON" : "OFF")} 명령 " +
                               "(StrobeConfig.EnableRegister 미설정이면 장비는 무시)", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _strobeReady = false;
                _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 온/오프 실패({ex.GetType().Name}): {ex.Message}",
                               LogLevel.Error);
            }
        }

        // 스트로브 지연 컨트롤러 연결 보장. 실장 COM 포트가 없어도 화면은 떠야 하므로 지연 초기화 + 실패 허용.
        private bool EnsureStrobe()
        {
            if (_strobeReady) return true;
            try
            {
                _strobe.Init();
                _strobe.Enable(true);
                _strobeReady = _strobe.IsConnected;
                if (_strobeReady)
                {
                    IsStrobeOn = true;   // 연결 시 Enable(true)로 켠 상태와 동기화
                    string port = _strobeCfg != null
                        ? $" ({_strobeCfg.ComPort}, {_strobeCfg.BaudRate}bps, Unit {_strobeCfg.UnitId}, " +
                          $"Reg 0x{_strobeCfg.DelayRegister:X4})"
                        : "";
                    _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 연결됨(포트 열림){port} — 장비 응답은 Delay 적용/리드백으로 확인",
                                   LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _strobeReady = false;
                // 포트 열기 실패(포트 없음/점유)와 Modbus 무응답(보레이트/UnitId)을 구분할 수 있게 타입도 남긴다.
                _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 연결 실패({ex.GetType().Name}): {ex.Message}",
                               LogLevel.Warning);
            }
            return _strobeReady;
        }

        // ── Spit DW — 토출 개시/중단 토글 ──────────────────────────────────────
        // 화면의 Nozzle Select 결과(NozzleControlGlobal)와 Frequency 입력으로 토출을 시작한다.
        // (LabVIEW 5_WIZ_Set Nozzle and WF with DW.vi 의 "Spit DW" 불리언 컨트롤)
        private async Task ExecuteToggleSpitAsync()
        {
            if (IsSpitting) { await StopSpitAsync(); return; }

            var nozzles = Nozzle.NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles;
            if (nozzles.Count == 0)
            {
                _mainVM.AddLog("[VISION] DropWatcher: Spit — 선택된 노즐이 없습니다. Nozzle Select 로 먼저 지정하세요.",
                               LogLevel.Warning);
                return;
            }
            if (FrequencyHz <= 0)
            {
                _mainVM.AddLog("[VISION] DropWatcher: Spit — Frequency 는 0보다 커야 합니다.", LogLevel.Warning);
                return;
            }

            var settings = new SpitSettings { Nozzles = nozzles, FrequencyHz = FrequencyHz };
            try
            {
                _spit.Start(settings);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: Spit 시작 실패: {ex.Message}", LogLevel.Error);
                return;
            }

            // 헤드 범위를 벗어난 노즐은 조용히 버려지면 안 된다 — 번호 기준(0/1 시작) 문제일 수 있다.
            if (_spit is VirtualSpit vs && vs.IgnoredNozzles.Count > 0)
                _mainVM.AddLog($"[VISION] DropWatcher: Spit — 헤드 노즐 범위" +
                               $"({AppConstants.FirstNozzleNumber}~{AppConstants.HeadNozzleCount}) 밖이라 무시된 번호: " +
                               string.Join(",", vs.IgnoredNozzles), LogLevel.Warning);

            // 토출이 돌기 시작해야 분주기가 셀 펄스가 생긴다 → 스핏 다음에 트리거 체인 기동.
            try
            {
                ApplyCameraForStrobe();          // 스트로브 촬영용 노출/조명 설정
                _trigger.Start(FrequencyHz);
                OnPropertyChanged(nameof(IsTriggerSynced));
                _mainVM.AddLog($"[VISION] DropWatcher: 트리거 체인 기동 — 분주 1/{_trigCfg.DivideRatio}, " +
                               $"실촬영 {_trigger.EffectiveFrameRateHz:F1}fps", LogLevel.Info);

                // 프레임 누락 위험은 조용히 넘기면 "그냥 느린 촬영"으로 보여 원인 추적이 어렵다.
                if (!string.IsNullOrEmpty(_trigger.MarginWarning))
                    _mainVM.AddLog($"[VISION] DropWatcher: 트리거 마진 경고 — {_trigger.MarginWarning}", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                // 트리거 없이도 토출 자체는 유효하다(육안 확인 등). 다만 측정값은 신뢰할 수 없다.
                _mainVM.AddLog($"[VISION] DropWatcher: 트리거 체인 기동 실패: {ex.Message} " +
                               "— 촬영이 토출과 동기되지 않아 속도 측정값을 신뢰할 수 없습니다.", LogLevel.Error);
            }

            IsSpitting = true;
            _mainVM.AddLog($"[VISION] DropWatcher: Spit 시작 — 노즐 {nozzles.Count}개 @ {FrequencyHz}Hz", LogLevel.Info);

            // Duration 이 설정돼 있으면 그 시간 뒤 자동 정지(잉크 낭비/건조 방지).
            if (DurationSec > 0 && DurationSec < 3600)
                _ = AutoStopSpitAsync(TimeSpan.FromSeconds(DurationSec));
        }

        // 토출 중단 — Spit DW = OFF. 내부적으로 중단 명령 후 실제 idle 을 확인한다.
        private async Task<bool> StopSpitAsync()
        {
            // 트리거 공급부터 차단(역순 정지) — 토출이 멎는 동안 헛 트리거가 나가지 않게.
            try { _trigger.Stop(); OnPropertyChanged(nameof(IsTriggerSynced)); }
            catch (Exception ex) { _mainVM.AddLog($"[VISION] DropWatcher: 트리거 체인 정지 실패: {ex.Message}", LogLevel.Warning); }

            bool idle;
            try
            {
                idle = await _spit.StopAsync();
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: Spit 중단 실패: {ex.Message}", LogLevel.Error);
                idle = false;
            }

            IsSpitting = _spit.IsSpitting;
            if (idle)
                _mainVM.AddLog("[VISION] DropWatcher: Spit 정지", LogLevel.Info);
            else
                // 정지 미확인을 성공처럼 넘기면 헤드가 계속 토출 중일 수 있다.
                _mainVM.AddLog("[VISION] DropWatcher: Spit 중단 후에도 정지가 확인되지 않았습니다. 헤드 상태를 확인하세요.",
                               LogLevel.Error);
            return idle;
        }

        private async Task AutoStopSpitAsync(TimeSpan after)
        {
            try
            {
                await Task.Delay(after);
                if (IsSpitting)
                {
                    _mainVM.AddLog($"[VISION] DropWatcher: Duration({DurationSec:F0}s) 경과 — Spit 자동 정지", LogLevel.Info);
                    await StopSpitAsync();
                }
            }
            catch { /* 화면 종료 등 — 무시 */ }
        }

        // Abort — 토출/측정 중단.
        // (LabVIEW METEOR PCC Func 의 Abort → BUSY 재시도 → isBusy 폴링 절차)
        //
        // 순서가 중요하다: 촬영 루프를 먼저 끊어야 중단 도중에 새 프레임을 찍지 않는다.
        //   ① 진행 중인 측정 취소  ② 라이브 뷰 정지  ③ 토출 중단 + 실제 정지 확인  ④ 스트로브 소등
        //
        // ※ 소프트웨어 중단이다. 모션·펌프·밸브는 멈추지 않으며 비상정지를 대체하지 않는다.
        private async Task ExecuteAbortAsync()
        {
            _mainVM.AddLog("[VISION] DropWatcher: Abort 요청", LogLevel.Warning);

            _measureCts?.Cancel();                 // ① 측정 루프 중단(다음 await 지점에서 빠져나온다)
            if (IsLiveView) ToggleLiveView();      // ② 연속 캡쳐 정지

            bool idle = await StopSpitAsync();               // ③ 토출 중단 + idle 확인

            // ④ 발광 소등 — 중단 후 스트로브가 계속 켜져 있을 이유가 없다. 미연결이면 건너뛴다.
            if (_strobeReady)
            {
                try { _strobe.Enable(false); IsStrobeOn = false; }
                catch (Exception ex)
                {
                    _strobeReady = false;
                    _mainVM.AddLog($"[VISION] DropWatcher: 스트로브 소등 실패: {ex.Message}", LogLevel.Warning);
                }
            }

            IsBusy = false;
            RaiseMeasureCanExecute();

            // 개별 사유는 StopSpitAsync 가 이미 로그로 남겼다. 여기선 화면 요약만.
            LastResultText = idle ? "중단됨" : "중단 실패 — 토출 정지 미확인";
            ClearResultCard();
        }

        // Measure Velocity / Time Interval Measure — OpenCV(DropWatcherProcessor)로 액적 분석.
        //
        // 실측 DW Raw 는 "한 프레임에 노즐들이 가로로 늘어선" 구조다(스트로브가 액적을 얼림).
        // 따라서 컬럼 = 노즐이고, 속도 = 낙하거리/스트로브 지연 → 단일 프레임 분석이 맞다.
        // (위상 스윕으로 여러 장을 훑는 방식은 이 장비 구조와 맞지 않아 사용하지 않는다)
        //
        // 대상: 지금 화면에 떠 있는 이미지(_measureSourcePath). 라이브 중이면 그 자리에서 1장 캡쳐.
        // → 보이는 것과 측정 대상이 항상 같다. 샘플을 분석하고 싶으면 샘플 파일을 열면 된다.
        private async Task ExecuteMeasureAsync(string action)
        {
            IsBusy = true;
            RaiseMeasureCanExecute();
            var cts = NewMeasureCts();
            try
            {
                VisionImage frame;
                string src;
                if (!string.IsNullOrEmpty(_measureSourcePath) && File.Exists(_measureSourcePath))
                {
                    frame = new VisionImage { CameraId = CamId, FilePath = _measureSourcePath, IsValid = true };
                    src   = string.Equals(_measureSourcePath, SampleImagePath, StringComparison.OrdinalIgnoreCase)
                            ? "Raw 샘플" : Path.GetFileName(_measureSourcePath);
                }
                else
                {
                    // 라이브 화면(파일 없음) → 측정용 1장은 기록으로 남긴다.
                    frame = await _vision.CaptureAsync(CamId, saveToDisk: true);
                    src   = "라이브 캡쳐";
                }

                var drops = await Task.Run(() => _proc.DetectDroplets(frame), cts.Token);
                double delayUs = AppliedDelayUs > 0 ? AppliedDelayUs : DelayTimeUs;

                // 오버레이(컬럼 분할선/측정창/중심마커/속도) 생성 → 좌측 이미지로 표시.
                string dir = Path.Combine(Path.GetTempPath(), "IJP_DropWatcher");
                Directory.CreateDirectory(dir);
                string annPath = Path.Combine(dir, $"annotated_{DateTime.Now:HHmmss_fff}.png");
                string? saved = await Task.Run(() => _proc.SaveAnnotatedFrame(annPath, frame, drops, delayUs), cts.Token);
                if (!string.IsNullOrEmpty(saved)) CurrentImagePath = saved;
                else if (!string.IsNullOrEmpty(frame.FilePath)) CurrentImagePath = frame.FilePath;

                ReportDroplets(action, drops, delayUs, src);
            }
            catch (OperationCanceledException)
            {
                // Abort — 사유는 Abort 쪽에서 이미 로그로 남긴다.
                _mainVM.AddLog($"[VISION] DropWatcher: {action} 중단됨", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: {action} 실패: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
                RaiseMeasureCanExecute();
            }
        }

        // ── 스트로브 촬영용 카메라 설정 ───────────────────────────────────────
        // 드랍와쳐는 "암실 + 짧은 스트로브 발광" 조건이라, 일반 촬영과 노출 전략이 정반대다.
        //   · 노출은 스트로브 발광폭보다 충분히 길게 — 짧으면 발광 순간을 놓쳐 액적이 안 잡힌다.
        //     (노출이 길어도 어두운 배경이라 흐르지 않는다. 방울을 얼리는 건 셔터가 아니라 발광폭이다)
        //   · 상시 조명은 끈다 — 켜져 있으면 배경이 밝아져 실루엣 대비가 죽는다.
        private void ApplyCameraForStrobe()
        {
            try
            {
                double exposureMs = Math.Max(MinExposureMs, _trigCfg.CamWidthUs / 1000.0 * ExposureMarginRatio);
                _vision.SetExposure(CamId, exposureMs);
                _vision.SetLight(CamId, false);
                _mainVM.AddLog($"[VISION] DropWatcher: 카메라 설정 — 노출 {exposureMs:F2}ms, 상시조명 OFF", LogLevel.Info);
            }
            catch (Exception ex)
            {
                // 설정 실패해도 촬영 자체는 가능하다 — 다만 이미지 품질을 신뢰할 수 없다.
                _mainVM.AddLog($"[VISION] DropWatcher: 카메라 설정 실패: {ex.Message} — 노출/조명이 " +
                               "스트로브 조건에 맞지 않을 수 있습니다.", LogLevel.Warning);
            }
        }

        /// <summary>카메라 트리거 폭 대비 노출 여유 배수 — 트리거 지터를 흡수한다.</summary>
        private const double ExposureMarginRatio = 3.0;
        /// <summary>노출 하한[ms]. 너무 짧으면 카메라가 설정을 무시하거나 프레임을 못 만든다.</summary>
        private const double MinExposureMs = 0.05;

        // ── 측정용 프레임 취득 ────────────────────────────────────────────────
        // 트리거 체인이 돌고 있으면 하드웨어 트리거 동기 프레임을 받는다 — 스트로브가 얼린
        // 그 순간의 액적이다. 체인이 없으면 자유 촬영으로 떨어지는데, 이때 찍히는 것은
        // 임의 위상의 화면이므로 속도값을 신뢰하면 안 된다(로그로 구분해 남긴다).
        private Task<VisionImage> GrabAsync(CancellationToken ct)
            => _trigger.IsRunning
                ? _vision.WaitForHardwareTriggerAsync(CamId, ct)
                : _vision.CaptureAsync(CamId, saveToDisk: false);

        /// <summary>측정 결과에 트리거 동기 여부를 표시 — 비동기 촬영 결과는 참고값이다.</summary>
        public bool IsTriggerSynced => _trigger.IsRunning;

        // 새 측정용 취소 토큰. 이전 것은 정리하고 교체한다(Abort 가 이 토큰을 취소한다).
        private CancellationTokenSource NewMeasureCts()
        {
            _measureCts?.Dispose();
            _measureCts = new CancellationTokenSource();
            return _measureCts;
        }

        // 다중노즐 단일 프레임 결과 보고 + 그래프(노즐별 속도/낙하위치/부피) 갱신.
        private void ReportDroplets(string action, IReadOnlyList<DropletInfo> drops, double delayUs, string src)
        {
            if (drops == null || drops.Count == 0)
            {
                LastResultText = "액적 미검출";
                ClearResultCard();
                _mainVM.AddLog($"[VISION] DropWatcher: {action}({src}) — 액적 미검출", LogLevel.Warning);
                return;
            }

            double[] vel = _proc.ComputeDropletVelocities(drops, delayUs);
            var okVel = vel.Where(v => !double.IsNaN(v)).ToArray();
            double avgDia = drops.Average(d => d.DiameterMicron);
            double avgVol = drops.Average(d => d.VolumePicoLiter);

            LastResultText = okVel.Length > 0
                ? $"노즐 {drops.Count}개 · 직경 {avgDia:F1}µm · 부피 {avgVol:F1}pL · 속도 {okVel.Average():F2}m/s (편차 {okVel.Max() - okVel.Min():F2})"
                : $"노즐 {drops.Count}개 · 직경 {avgDia:F1}µm · 부피 {avgVol:F1}pL";

            ResultNozzles  = drops.Count.ToString();
            ResultDiameter = $"{avgDia:F1}";
            ResultVolume   = $"{avgVol:F1}";
            ResultVelocity = okVel.Length > 0 ? $"{okVel.Average():F2}" : "-";
            ResultSpread   = okVel.Length > 0 ? $"{okVel.Max() - okVel.Min():F2}" : "-";
            HasResult      = true;
            _mainVM.AddLog($"[VISION] DropWatcher: {action}({src}) — {LastResultText}", LogLevel.Info);

            BuildDropletCharts(drops, vel);
        }

        /// <summary>결과 카드를 비운다 — 실패/중단 후 직전 성공값이 남아 오해를 주지 않게.</summary>
        private void ClearResultCard()
        {
            HasResult      = false;
            ResultNozzles  = "-";
            ResultDiameter = "-";
            ResultVolume   = "-";
            ResultVelocity = "-";
            ResultSpread   = "-";
        }

        // 다중노즐 프레임 그래프 — 오버레이와 동일한 데이터/컬럼 순서(노즐 번호 축).
        private void BuildDropletCharts(IReadOnlyList<DropletInfo> drops, double[] vel)
        {
            int n = drops.Count;
            double um = _procCfg.MicronsPerPixel;
            string[] labels = Enumerable.Range(0, n).Select(i => i.ToString()).ToArray();

            var posUm = new double[n];
            var volPl = new double[n];
            for (int i = 0; i < n; i++)
            {
                posUm[i] = (drops[i].CentroidYPixel - _procCfg.NozzleYPixel) * um;   // 낙하거리
                volPl[i] = drops[i].VolumePicoLiter;
            }

            ApplyCharts(labels, vel, posUm, volPl, "Drop Position (um)",
                        "낙하 위치 Drop Position (µm) — X: 노즐 번호");
        }

        // 노즐별 3개 차트(속도/위치/부피)를 한 번에 갱신. 단일프레임·2점 측정이 공유한다.
        private void ApplyCharts(string[] labels, double[] vel, double[] posUm, double[] volPl,
                                 string posAxisTitle, string posChartTitle)
        {
            PositionChartTitle = posChartTitle;
            VelocitySeries = new ISeries[] { MakeLine("Drop", SKColors.LimeGreen, vel) };
            PositionSeries = new ISeries[] { MakeLine("Drop", SKColors.DodgerBlue, posUm) };
            SpitRateSeries = new ISeries[] { MakeLine("Drop", SKColors.Orange, volPl) };

            VelocityXAxes = MakeXAxes(labels, XAxisName);
            PositionXAxes = MakeXAxes(labels, XAxisName);
            SpitRateXAxes = MakeXAxes(labels, XAxisName);
            VelocityYAxes = MakeYAxes("Velocity (m/s)");
            PositionYAxes = MakeYAxes(posAxisTitle);
            SpitRateYAxes = MakeYAxes("Volume (pL)");

            OnPropertyChanged(nameof(VelocitySeries));
            OnPropertyChanged(nameof(PositionSeries));
            OnPropertyChanged(nameof(SpitRateSeries));
            OnPropertyChanged(nameof(VelocityXAxes));
            OnPropertyChanged(nameof(PositionXAxes));
            OnPropertyChanged(nameof(SpitRateXAxes));
            OnPropertyChanged(nameof(VelocityYAxes));
            OnPropertyChanged(nameof(PositionYAxes));
            OnPropertyChanged(nameof(SpitRateYAxes));
        }

        // ── Time Interval Measure — 2점 지연 측정 ──────────────────────────────
        // Delay 1 / Delay 2 두 지연에서 각각 촬영해, 같은 노즐 액적의 ΔY 로 속도를 낸다.
        // (LabVIEW 5_WIZ_Set Nozzle and WF with DW.vi 의 Time1 → Time2 → Measure 시퀀스)
        //
        // Measure Velocity(단일 프레임)와의 차이:
        //   단일 프레임 — 낙하거리 = 중심Y − 노즐면Y  → 노즐면 Y 교정값이 틀리면 속도가 통째로 틀어짐.
        //   2점 측정   — 낙하거리 = 두 시점의 ΔY      → 절대 기준이 없어도 옳다. 대신 스트로브 필요.
        private async Task ExecuteTwoPointMeasureAsync()
        {
            const string action = "Time Interval Measure";

            if (Math.Abs(Delay2Us - Delay1Us) < 1e-6)
            {
                LastResultText = "Delay 1 과 Delay 2 가 같습니다";
                ClearResultCard();
                _mainVM.AddLog($"[VISION] DropWatcher: {action} — Delay 1/2 를 서로 다르게 설정하세요.", LogLevel.Warning);
                return;
            }

            if (!EnsureStrobe())
            {
                LastResultText = "스트로브 미연결 — 2점 측정 불가";
                ClearResultCard();
                _mainVM.AddLog($"[VISION] DropWatcher: {action} — 스트로브 컨트롤러에 연결할 수 없어 측정을 건너뜁니다. " +
                               "단일 프레임 측정은 Measure Velocity 를 쓰세요.", LogLevel.Warning);
                return;
            }

            bool wasLive = IsLiveView;
            if (wasLive) ToggleLiveView();   // 라이브 캡쳐가 지연 적용 사이에 끼어들지 않도록 정지

            IsBusy = true;
            RaiseMeasureCanExecute();
            var cts = NewMeasureCts();
            try
            {
                // 노즐 번호 매핑에 필요한 정보를 매 측정마다 최신값으로 넘긴다
                // (Nozzle Select 나 피치 교정이 측정 사이에 바뀔 수 있다).
                _twoPoint.ExpectedNozzles = Nozzle.NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles;
                _twoPoint.NozzlePitchUm   = NozzlePitchUm;

                var r = await _twoPoint.MeasureVelocityAsync(Delay1Us, Delay2Us, cts.Token);
                if (!r.Success)
                {
                    LastResultText = r.Message;
                    ClearResultCard();
                    _mainVM.AddLog($"[VISION] DropWatcher: {action} 실패 — {r.Message}", LogLevel.Warning);
                    return;
                }

                // Time2 프레임에 오버레이를 그려 화면에 표시(측정 대상과 보이는 것을 일치시킨다).
                if (r.Frame2 != null)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "IJP_DropWatcher");
                    Directory.CreateDirectory(dir);
                    string annPath = Path.Combine(dir, $"twopoint_{DateTime.Now:HHmmss_fff}.png");
                    string? saved = await Task.Run(() =>
                        _proc.SaveAnnotatedFrame(annPath, r.Frame2, r.DropsAt2, Delay2Us), cts.Token);
                    if (!string.IsNullOrEmpty(saved))
                    {
                        CurrentImagePath   = saved;
                        _measureSourcePath = null;   // 오버레이 결과 → 재측정 대상 아님
                    }
                }

                ReportTwoPoint(action, r);
            }
            catch (OperationCanceledException)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: {action} 중단됨", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: {action} 실패: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
                RaiseMeasureCanExecute();
            }
        }

        private void ReportTwoPoint(string action, DropVelocityResult r)
        {
            // 트리거 비동기 상태의 측정은 위상이 보장되지 않는다 — 결과에 명시해 오독을 막는다.
            string sync = IsTriggerSynced ? "" : " ※트리거 비동기(참고값)";
            // 품질 저하·불토출 등은 결과를 신뢰하기 어렵게 만드는 요소라 요약에 함께 띄운다.
            string warn = r.Warnings.Count > 0 ? $" ※{string.Join(" / ", r.Warnings)}" : "";

            LastResultText =
                $"노즐 {r.Nozzles.Count}개 · 속도 {r.VelocityMps:F2}m/s (편차 {r.VelocitySpreadMps:F2}) · " +
                $"직경 {r.DiameterUm:F1}µm · 부피 {r.VolumePl:F1}pL · Δt {Math.Abs(r.Time2Us - r.Time1Us):F1}µs{sync}{warn}";

            // 목표 4~6 m/s 를 벗어나면 파형/압력 조정이 필요하다는 신호라 경고로 올린다.
            bool clean = r.InTargetRange() && r.Warnings.Count == 0;
            _mainVM.AddLog($"[VISION] DropWatcher: {action}(2점 {r.Time1Us:F1}→{r.Time2Us:F1}us) — {LastResultText}" +
                           (r.InTargetRange() ? "" : " [목표 4~6 m/s 벗어남]"),
                           clean ? LogLevel.Info : LogLevel.Warning);

            // 불토출은 개별 노즐 문제라 따로 남긴다 — 요약 한 줄에 묻히면 놓친다.
            if (r.Grid != null && r.Grid.MissingNozzles.Count > 0)
                _mainVM.AddLog($"[VISION] DropWatcher: 불토출 노즐 — {string.Join(",", r.Grid.MissingNozzles)}" +
                               (r.Grid.AbsoluteMappingConfident ? "" : " (번호 참고값 — 양 끝 불토출 시 밀릴 수 있음)"),
                               LogLevel.Warning);

            int n = r.Nozzles.Count;
            var labels = Enumerable.Range(0, n).Select(i => i.ToString()).ToArray();
            ApplyCharts(labels,
                        r.Nozzles.Select(v => v.VelocityMps).ToArray(),
                        r.Nozzles.Select(v => v.FallDistanceUm).ToArray(),
                        r.Nozzles.Select(v => v.VolumePl).ToArray(),
                        "Fall in Δt (um)",
                        "Δt 낙하거리 Fall in Δt (µm) — X: 노즐 번호");
        }

        // ── Live View: 연속 캡쳐로 최신 프레임을 계속 갱신 ─────────────────────
        private void ToggleLiveView()
        {
            if (IsLiveView)
            {
                _liveTimer.Stop();
                IsLiveView = false;
                _mainVM.AddLog("[VISION] DropWatcher: Live View 정지", LogLevel.Info);
            }
            else
            {
                IsLiveView = true;
                _liveTimer.Start();
                _mainVM.AddLog("[VISION] DropWatcher: Live View 시작", LogLevel.Info);
            }
        }

        private async Task LiveGrabAsync()
        {
            if (_liveGrabbing || IsBusy) return;   // 측정 중이거나 이전 캡쳐 진행 중이면 건너뜀
            _liveGrabbing = true;
            try
            {
                if (!_liveFirstTickLogged)
                {
                    _liveFirstTickLogged = true;   // 크래시 지점 특정용(2026-07-23) — 원인 확정 후 정리
                    LoggerService.WriteToFile("INFO", "[DW] Live 첫 캡쳐 호출");
                }
                // saveToDisk:false — 라이브는 초당 5장이라 파일로 남기면 디스크가 순식간에 찬다.
                // 픽셀 버퍼를 그대로 화면에 그린다(파일이 없으므로 CurrentImagePath 는 건드리지 않음).
                var image = await _vision.CaptureAsync(CamId, saveToDisk: false);
                if (image.IsValid)
                {
                    if (!_liveFirstFrameLogged)
                    {
                        _liveFirstFrameLogged = true;
                        LoggerService.WriteToFile("INFO",
                            $"[DW] Live 첫 프레임 수신 ({image.Width}x{image.Height}, {image.PixelData?.Length ?? 0} bytes)");
                    }
                    _liveInvalidCount = 0;
                    var frame = Vision.VisionDriverImageSource.FromPixels(image);
                    if (frame != null)
                    {
                        CurrentFrame       = frame;
                        _measureSourcePath = null;   // 라이브 화면 → 측정은 그 시점에 새로 1장 캡쳐
                    }
                }
                else if (++_liveInvalidCount >= LiveInvalidStopCount)
                {
                    // 실카메라 촬영 실패가 계속되는데 화면은 이전 이미지에 머물러 "라이브가 안 바뀐다"로
                    // 보였던 실장 이슈(2026-07-23) — 조용히 넘기지 말고 알리고 정지한다.
                    _mainVM.AddLog("[VISION] DropWatcher: Live 프레임 획득이 계속 실패합니다 — " +
                                   "VisionConfig 해상도/픽셀포맷 확인 필요 (C:\\Logs 의 [IMAQdx Vision] 참조)",
                                   LogLevel.Error);
                    _liveTimer.Stop();
                    IsLiveView = false;
                    _liveInvalidCount = 0;
                }
                CamStatus = _vision.GetStatus(CamId);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: Live 캡쳐 실패: {ex.Message}", LogLevel.Error);
                _liveTimer.Stop();
                IsLiveView = false;
            }
            finally { _liveGrabbing = false; }
        }

        // ── 이미지 파일 열기 ──────────────────────────────────────────────────
        // 저장된 캡쳐/샘플 이미지를 불러와 화면에 표시한다(측정은 하지 않음).
        private void ExecuteOpenImage()
        {
            string defaultDir = Path.Combine(@"C:\Logs\Vision", CamId);
            if (!Directory.Exists(defaultDir)) defaultDir = @"C:\Logs\Vision";
            if (!Directory.Exists(defaultDir)) defaultDir = Path.GetDirectoryName(SampleImagePath) ?? "";
            if (!Directory.Exists(defaultDir))
                defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title            = "이미지 파일 선택",
                Filter           = "이미지 파일|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|모든 파일|*.*",
                InitialDirectory = defaultDir,
                Multiselect      = false,
            };
            if (dlg.ShowDialog() != true) return;

            _measureSourcePath = dlg.FileName;   // 연 이미지가 곧 측정 대상
            CurrentImagePath   = dlg.FileName;
            _mainVM.AddLog($"[VISION] DropWatcher: 이미지 로드: {Path.GetFileName(dlg.FileName)}", LogLevel.Info);
        }

        private void RaiseMeasureCanExecute()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ((RelayCommand)MeasureVelocityCommand).RaiseCanExecuteChanged();
                ((RelayCommand)TimeIntervalMeasureCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CalibrateScaleCommand).RaiseCanExecuteChanged();
            });
        }

        // ── 캘리브레이션 ───────────────────────────────────────────────────────
        // 현재 화면 이미지에서 액적을 검출해, 입력한 노즐 피치(µm)로 µm/px 를 자동 산출한다.
        // 정상 토출 프레임(노즐당 액적 1개)이어야 피치가 정확하다.
        private async Task ExecuteCalibrateScaleAsync()
        {
            if (NozzlePitchUm <= 0)
            {
                _mainVM.AddLog("[VISION] DropWatcher: 노즐 피치(µm)를 먼저 입력하세요.", LogLevel.Warning);
                return;
            }

            IsBusy = true;
            RaiseMeasureCanExecute();
            try
            {
                VisionImage frame;
                if (!string.IsNullOrEmpty(_measureSourcePath) && File.Exists(_measureSourcePath))
                    frame = new VisionImage { CameraId = CamId, FilePath = _measureSourcePath, IsValid = true };
                else
                    frame = await _vision.CaptureAsync(CamId, saveToDisk: true);

                var drops = await Task.Run(() => _proc.DetectDroplets(frame));
                double umpp = DropWatcherProcessor.CalibrateMicronsPerPixel(drops, NozzlePitchUm);
                if (double.IsNaN(umpp))
                {
                    _mainVM.AddLog(
                        $"[VISION] DropWatcher: µm/px 교정 실패 (검출 액적 {drops.Count}개 — 2개 이상 필요)",
                        LogLevel.Warning);
                    return;
                }

                MicronsPerPixel = umpp;
                _mainVM.AddLog(
                    $"[VISION] DropWatcher: µm/px 교정 완료 — {umpp:F3} µm/px (노즐 {drops.Count}개, 피치 {NozzlePitchUm:F1}µm)",
                    LogLevel.Success);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: µm/px 교정 실패: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsBusy = false;
                RaiseMeasureCanExecute();
            }

            // 교정값으로 즉시 재측정(위 finally 로 IsBusy 해제 후 — 재측정이 IsBusy 를 다시 잡는다)
            await ExecuteMeasureAsync("Calibrate");
        }

        // 현재 캘리브레이션(µm/px, 노즐면 Y 등 전체 파라미터)을 DropWatcherConfig.json 에 저장.
        // 초점 기준 저장 — 지금 화면 이미지의 선명도를 기준값으로 삼는다.
        // 작업자가 "초점이 맞았다"고 확인한 시점에 눌러야 의미가 있다. 이후 측정은 이 기준 대비
        // 비율로 초점 이탈을 판정한다(선명도 절대값은 렌즈·배율마다 자릿수가 달라 기준이 못 된다).
        private void ExecuteCaptureFocusReference()
        {
            try
            {
                VisionImage frame;
                if (!string.IsNullOrEmpty(_measureSourcePath) && File.Exists(_measureSourcePath))
                    frame = new VisionImage { CameraId = CamId, FilePath = _measureSourcePath, IsValid = true };
                else
                {
                    _mainVM.AddLog("[VISION] DropWatcher: 초점 기준 저장 — 기준으로 삼을 이미지가 없습니다. " +
                                   "이미지를 열거나 측정을 먼저 하세요.", LogLevel.Warning);
                    return;
                }

                double sharpness = _proc.CaptureSharpnessReference(frame);
                if (sharpness <= 0)
                {
                    _mainVM.AddLog("[VISION] DropWatcher: 초점 기준 저장 실패 — 선명도를 측정할 수 없습니다.",
                                   LogLevel.Warning);
                    return;
                }

                OnPropertyChanged(nameof(ReferenceSharpness));
                _mainVM.AddLog($"[VISION] DropWatcher: 초점 기준 저장 — 선명도 {sharpness:F1} " +
                               $"(이후 {_procCfg.MinSharpnessRatio * 100:F0}% 미만이면 초점 저하로 표시). " +
                               "'교정값 저장'을 눌러야 파일에 남습니다.", LogLevel.Success);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: 초점 기준 저장 실패: {ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>저장된 초점 기준 선명도(0 이면 미설정 — 초점 검사 비활성).</summary>
        public double ReferenceSharpness => _procCfg.ReferenceSharpness;

        private void ExecuteSaveCalibration()
        {
            try
            {
                new ConfigLoader().SaveDropWatcherConfig(_cfgPath, _procCfg);
                _mainVM.AddLog(
                    $"[VISION] DropWatcher: 캘리브레이션 저장 — {MicronsPerPixel:F3} µm/px, 노즐면 Y={NozzleYPixel:F0}px",
                    LogLevel.Success);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] DropWatcher: 캘리브레이션 저장 실패: {ex.Message}", LogLevel.Error);
            }
        }

        // ── 그래프 초기 상태 ────────────────────────────────────────────────
        // 측정 전에는 빈 그래프(축만)로 둔다. 예전에는 데모용 가짜 곡선을 그렸는데,
        // 실측 결과처럼 보여 오해를 부르고 축(Time/Drop 1~5)도 실제 측정 축(Nozzle #)과 달랐다.
        // 측정하면 BuildDropletCharts 가 시리즈/축을 채운다.
        private void BuildCharts()
        {
            VelocitySeries = Array.Empty<ISeries>();
            PositionSeries = Array.Empty<ISeries>();
            SpitRateSeries = Array.Empty<ISeries>();

            // 축 이름은 측정 후와 동일하게 유지 → 측정 전후로 라벨이 바뀌지 않는다.
            VelocityXAxes = MakeXAxes(Array.Empty<string>(), XAxisName);
            PositionXAxes = MakeXAxes(Array.Empty<string>(), XAxisName);
            SpitRateXAxes = MakeXAxes(Array.Empty<string>(), XAxisName);
            VelocityYAxes = MakeYAxes("Velocity (m/s)");
            PositionYAxes = MakeYAxes("Drop Position (um)");
            SpitRateYAxes = MakeYAxes("Volume (pL)");

            OnPropertyChanged(nameof(VelocitySeries));
            OnPropertyChanged(nameof(PositionSeries));
            OnPropertyChanged(nameof(SpitRateSeries));
            OnPropertyChanged(nameof(VelocityXAxes));
            OnPropertyChanged(nameof(PositionXAxes));
            OnPropertyChanged(nameof(SpitRateXAxes));
            OnPropertyChanged(nameof(VelocityYAxes));
            OnPropertyChanged(nameof(PositionYAxes));
            OnPropertyChanged(nameof(SpitRateYAxes));
        }

        private static ISeries MakeLine(string name, SKColor color, double[] values) =>
            new LineSeries<double>
            {
                Name           = name,
                Values         = values,
                Stroke         = new SolidColorPaint(color, 1.6f),
                Fill           = null,
                GeometrySize   = 5,
                GeometryStroke = new SolidColorPaint(color, 1.6f),
                GeometryFill   = new SolidColorPaint(SKColors.White),
                LineSmoothness = 0.2,
            };

        private static Axis[] MakeXAxes(string[] labels, string name) => new[]
        {
            new Axis
            {
                Labels          = labels,
                Name            = name,
                TextSize        = 10,
                NamePaint       = TextPaint(),
                LabelsPaint     = TextPaint(),
                SeparatorsPaint = new SolidColorPaint(AxisGrid) { StrokeThickness = 0.5f },
            }
        };

        private static Axis[] MakeYAxes(string name) => new[]
        {
            new Axis
            {
                Name            = name,
                TextSize        = 10,
                NamePaint       = TextPaint(),
                LabelsPaint     = TextPaint(),
                SeparatorsPaint = new SolidColorPaint(AxisGrid) { StrokeThickness = 0.5f },
            }
        };

        public void Dispose()
        {
            _liveTimer.Stop();
            _pollTimer.Stop();
            _measureCts?.Cancel();
            _measureCts?.Dispose();
            // 화면을 떠나도 헤드가 계속 토출하면 안 된다. 종료 경로라 무한정 기다리진 않는다.
            try { _trigger.Stop(); } catch { }
            _trigger.Dispose();
            try { _spit.StopAsync(1_000).Wait(2_000); } catch { }
            _spit.Dispose();
            try { _strobe.Enable(false); } catch { /* 이미 끊긴 포트 — 종료 경로라 무시 */ }
            _strobe.Dispose();
        }
    }
}
