using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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
        private bool _liveGrabbing;                     // 캡쳐 중복 방지

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

        private string? _currentImagePath;
        public string? CurrentImagePath
        {
            get => _currentImagePath;
            private set
            {
                if (SetProperty(ref _currentImagePath, value))
                {
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(HasNoImage));
                }
            }
        }
        public bool HasImage   => !string.IsNullOrEmpty(CurrentImagePath);
        public bool HasNoImage => string.IsNullOrEmpty(CurrentImagePath);

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
            private set { if (SetProperty(ref _isLiveView, value)) OnPropertyChanged(nameof(LiveViewLabel)); }
        }
        public string LiveViewLabel => IsLiveView ? "■ Stop" : "▶ Live";

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

        // ── 측정 그래프 (Velocity / Drop Position / Side Spit Rate vs Time) ────
        // Sample DW.vi 우측의 3개 그래프. 각 그래프는 Drop 1~5 시리즈로 구성된다.
        // 실제 측정 알고리즘 연결 전까지는 대표 샘플 곡선으로 화면 형태를 보여준다.
        public ISeries[] VelocitySeries { get; private set; } = Array.Empty<ISeries>();
        public ISeries[] PositionSeries { get; private set; } = Array.Empty<ISeries>();
        public ISeries[] SpitRateSeries { get; private set; } = Array.Empty<ISeries>();

        public Axis[] VelocityXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] VelocityYAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] PositionXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] PositionYAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] SpitRateXAxes { get; private set; } = Array.Empty<Axis>();
        public Axis[] SpitRateYAxes { get; private set; } = Array.Empty<Axis>();

        // Drop 1~5 시리즈 이름/색상(범례 색과 일치)
        private static readonly (string name, SKColor color)[] DropDefs =
        {
            ("Drop 1", SKColors.DodgerBlue),
            ("Drop 2", SKColors.Red),
            ("Drop 3", SKColors.LimeGreen),
            ("Drop 4", new SKColor(0x84, 0xCC, 0x16)),
            ("Drop 5", SKColors.Cyan),
        };

        private static readonly SKColor AxisText = new SKColor(0x94, 0xA3, 0xB8);
        private static readonly SKColor AxisGrid = new SKColor(0x33, 0x41, 0x55);

        // ── 커맨드 ────────────────────────────────────────────────────────────
        public ICommand SetDelay1Command           { get; }
        public ICommand SetDelay2Command           { get; }
        public ICommand NozzleSelectCommand        { get; }
        public ICommand AbortCommand               { get; }
        public ICommand MeasureVelocityCommand     { get; }
        public ICommand TimeIntervalMeasureCommand { get; }
        public ICommand ToggleLiveViewCommand      { get; }

        public DropWatcherViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            _vision = mainVM.GetController().GetMachine().Vision;

            SetDelay1Command           = new RelayCommand(_ => ExecuteSetDelay(1));
            SetDelay2Command           = new RelayCommand(_ => ExecuteSetDelay(2));
            NozzleSelectCommand        = new RelayCommand(_ => LogPlaceholder("Nozzle Select"));
            AbortCommand               = new RelayCommand(_ => ExecuteAbort());
            MeasureVelocityCommand     = new RelayCommand(async _ => await ExecuteMeasureAsync("Measure Velocity"),      _ => !IsBusy);
            TimeIntervalMeasureCommand = new RelayCommand(async _ => await ExecuteMeasureAsync("Time Interval Measure"), _ => !IsBusy);
            ToggleLiveViewCommand      = new RelayCommand(_ => ToggleLiveView());

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += (_, _) => CamStatus = _vision.GetStatus(CamId);
            _pollTimer.Start();

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };  // 약 5 fps
            _liveTimer.Tick += async (_, _) => await LiveGrabAsync();

            CamStatus = _vision.GetStatus(CamId);

            // 샘플 Raw 이미지가 있으면 화면 진입 시 바로 표시
            if (File.Exists(SampleImagePath))
                CurrentImagePath = SampleImagePath;

            BuildCharts();
        }

        // Set Delay Time 버튼 — 현재 Delay Time 값을 Delay 1/2 및 적용값으로 반영
        private void ExecuteSetDelay(int which)
        {
            if (which == 1) Delay1Us = DelayTimeUs;
            else            Delay2Us = DelayTimeUs;
            AppliedDelayUs = DelayTimeUs;
            LogPlaceholder($"Set Delay Time {which} ← {DelayTimeUs:F1} us");
        }

        private void ExecuteAbort()
        {
            IsBusy = false;
            RaiseMeasureCanExecute();
            _mainVM.AddLog("[VISION] DropWatcher: Abort", LogLevel.Warning);
        }

        // Measure Velocity / Time Interval Measure — 현재는 캡쳐만 수행하고 측정 알고리즘은 추후 연결
        private async Task ExecuteMeasureAsync(string action)
        {
            IsBusy = true;
            RaiseMeasureCanExecute();
            try
            {
                // 샘플 Raw 이미지가 있으면 그것을 검사 대상으로 사용, 없으면 가상 캡쳐
                if (File.Exists(SampleImagePath))
                {
                    CurrentImagePath = SampleImagePath;
                    _mainVM.AddLog($"[VISION] DropWatcher: {action} — Raw 샘플 이미지 사용 (측정 알고리즘 미구현)", LogLevel.Info);
                }
                else
                {
                    var image = await _vision.CaptureAsync(CamId);
                    if (image.IsValid)
                        CurrentImagePath = image.FilePath;
                    _mainVM.AddLog($"[VISION] DropWatcher: {action} — 캡쳐 완료 (측정 알고리즘 미구현)", LogLevel.Info);
                }

                // 측정 결과 그래프 갱신 (알고리즘 연결 전까지 대표 샘플 곡선)
                BuildCharts(action.GetHashCode());
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
                var image = await _vision.CaptureAsync(CamId);
                if (image.IsValid) CurrentImagePath = image.FilePath;
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

        private void LogPlaceholder(string action) =>
            _mainVM.AddLog($"[VISION] DropWatcher: {action} (미구현)", LogLevel.Info);

        private void RaiseMeasureCanExecute()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ((RelayCommand)MeasureVelocityCommand).RaiseCanExecuteChanged();
                ((RelayCommand)TimeIntervalMeasureCommand).RaiseCanExecuteChanged();
            });
        }

        // ── 그래프 데이터 구성 ──────────────────────────────────────────────
        // 실제 DW Vision 측정 알고리즘 연결 전까지 대표 샘플 곡선으로 형태를 표시한다.
        // 연결 시 이 메서드를 측정 결과(Drop 1~5 시계열)로 채우면 된다.
        private void BuildCharts(int seed = 0)
        {
            const int n = 18;                                    // Time 0~17 (um)
            string[] timeLabels = Enumerable.Range(0, n).Select(t => t.ToString()).ToArray();
            double phase = (seed % 7) * 0.3;

            var velocity = new ISeries[DropDefs.Length];
            var position = new ISeries[DropDefs.Length];
            var spitRate = new ISeries[DropDefs.Length];

            for (int s = 0; s < DropDefs.Length; s++)
            {
                double off = s * 0.06;
                var vv = new double[n];
                var pp = new double[n];
                var sr = new double[n];
                for (int t = 0; t < n; t++)
                {
                    // Velocity(m/s): 약 4.5~7.5, 시리즈별 약간의 편차
                    vv[t] = 6.0 + 1.1 * Math.Sin(t * 0.7 + phase) - 0.5 * Math.Sin(t * 0.33) + off;
                    // Drop Position(um): 약 150~650, 거의 선형 증가(시리즈 중첩)
                    pp[t] = 150 + t * 29 + off * 12;
                    // Side Spit Rate(%): 0 부근, t=11 근처 스파이크
                    sr[t] = 0.18 * Math.Sin(t * 0.9 + s + phase) + (t == 11 ? 0.35 : 0) - (t == 12 ? 0.35 : 0);
                }
                velocity[s] = MakeLine(DropDefs[s].name, DropDefs[s].color, vv);
                position[s] = MakeLine(DropDefs[s].name, DropDefs[s].color, pp);
                spitRate[s] = MakeLine(DropDefs[s].name, DropDefs[s].color, sr);
            }

            VelocitySeries = velocity;
            PositionSeries = position;
            SpitRateSeries = spitRate;

            VelocityXAxes = MakeXAxes(timeLabels);
            PositionXAxes = MakeXAxes(timeLabels);
            SpitRateXAxes = MakeXAxes(timeLabels);
            VelocityYAxes = MakeYAxes("Velocity (m/s)");
            PositionYAxes = MakeYAxes("Drop Position (um)");
            SpitRateYAxes = MakeYAxes("Side Spit Rate (%)");

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

        private static Axis[] MakeXAxes(string[] labels) => new[]
        {
            new Axis
            {
                Labels          = labels,
                Name            = "Time (um)",
                TextSize        = 10,
                NamePaint       = new SolidColorPaint(AxisText),
                LabelsPaint     = new SolidColorPaint(AxisText),
                SeparatorsPaint = new SolidColorPaint(AxisGrid) { StrokeThickness = 0.5f },
            }
        };

        private static Axis[] MakeYAxes(string name) => new[]
        {
            new Axis
            {
                Name            = name,
                TextSize        = 10,
                NamePaint       = new SolidColorPaint(AxisText),
                LabelsPaint     = new SolidColorPaint(AxisText),
                SeparatorsPaint = new SolidColorPaint(AxisGrid) { StrokeThickness = 0.5f },
            }
        };

        public void Dispose()
        {
            _liveTimer.Stop();
            _pollTimer.Stop();
        }
    }
}
