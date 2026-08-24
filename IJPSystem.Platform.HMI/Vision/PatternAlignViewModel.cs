using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Vision;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IJPSystem.Platform.HMI.Vision
{
    /// <summary>
    /// 글라스 정렬 — 패턴 등록 · 저장 · 찾기.
    ///
    /// <para><b>지금 하는 것은 "얼마나 벗어났나"까지다.</b> 스테이지를 움직이지는 않는다.
    /// 매칭이 엉뚱한 곳을 잡았을 때 그대로 모터가 나가면 안 되고, 애초에 픽셀을 mm 로
    /// 바꿀 교정값(µm/px)이 아직 없다 — 그게 서면 그때 이동을 붙인다.</para>
    ///
    /// <para>화면(GlassViewModel)에서 떼어 둔 이유: 정렬은 카메라·조명·조그와 성격이 다르고,
    /// 화면 없이 값만 검증할 수 있어야 한다.</para>
    /// </summary>
    public sealed class PatternAlignViewModel : ViewModelBase
    {
        private readonly PatternRepository _repo = new();
        private readonly Func<BitmapSource?> _frame;
        private readonly Action<string, LogLevel> _log;

        public PatternAlignViewModel(Func<BitmapSource?> currentFrame, Action<string, LogLevel> log)
        {
            _frame = currentFrame ?? throw new ArgumentNullException(nameof(currentFrame));
            _log   = log ?? ((_, _) => { });

            StartRegisterCommand = new RelayCommand(_ => StartRegister());
            CancelRegisterCommand = new RelayCommand(_ => IsRegistering = false);
            SaveCommand  = _save = new RelayCommand(_ => Save(),  _ => HasRoi);
            FindCommand  = _find = new RelayCommand(_ => Find(),  _ => HasPattern);
            ClearCommand = new RelayCommand(_ => Clear());
            LoadCommand  = new RelayCommand(p => Load(p as string ?? SelectedPattern));

            RefreshList();
            if (Patterns.Count > 0) Load(Patterns[0]);
        }

        // ── 저장된 패턴 목록 ─────────────────────────────────────────────
        public ObservableCollection<string> Patterns { get; } = new();

        private string? _selectedPattern;
        public string? SelectedPattern
        {
            get => _selectedPattern;
            set
            {
                if (!SetProperty(ref _selectedPattern, value)) return;
                if (!string.IsNullOrEmpty(value)) Load(value!);
            }
        }

        public string PatternFolder => _repo.RootDirectory;

        // 버튼이 다시 켜지려면 이 둘을 직접 흔들어 줘야 한다 — 여기 RelayCommand 는
        // CommandManager 를 쓰지 않는 쪽이라 InvalidateRequerySuggested 로는 꿈쩍도 안 한다.
        private readonly RelayCommand _save, _find;

        /// <summary>ROI/패턴이 바뀌었으니 [패턴저장]/[패턴 찾기] 를 다시 판정하게 한다.</summary>
        private void RefreshButtons()
        {
            _save.RaiseCanExecuteChanged();
            _find.RaiseCanExecuteChanged();
        }

        public ICommand StartRegisterCommand { get; }
        public ICommand CancelRegisterCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand FindCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand LoadCommand { get; }

        // ── 등록(ROI) ────────────────────────────────────────────────────
        private bool _isRegistering;
        /// <summary>드래그로 영역을 고르는 중. 켜져 있을 때만 덮개가 마우스를 받는다.</summary>
        public bool IsRegistering
        {
            get => _isRegistering;
            set
            {
                if (!SetProperty(ref _isRegistering, value)) return;
                OnPropertyChanged(nameof(RegisterButtonText));
                OnPropertyChanged(nameof(HintText));
            }
        }

        public string RegisterButtonText => IsRegistering ? "등록 취소" : "패턴등록시작";

        private double _roiX, _roiY, _roiW, _roiH;
        public double RoiX { get => _roiX; set { if (SetProperty(ref _roiX, value)) RoiChanged(); } }
        public double RoiY { get => _roiY; set { if (SetProperty(ref _roiY, value)) RoiChanged(); } }
        public double RoiW { get => _roiW; set { if (SetProperty(ref _roiW, value)) RoiChanged(); } }
        public double RoiH { get => _roiH; set { if (SetProperty(ref _roiH, value)) RoiChanged(); } }

        /// <summary>쓸 만한 크기의 영역이 잡혔나. 너무 작으면 어디에나 맞아 버린다.</summary>
        public bool HasRoi
        {
            get
            {
                var f = _frame();
                if (f == null || RoiW <= 0 || RoiH <= 0) return false;
                return RoiW * f.PixelWidth >= MinTemplatePx && RoiH * f.PixelHeight >= MinTemplatePx;
            }
        }

        /// <summary>패턴 최소 변 길이(픽셀). 이보다 작으면 반복 무늬에서 오매칭이 급증한다.</summary>
        public const int MinTemplatePx = 24;

        private void RoiChanged()
        {
            OnPropertyChanged(nameof(HasRoi));
            OnPropertyChanged(nameof(RoiSizeText));
            OnPropertyChanged(nameof(HintText));
            RefreshButtons();

            // 화면에서 잘라 미리보기를 만든다 — 무엇을 등록하려는지 눈으로 확인하고 저장해야 한다.
            UpdatePreviewFromRoi();
        }

        public string RoiSizeText
        {
            get
            {
                var f = _frame();
                if (f == null || RoiW <= 0 || RoiH <= 0) return "영역 없음";
                return $"{RoiW * f.PixelWidth:F0} × {RoiH * f.PixelHeight:F0} px";
            }
        }

        public string HintText =>
            IsRegistering ? "이미지 위에서 마크를 감싸도록 드래그하세요."
            : !HasPattern ? "등록된 패턴이 없습니다."
            : "찾기를 누르면 지금 화면에서 패턴을 찾습니다.";

        // ── 등록/불러온 패턴 ─────────────────────────────────────────────
        private GrayImage? _template;
        private PatternDefinition _definition = new();

        public bool HasPattern => _template != null;

        private BitmapSource? _preview;
        /// <summary>등록 이미지 미리보기.</summary>
        public BitmapSource? Preview
        {
            get => _preview;
            private set => SetProperty(ref _preview, value);
        }

        private string _patternName = "GlassMark";
        public string PatternName
        {
            get => _patternName;
            set => SetProperty(ref _patternName, value);
        }

        private double _minScore = 0.70;
        /// <summary>합격 점수. 낮추면 아무 데나 맞고, 높이면 조명이 조금만 바뀌어도 놓친다.</summary>
        public double MinScore
        {
            get => _minScore;
            set => SetProperty(ref _minScore, Math.Clamp(value, 0.1, 0.99));
        }

        private int _searchRadiusPx;
        /// <summary>기준 위치 주변만 볼 반경(px). 0 이면 화면 전체.</summary>
        public int SearchRadiusPx
        {
            get => _searchRadiusPx;
            set => SetProperty(ref _searchRadiusPx, Math.Max(0, value));
        }

        // ── 찾기 결과 ────────────────────────────────────────────────────
        private bool _hasResult, _resultFailed;
        public bool HasResult    { get => _hasResult;    private set => SetProperty(ref _hasResult, value); }
        public bool ResultFailed { get => _resultFailed; private set => SetProperty(ref _resultFailed, value); }

        private double _resX, _resY, _resW, _resH;
        public double ResultX { get => _resX; private set => SetProperty(ref _resX, value); }
        public double ResultY { get => _resY; private set => SetProperty(ref _resY, value); }
        public double ResultW { get => _resW; private set => SetProperty(ref _resW, value); }
        public double ResultH { get => _resH; private set => SetProperty(ref _resH, value); }

        private string _scoreText = "-", _posText = "-", _offsetText = "-", _resultLabel = "";
        public string ScoreText   { get => _scoreText;   private set => SetProperty(ref _scoreText, value); }
        public string PosText     { get => _posText;     private set => SetProperty(ref _posText, value); }

        /// <summary>기준 위치 대비 벗어난 양. 이 값이 나중에 스테이지 이동량이 된다.</summary>
        public string OffsetText  { get => _offsetText;  private set => SetProperty(ref _offsetText, value); }

        public string ResultLabel { get => _resultLabel; private set => SetProperty(ref _resultLabel, value); }

        // ── 동작 ─────────────────────────────────────────────────────────

        private void StartRegister()
        {
            if (IsRegistering) { IsRegistering = false; return; }

            if (_frame() == null)
            {
                Dialogs.Show("먼저 캡쳐하거나 이미지를 여세요.", "패턴 등록",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ClearResult();
            RoiW = RoiH = 0;
            IsRegistering = true;
        }

        /// <summary>지금 화면의 ROI 를 잘라 패턴으로 저장한다.</summary>
        private void Save()
        {
            var frame = _frame();
            if (frame == null || !HasRoi) return;

            var scene = ToGray(frame);
            if (scene == null)
            {
                Dialogs.Show("화면 이미지를 읽지 못했습니다.", "패턴 저장",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var (x, y, w, h) = RoiPixels(frame);
            var templ = scene.Crop(x, y, w, h);

            string name = PatternRepository.SanitizeName(PatternName);
            if (_repo.Load(name) != null &&
                Dialogs.Show($"[{name}] 패턴이 이미 있습니다. 덮어쓸까요?", "덮어쓰기",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var def = new PatternDefinition
            {
                Name           = name,
                ReferenceX     = x + (w - 1) / 2.0,
                ReferenceY     = y + (h - 1) / 2.0,
                SceneWidth     = scene.Width,
                SceneHeight    = scene.Height,
                MinScore       = MinScore,
                SearchRadiusPx = SearchRadiusPx,
                SavedAt        = DateTime.Now,
            };

            try
            {
                _repo.Save(def, templ);
            }
            catch (Exception ex)
            {
                _log($"[PATTERN] 저장 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show("저장하지 못했습니다.\n" + ex.Message, "패턴 저장",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _template   = templ;
            _definition = def;
            PatternName = name;
            IsRegistering = false;

            RefreshList();
            _selectedPattern = name;
            OnPropertyChanged(nameof(SelectedPattern));
            OnPropertyChanged(nameof(HasPattern));
            OnPropertyChanged(nameof(HintText));
            RefreshButtons();

            _log($"[PATTERN] 저장: {name} ({w}×{h}px, 기준 {def.ReferenceX:F0},{def.ReferenceY:F0})", LogLevel.Success);
        }

        private void Load(string? name)
        {
            if (string.IsNullOrEmpty(name)) return;

            var entry = _repo.Load(name!);
            if (entry == null)
            {
                _log($"[PATTERN] 읽기 실패: {name}", LogLevel.Warning);
                return;
            }

            _template   = entry.Template;
            _definition = entry.Definition;

            PatternName    = entry.Definition.Name;
            MinScore       = entry.Definition.MinScore;
            SearchRadiusPx = entry.Definition.SearchRadiusPx;
            Preview        = ToBitmap(entry.Template);

            ClearResult();
            RoiW = RoiH = 0;

            OnPropertyChanged(nameof(HasPattern));
            OnPropertyChanged(nameof(HintText));
            OnPropertyChanged(nameof(ReferenceText));
            RefreshButtons();
        }

        public string ReferenceText => _template == null
            ? "-"
            : $"{_definition.TemplateWidth}×{_definition.TemplateHeight}px · 기준 " +
              $"{_definition.ReferenceX:F0}, {_definition.ReferenceY:F0}";

        /// <summary>지금 화면에서 한 번 찾는다.</summary>
        private void Find()
        {
            var frame = _frame();
            if (frame == null || _template == null) return;

            var scene = ToGray(frame);
            if (scene == null) return;

            if (!_definition.MatchesScene(scene.Width, scene.Height))
            {
                Dialogs.Show(
                    $"등록할 때와 해상도가 다릅니다.\n등록 {_definition.SceneWidth}×{_definition.SceneHeight} " +
                    $"→ 지금 {scene.Width}×{scene.Height}\n\n기준 좌표를 믿을 수 없어 패턴을 다시 등록해야 합니다.",
                    "해상도 불일치", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var m = PatternMatcher.Find(scene, _template, new PatternSearchOptions
            {
                MinScore       = MinScore,
                SearchRadiusPx = SearchRadiusPx,
                ExpectedX      = _definition.ReferenceX,
                ExpectedY      = _definition.ReferenceY,
            });

            ShowResult(m, scene);
        }

        private void ShowResult(PatternMatch m, GrayImage scene)
        {
            HasResult    = true;
            ResultFailed = !m.Found;
            ScoreText    = m.Score.ToString("F3");

            double w = _template!.Width, h = _template.Height;
            ResultW = w / scene.Width;
            ResultH = h / scene.Height;
            ResultX = (m.CenterX - (w - 1) / 2.0) / scene.Width;
            ResultY = (m.CenterY - (h - 1) / 2.0) / scene.Height;

            if (!m.Found)
            {
                PosText     = "-";
                OffsetText  = "-";
                ResultLabel = $"실패 {m.Score:F3} (합격 {MinScore:F2})";
                _log($"[PATTERN] 못 찾음 — 최고 점수 {m.Score:F3}", LogLevel.Warning);
                return;
            }

            double dx = m.CenterX - _definition.ReferenceX;
            double dy = m.CenterY - _definition.ReferenceY;

            PosText     = $"{m.CenterX:F1}, {m.CenterY:F1} px";
            OffsetText  = $"ΔX {dx:+0.0;-0.0;0.0} · ΔY {dy:+0.0;-0.0;0.0} px";
            ResultLabel = $"{m.Score:F3}  ΔX {dx:+0.0;-0.0;0.0}  ΔY {dy:+0.0;-0.0;0.0}";

            _log($"[PATTERN] 찾음 {m.Score:F3} · ΔX {dx:F1} ΔY {dy:F1} px", LogLevel.Info);
        }

        private void Clear()
        {
            ClearResult();
            RoiW = RoiH = 0;
            IsRegistering = false;
        }

        private void ClearResult()
        {
            HasResult    = false;
            ResultFailed = false;
            ScoreText    = "-";
            PosText      = "-";
            OffsetText   = "-";
            ResultLabel  = "";
        }

        private void RefreshList()
        {
            Patterns.Clear();
            foreach (string n in _repo.List()) Patterns.Add(n);
        }

        // ── 좌표·이미지 변환 ─────────────────────────────────────────────

        private (int X, int Y, int W, int H) RoiPixels(BitmapSource frame)
        {
            int x = (int)Math.Round(RoiX * frame.PixelWidth);
            int y = (int)Math.Round(RoiY * frame.PixelHeight);
            int w = (int)Math.Round(RoiW * frame.PixelWidth);
            int h = (int)Math.Round(RoiH * frame.PixelHeight);
            return (x, y, Math.Max(1, w), Math.Max(1, h));
        }

        private void UpdatePreviewFromRoi()
        {
            var frame = _frame();
            if (frame == null || !HasRoi) return;

            var scene = ToGray(frame);
            if (scene == null) return;

            var (x, y, w, h) = RoiPixels(frame);
            Preview = ToBitmap(scene.Crop(x, y, w, h));
        }

        /// <summary>화면 프레임 → 8비트 그레이. 색 포맷이 무엇이든 Gray8 로 한 번에 변환한다.</summary>
        public static GrayImage? ToGray(BitmapSource src)
        {
            try
            {
                var gray = src.Format == PixelFormats.Gray8
                    ? src
                    : new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);

                int w = gray.PixelWidth, h = gray.PixelHeight;
                var buf = new byte[w * h];
                gray.CopyPixels(buf, w, 0);
                return new GrayImage(buf, w, h);
            }
            catch { return null; }
        }

        private static BitmapSource ToBitmap(GrayImage img)
        {
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96,
                                          PixelFormats.Gray8, null, img.Pixels, img.Width);
            bmp.Freeze();
            return bmp;
        }
    }
}
