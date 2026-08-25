using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
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
        public ICommand OpenImageCommand  { get; }
        public ICommand ToggleCrossLineCommand { get; }
        public ICommand CenterCrossCommand     { get; }

        /// <summary>
        /// VisionConfig 에 실제로 들어 있는 글라스뷰 카메라 ID 를 고른다 — CAM_GV 우선, 없으면 옛 CAM_02.
        /// 둘 다 없으면 표준 ID 를 그대로 쓴다(미연결로 표시되고, 로그로 원인을 알 수 있게 한다).
        /// </summary>
        private static string ResolveCamId(IVisionDriver vision, MainViewModel mainVM)
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
            OpenImageCommand = new RelayCommand(_ => ExecuteOpenImage(),  _ => !IsLiveMode);
            // 크로스라인은 카메라와 무관한 화면 오버레이라 조건 없이 언제나 조작 가능하다.
            ToggleCrossLineCommand = new RelayCommand(_ => CrossLineVisible = !CrossLineVisible);
            CenterCrossCommand     = new RelayCommand(_ => { CrossXRatio = 0.5; CrossYRatio = 0.5; });

            // 카메라 상태 폴링 (500ms)
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statusTimer.Tick += (_, _) => CamStatus = _vision.GetStatus(CamId);
            _statusTimer.Start();

            // 라이브 캡쳐 타이머
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_liveIntervalMs) };
            _liveTimer.Tick += async (_, _) => await LiveTickAsync();

            CamStatus = _vision.GetStatus(CamId);

            // 정렬(패턴 매칭)은 별도 VM 이 맡는다 — 카메라·조명·조그와 성격이 다르고,
            // 화면 없이 값만 검증할 수 있어야 한다.
            Align = new Vision.PatternAlignViewModel(() => CurrentFrame, _mainVM.AddLog);
            BindMinScoreToRecipe();
        }

        /// <summary>글라스 정렬 — 패턴 등록·저장·찾기.</summary>
        public Vision.PatternAlignViewModel Align { get; }

        /// <summary>
        /// 합격 점수는 레시피가 주인이다 — 여기로 밀어 넣고, 레시피에서 바뀌면 따라간다.
        ///
        /// <para>글라스 화면에서 고칠 수 있게 두면 어느 기준으로 찾은 결과인지가 레시피에
        /// 남지 않는다. 그래서 화면은 보여 주기만 하고, 값은 이 한 방향으로만 흐른다.</para>
        /// </summary>
        private void BindMinScoreToRecipe()
        {
            var recipe = _mainVM.RecipeVM;
            if (recipe == null) return;   // 초기화 순서에 따라 아직 없을 수 있다

            Align.MinScore = recipe.PatternMinScore;
            recipe.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RecipeViewModel.PatternMinScore))
                    Align.MinScore = recipe.PatternMinScore;
            };
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
        private readonly Vision.LiveFrameBuffer _liveBuffer = new();

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
        }
    }
}
