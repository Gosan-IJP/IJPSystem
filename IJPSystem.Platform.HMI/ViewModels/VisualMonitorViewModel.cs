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

        public VisualMonitorViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            var vision = mainVM.GetController().GetMachine().Vision;

            // 설정된 카메라를 각각 뷰 소스로(공용 IVisionDriver 어댑터)
            foreach (var st in vision.GetAllStatus())
            {
                string name = string.IsNullOrEmpty(st.Name) ? st.CameraId : st.Name;
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
            _selectedSource = Sources[0];

            ToggleCrossLineCommand = new RelayCommand(_ => CrossLineVisible = !CrossLineVisible);
            CenterCrossCommand     = new RelayCommand(_ => CenterCross());
            SetToolCommand         = new RelayCommand(t => { if (Enum.TryParse<ViewTool>(t?.ToString(), out var vt)) Tool = vt; });

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
        private ViewTool _tool = ViewTool.Select;
        public ViewTool Tool { get => _tool; set => SetProperty(ref _tool, value); }

        // ── 줌 ───────────────────────────────────────────────────────────────
        private double _zoom = 1.0;
        public double Zoom { get => _zoom; set => SetProperty(ref _zoom, Math.Clamp(value, 0.1, 10)); }

        public ICommand ToggleCrossLineCommand { get; }
        public ICommand CenterCrossCommand     { get; }
        public ICommand SetToolCommand         { get; }

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
