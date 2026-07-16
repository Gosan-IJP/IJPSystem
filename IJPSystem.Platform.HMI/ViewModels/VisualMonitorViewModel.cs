using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.HMI.Vision;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>미리보기 뷰 도구.</summary>
    public enum ViewTool { Zoom, Select, Pan }

    /// <summary>
    /// "Visual Monitor" 화면 — 다중 카메라 소스 선택 + 라이브 프레임 + 크로스라인(SW 오버레이) + 뷰 툴(줌/이동/선택).
    /// 카메라는 공용 IVisionDriver 를 감싼 뷰 소스(VisionDriverImageSource)로 통일해 사용한다.
    /// </summary>
    public class VisualMonitorViewModel : ViewModelBase, IDisposable
    {
        private readonly MainViewModel _mainVM;
        private readonly Dictionary<string, IImageSource> _sources = new();
        private readonly DispatcherTimer _timer;
        private bool _grabbing;

        public ObservableCollection<string> Sources { get; } = new();

        /// <param name="preferredSource">
        /// 기본 선택 소스의 이름 일부(예: "Drop"). 일치하는 소스가 없으면 첫 번째 소스를 쓴다.
        /// </param>
        public VisualMonitorViewModel(MainViewModel mainVM, string? preferredSource = null)
        {
            _mainVM = mainVM;
            var vision = mainVM.GetController().GetMachine().Vision;

            // 설정된 카메라를 각각 뷰 소스로(공용 IVisionDriver 어댑터).
            // 표시명은 DisplayLabel(=DisplayName→Name→CameraId), 노출 여부는 ShowInMonitor(Config) 를 따른다.
            foreach (var st in vision.GetAllStatus())
            {
                if (!st.ShowInMonitor) continue;   // 전용 화면만 쓰는 카메라는 모니터 소스에서 제외
                string name = st.DisplayLabel;
                if (_sources.ContainsKey(name)) name = st.CameraId;   // 이름 중복 시 ID 로
                _sources[name] = new VisionDriverImageSource(name, vision, st.CameraId);
                Sources.Add(name);
            }
            if (Sources.Count == 0)   // 카메라 미설정 시 가상 소스로 대체
            {
                var v = new VirtualImageSource("Virtual");
                _sources[v.Name] = v;
                Sources.Add(v.Name);
            }
            _selectedSource = PickDefaultSource(preferredSource);

            ToggleCrossLineCommand = new RelayCommand(_ => CrossLineVisible = !CrossLineVisible);
            CenterCrossCommand     = new RelayCommand(_ => CenterCross());
            SetToolCommand         = new RelayCommand(t => { if (Enum.TryParse<ViewTool>(t?.ToString(), out var vt)) Tool = vt; });
            // 버튼을 누르면 바로 눈에 보이게 동작하도록 확대/축소/맞춤은 툴 모드와 무관한 즉시 동작으로 둔다.
            ZoomInCommand          = new RelayCommand(_ => Zoom *= 1.25);
            ZoomOutCommand         = new RelayCommand(_ => Zoom /= 1.25);
            FitCommand             = new RelayCommand(_ => FitRequested?.Invoke(this, EventArgs.Empty));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };   // ~5 fps (파일 경유)
            _timer.Tick += async (_, _) => await UpdateFrameAsync();
            OpenSelected();
            _timer.Start();

            _mainVM.AddLog("[VISION] Visual Monitor 진입", LogLevel.Info);
        }

        // ── 소스 ─────────────────────────────────────────────────────────────
        private string _selectedSource;
        public string SelectedSource
        {
            get => _selectedSource;
            set { if (SetProperty(ref _selectedSource, value)) OpenSelected(); }
        }

        // ── 프레임 ───────────────────────────────────────────────────────────
        private BitmapSource? _frame;
        public BitmapSource? Frame { get => _frame; private set => SetProperty(ref _frame, value); }

        // ── 크로스라인 (디바이스 무관 SW 오버레이) ────────────────────────────
        private bool _crossVisible = true;
        public bool CrossLineVisible { get => _crossVisible; set => SetProperty(ref _crossVisible, value); }

        private double _crossX = 0.5, _crossY = 0.5;
        public double CrossXRatio { get => _crossX; set => SetProperty(ref _crossX, Clamp01(value)); }
        public double CrossYRatio { get => _crossY; set => SetProperty(ref _crossY, Clamp01(value)); }
        public void CenterCross() { CrossXRatio = 0.5; CrossYRatio = 0.5; }

        // ── 뷰 툴 ────────────────────────────────────────────────────────────
        // 기본은 Pan(드래그 이동) — 뷰어로서 가장 안전한 기본값.
        // (Select 는 클릭 위치로 크로스라인을 옮기므로 기본값이면 실수로 움직이기 쉽다)
        private ViewTool _tool = ViewTool.Pan;
        public ViewTool Tool { get => _tool; set => SetProperty(ref _tool, value); }

        // ── 줌 ───────────────────────────────────────────────────────────────
        private double _zoom = 1.0;
        public double Zoom { get => _zoom; set => SetProperty(ref _zoom, Math.Clamp(value, 0.1, 10)); }

        public ICommand ToggleCrossLineCommand { get; }
        public ICommand CenterCrossCommand     { get; }
        public ICommand SetToolCommand         { get; }
        public ICommand ZoomInCommand          { get; }
        public ICommand ZoomOutCommand         { get; }
        public ICommand FitCommand             { get; }

        /// <summary>
        /// 화면 맞춤 요청. 뷰포트 크기는 View 만 알기 때문에 실제 계산은 View 가 수행한다.
        /// </summary>
        public event EventHandler? FitRequested;

        // 기본 소스 선택: preferred(부분일치, 대소문자 무시) → 없으면 첫 번째.
        private string PickDefaultSource(string? preferred)
        {
            if (!string.IsNullOrEmpty(preferred))
            {
                foreach (var s in Sources)
                    if (s.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0) return s;
            }
            return Sources[0];
        }

        // ── 불러온 이미지 ────────────────────────────────────────────────────
        // 소스 목록의 고정 슬롯 하나를 재사용한다(다시 Load 해도 항목이 늘지 않음).
        private const string LoadedImageSourceName = "Loaded Image";
        private StaticImageSource? _loadedImage;

        /// <summary>
        /// 이미지 파일을 읽어 "Loaded Image" 소스로 등록하고 즉시 선택한다.
        /// 카메라와 같은 소스 목록에 넣으므로 줌/팬/크로스라인이 그대로 동작하고,
        /// 콤보박스에서 카메라를 다시 고르면 라이브로 복귀한다.
        /// </summary>
        /// <returns>성공 여부. 실패 사유는 로그로 남긴다.</returns>
        public bool LoadImageFromFile(string path)
        {
            try
            {
                var bmp = LoadFrozen(path);

                if (_loadedImage == null)
                {
                    _loadedImage = new StaticImageSource(LoadedImageSourceName);
                    _sources[LoadedImageSourceName] = _loadedImage;
                    Sources.Add(LoadedImageSourceName);
                }
                _loadedImage.SetImage(bmp, path);
                _loadedImage.Open();

                // 이미 선택돼 있으면 SelectedSource 대입이 무시되므로(값 동일) 프레임을 직접 반영한다.
                SelectedSource = LoadedImageSourceName;
                Frame = bmp;
                FitRequested?.Invoke(this, EventArgs.Empty);

                _mainVM.AddLog($"[VISION] 이미지 로드: {System.IO.Path.GetFileName(path)}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VISION] 이미지 로드 실패 — {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        // 파일 잠금을 피하려고 OnLoad 로 전부 읽어들인 뒤 Freeze(다른 스레드 접근 허용)
        private static BitmapSource LoadFrozen(string path)
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

        /// <summary>라이브 갱신 시작. 화면이 보일 때만 호출(불필요한 백그라운드 캡쳐 방지).</summary>
        public void Start()
        {
            OpenSelected();
            if (!_timer.IsEnabled) _timer.Start();
        }

        /// <summary>라이브 갱신 정지. 화면을 벗어나면 호출.</summary>
        public void Stop()
        {
            if (_timer.IsEnabled) _timer.Stop();
        }

        private void OpenSelected()
        {
            if (_selectedSource != null && _sources.TryGetValue(_selectedSource, out var src) && !src.IsOpen)
                src.Open();
        }

        private async Task UpdateFrameAsync()
        {
            if (_grabbing) return;
            if (_selectedSource == null || !_sources.TryGetValue(_selectedSource, out var src)) return;
            _grabbing = true;
            try
            {
                var f = await src.GrabFrameAsync();
                if (f != null) Frame = f;   // 프레임 없으면(카메라 미연결) 이전 화면 유지
            }
            catch { /* 라이브 오류는 무시(다음 틱 재시도) */ }
            finally { _grabbing = false; }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        public void Dispose()
        {
            _timer.Stop();
            foreach (var s in _sources.Values) s.Dispose();
        }
    }
}
