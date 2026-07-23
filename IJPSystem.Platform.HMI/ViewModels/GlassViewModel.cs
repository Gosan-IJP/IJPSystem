using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Vision;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class GlassViewModel : ViewModelBase, IDisposable
    {
        private const string CamId = "CAM_02";

        private readonly IVisionDriver _vision;
        private readonly MainViewModel _mainVM;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _liveTimer;

        private CancellationTokenSource? _liveCts;

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
        private int _liveIntervalMs = 200;
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

        // ── 처리 중 상태 ──────────────────────────────────────────────────────
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        // ── 커맨드 ────────────────────────────────────────────────────────────
        public ICommand StartLiveCommand  { get; }
        public ICommand StopLiveCommand   { get; }
        public ICommand ToggleLiveCommand { get; }
        public ICommand CaptureCommand    { get; }
        public ICommand LightOnCommand    { get; }
        public ICommand LightOffCommand   { get; }
        public ICommand OpenImageCommand  { get; }

        public GlassViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            _vision = mainVM.GetController().GetMachine().Vision;

            StartLiveCommand  = new RelayCommand(_ => StartLive(),              _ => !IsLiveMode && !IsBusy);
            StopLiveCommand   = new RelayCommand(_ => StopLive(),               _ => IsLiveMode);
            ToggleLiveCommand = new RelayCommand(_ => { if (IsLiveMode) StopLive(); else StartLive(); });
            // 라이브 중에도 단발 캡쳐 허용(실장 피드백 2026-07-23) — IsBusy 게이트가 라이브 틱과
            // 겹침을 막아주고, 캡쳐 순간의 프레임이 파일로 저장된 뒤 라이브는 그대로 이어진다.
            CaptureCommand    = new RelayCommand(async _ => await ExecuteCaptureAsync(), _ => !IsBusy);
            LightOnCommand   = new RelayCommand(_ => ExecuteLight(true),  _ => !IsBusy);
            LightOffCommand  = new RelayCommand(_ => ExecuteLight(false), _ => !IsBusy);
            OpenImageCommand = new RelayCommand(_ => ExecuteOpenImage(),  _ => !IsLiveMode);

            // 카메라 상태 폴링 (500ms)
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => CamStatus = _vision.GetStatus(CamId);
            _statusTimer.Start();

            // 라이브 캡쳐 타이머
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_liveIntervalMs) };
            _liveTimer.Tick += async (_, _) => await LiveTickAsync();

            CamStatus = _vision.GetStatus(CamId);
        }

        // ── 라이브 시작 / 정지 ────────────────────────────────────────────────
        private void StartLive()
        {
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

        private async Task LiveTickAsync()
        {
            if (_liveTicking || IsBusy) return;   // 단발 캡쳐(IsBusy) 중에도 건너뜀
            _liveTicking = true;
            try
            {
                // saveToDisk:false — 라이브는 연속 캡쳐라 파일로 남기면 디스크가 순식간에 찬다.
                // 픽셀 버퍼를 그대로 화면에 그린다(파일이 없으므로 CurrentImagePath 는 건드리지 않음).
                var image = await _vision.CaptureAsync(CamId, saveToDisk: false);
                if (image.IsValid)
                {
                    var frame = Vision.VisionDriverImageSource.FromPixels(image);
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
        private void ExecuteLight(bool on)
        {
            _vision.SetLight(CamId, on);
            if (on) _vision.SetLightIntensity(CamId, LightIntensity);
            CamStatus = _vision.GetStatus(CamId);
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
        }
    }
}
