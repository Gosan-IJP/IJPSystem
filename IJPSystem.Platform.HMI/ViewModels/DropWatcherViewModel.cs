using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.IO;
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

        // ── 커맨드 ────────────────────────────────────────────────────────────
        public ICommand SetDelay1Command           { get; }
        public ICommand SetDelay2Command           { get; }
        public ICommand NozzleSelectCommand        { get; }
        public ICommand AbortCommand               { get; }
        public ICommand MeasureVelocityCommand     { get; }
        public ICommand TimeIntervalMeasureCommand { get; }

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

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += (_, _) => CamStatus = _vision.GetStatus(CamId);
            _pollTimer.Start();

            CamStatus = _vision.GetStatus(CamId);

            // 샘플 Raw 이미지가 있으면 화면 진입 시 바로 표시
            if (File.Exists(SampleImagePath))
                CurrentImagePath = SampleImagePath;
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

        public void Dispose() => _pollTimer.Stop();
    }
}
