using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Vision;
using System;
using System.Collections.ObjectModel;
using System.Linq;
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
            SetReferenceHereCommand = _setRef = new RelayCommand(_ => SetReferenceHere(), _ => HasPattern);

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
        private readonly RelayCommand _save, _find, _setRef;

        /// <summary>ROI/패턴이 바뀌었으니 [패턴저장]/[패턴 찾기] 를 다시 판정하게 한다.</summary>
        private void RefreshButtons()
        {
            _save.RaiseCanExecuteChanged();
            _find.RaiseCanExecuteChanged();
            _setRef.RaiseCanExecuteChanged();
        }

        public ICommand StartRegisterCommand { get; }
        public ICommand CancelRegisterCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand FindCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand LoadCommand { get; }

        /// <summary>지금 마크가 있는 자리를 이 패턴의 <b>기준</b>으로 다시 잡는다(템플릿은 그대로).</summary>
        public ICommand SetReferenceHereCommand { get; }

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

        public string RegisterButtonText => IsRegistering ? "Cancel" : "Register Pattern";

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
                if (f == null || RoiW <= 0 || RoiH <= 0) return "No region";
                return $"{RoiW * f.PixelWidth:F0} × {RoiH * f.PixelHeight:F0} px";
            }
        }

        public string HintText =>
            IsRegistering ? "Drag on the image to enclose the mark."
            : !HasPattern ? "No pattern registered."
            : "Find searches the current frame for this pattern.";

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

        private string _patternName = DefaultPatternName;

        /// <summary>
        /// 파일로 저장될 이름. <b>화면에는 없다</b> — 패턴을 한 개만 관리하기로 한 뒤로는
        /// 고를 것도 구분할 것도 없어서, 입력칸이 "뭔가 정해야 하나"라는 인상만 남겼다.
        ///
        /// <para>속성은 남겨 둔다. 옛 이름으로 저장된 패턴을 읽어 오면 그 이름을 그대로 이어
        /// 쓰므로(<see cref="Load"/>), 장비에 이미 있는 파일이 이름만 바뀌어 두 벌이 되지 않는다.</para>
        /// </summary>
        public string PatternName
        {
            get => _patternName;
            set => SetProperty(ref _patternName, value);
        }

        /// <summary>화면에서 이름을 받지 않으므로 새로 등록하는 패턴은 늘 이 이름이다.</summary>
        public const string DefaultPatternName = "GlassMark";

        private double _minScore = 0.70;
        /// <summary>
        /// 합격 점수. 낮추면 아무 데나 맞고, 높이면 조명이 조금만 바뀌어도 놓친다.
        ///
        /// <para><b>주인은 레시피다</b>(글라스 정보 → 정렬 합격 점수). 화면(GlassViewModel)이
        /// 활성 레시피 값을 여기로 밀어 넣고, 글라스 화면은 보여 주기만 한다. 여기서 고치게 두면
        /// 어느 기준으로 찾은 결과인지가 레시피에 남지 않는다.</para>
        /// </summary>
        public double MinScore
        {
            get => _minScore;
            set => SetProperty(ref _minScore, Math.Clamp(value, 0.50, 0.95));
        }

        private int _searchRadiusPx;
        /// <summary>기준 위치 주변만 볼 반경(px). 0 이면 화면 전체.</summary>
        public int SearchRadiusPx
        {
            get => _searchRadiusPx;
            set => SetProperty(ref _searchRadiusPx, Math.Max(0, value));
        }

        private string? _sceneWarning;
        /// <summary>
        /// 해상도가 조금 달라 오차가 섞였다는 안내. 문제 없으면 null.
        ///
        /// <para>막지 않고 알리기만 하는 이유: 매칭은 해상도와 무관하게 되고, 흔들리는 것은
        /// 기준 좌표뿐이다. 얼마나 흔들리는지를 숫자로 보여 주는 편이 낫다.</para>
        /// </summary>
        public string? SceneWarning
        {
            get => _sceneWarning;
            private set
            {
                if (SetProperty(ref _sceneWarning, value)) OnPropertyChanged(nameof(HasSceneWarning));
            }
        }

        public bool HasSceneWarning => !string.IsNullOrEmpty(SceneWarning);

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

            // 정렬 패턴은 <b>한 개만</b> 둔다. 여러 개가 있으면 시퀀스가 "어느 것으로 찾을지"를
            // 되물어야 하는데, 그 물음은 실장에서 곧바로 정렬 실패로 나온다
            // (2026-08-27 "정렬 패턴이 여러 개입니다 — 글라스 화면에서 쓸 패턴을 고르세요").
            // 그래서 이름이 같든 다르든 저장하면 앞의 것은 사라진다 — 지우기 전에 확인한다.
            var existing = _repo.List();
            if (existing.Count > 0)
            {
                bool sameName = existing.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                string ask = sameName
                    ? $"[{name}] 패턴을 덮어씁니다."
                    : $"등록된 패턴 [{string.Join(", ", existing)}] 을(를) 지우고 [{name}] 로 새로 등록합니다.";

                if (Dialogs.Show(ask + "\n계속할까요?", "패턴 저장",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }

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

                // 방금 저장한 것만 남긴다. 지우기는 저장이 성공한 <b>뒤에</b> 한다 —
                // 먼저 지우고 저장이 실패하면 쓸 수 있는 패턴이 하나도 없이 남는다.
                foreach (string old in existing)
                    if (!string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
                    {
                        _repo.Remove(old);
                        _log($"[PATTERN] 이전 패턴 삭제: {old}", LogLevel.Info);
                    }
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

            // 저장이 끝나면 드래그 영역을 지운다(2026-08-28). 파란 상자가 화면에 남고
            // [Save Pattern] 도 그대로 눌리면, 방금 저장이 된 것인지 아직 안 된 것인지
            // 화면만 보고는 알 수 없다 — 한 번 더 눌러 덮어쓰기 확인창을 보고서야 안다.
            // 여기서부터 "무엇이 등록돼 있나"의 근거는 오른쪽 미리보기 한 곳이다.
            Preview = ToBitmap(templ);
            RoiW = RoiH = 0;

            RefreshList();
            _selectedPattern = name;
            OnPropertyChanged(nameof(SelectedPattern));
            OnPropertyChanged(nameof(HasPattern));
            OnPropertyChanged(nameof(HintText));

            // 템플릿 크기·기준 좌표가 방금 바뀌었다. 안 알리면 머리말이 앞 패턴 값을
            // 그대로 달고 있어, 등록한 것과 다른 숫자를 보며 정렬을 맞추게 된다.
            OnPropertyChanged(nameof(ReferenceText));
            RefreshButtons();

            _log($"[PATTERN] 저장: {name} ({w}×{h}px, 기준 {def.ReferenceX:F0},{def.ReferenceY:F0})", LogLevel.Success);

            // 기준 자리가 곧 정렬이 <b>되돌아갈 목표</b>다. 가장자리에 등록하면 그쪽으로는
            // 고칠 여유가 거의 없다 — 화면 밖으로는 마크를 못 따라간다.
            // 예: 1280×1024 에서 (1082,326) 에 등록하면 오른쪽 여유가 198px(0.2mm)뿐이라,
            //     정렬이 시작하자마자 "0.5mm 벗어났습니다"로 걸린다(실장 2026-09-01).
            string? edge = EdgeWarning(def.ReferenceX, def.ReferenceY, scene.Width, scene.Height);

            Dialogs.Show($"패턴을 등록했습니다.\n\n" +
                         $"크기 : {w} × {h} px\n" +
                         $"기준 : {def.ReferenceX:F0}, {def.ReferenceY:F0} px" +
                         (edge == null ? "" : "\n\n⚠ " + edge),
                         "패턴 등록", MessageBoxButton.OK,
                         edge == null ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (edge != null) _log("[PATTERN] " + edge, LogLevel.Warning);
        }

        /// <summary>
        /// 지금 마크가 잡히는 자리를 <b>기준</b>으로 다시 잡는다. 템플릿(무늬)은 건드리지 않는다.
        ///
        /// <para><b>왜 따로 필요한가</b>: 기준은 "정렬이 마크를 되돌려 놓을 목표 픽셀"이다.
        /// 무늬가 잘 맞는데도(점수 0.78·0.99) 정렬이 "0.62mm 벗어났습니다"로 서는 일이 생기는데,
        /// 그건 마크가 화면 밖이어서가 아니라 <b>목표가 옛 자리</b>이기 때문이다
        /// (실장 2026-09-01: 마크는 463,338 에 잘 있는데 기준이 1082,326 이었다).</para>
        ///
        /// <para>그럴 때 [패턴 등록]을 다시 하면 ROI 를 손으로 다시 그려야 하고, 새로 자른 무늬가
        /// 전보다 나쁠 수도 있다. 여기서는 <b>좌표만</b> 고쳐 그 위험을 없앤다.</para>
        /// </summary>
        private void SetReferenceHere()
        {
            var frame = _frame();
            if (frame == null || _template == null) return;

            var scene = ToGray(frame);
            if (scene == null) return;

            // 기준을 옮기려는 참이니 옛 기준 둘레만 보면 안 된다 — 화면 전체에서 찾는다.
            var m = PatternMatcher.Find(scene, _template, new PatternSearchOptions { MinScore = MinScore });
            if (!m.Found)
            {
                Dialogs.Show($"지금 화면에서 마크를 찾지 못했습니다 — 최고 점수 {m.Score:F3} (합격 {MinScore:F2}).\n" +
                             "마크가 화면에 보이는지 확인하고 다시 누르세요.",
                             "기준 자리 갱신", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double oldX = _definition.ReferenceX, oldY = _definition.ReferenceY;
            string? edge = EdgeWarning(m.CenterX, m.CenterY, scene.Width, scene.Height);

            if (Dialogs.Show($"이 패턴의 기준 자리를 지금 마크 위치로 바꿉니다.\n\n" +
                             $"현재 기준 : {oldX:F0}, {oldY:F0} px\n" +
                             $"새 기준   : {m.CenterX:F0}, {m.CenterY:F0} px   (점수 {m.Score:F3})\n" +
                             $"이동량    : {m.CenterX - oldX:+0;-0;0}, {m.CenterY - oldY:+0;-0;0} px\n\n" +
                             "무늬(템플릿)는 그대로 두고 좌표만 바꿉니다." +
                             (edge == null ? "" : "\n\n⚠ " + edge),
                             "기준 자리 갱신", MessageBoxButton.YesNo,
                             edge == null ? MessageBoxImage.Question : MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            _definition.ReferenceX  = m.CenterX;
            _definition.ReferenceY  = m.CenterY;
            _definition.SceneWidth  = scene.Width;
            _definition.SceneHeight = scene.Height;
            _definition.SavedAt     = DateTime.Now;

            try { _repo.Save(_definition, _template); }
            catch (Exception ex)
            {
                _log($"[PATTERN] 기준 저장 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show("기준을 저장하지 못했습니다.\n\n" + ex.Message,
                             "기준 자리 갱신", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            OnPropertyChanged(nameof(ReferenceText));
            ShowResult(m, scene);

            _log($"[PATTERN] 기준 갱신: {_definition.Name} · {oldX:F0},{oldY:F0} → " +
                 $"{m.CenterX:F0},{m.CenterY:F0} px (점수 {m.Score:F3})", LogLevel.Success);
            if (edge != null) _log("[PATTERN] " + edge, LogLevel.Warning);
        }

        /// <summary>
        /// 기준 자리가 화면 가운데에서 얼마나 치우쳤는지 — 치우친 쪽으로는 정렬이 못 고친다.
        /// 가운데에서 반폭의 40% 를 넘으면 알린다(그 너머는 남는 여유가 반의 반도 안 된다).
        /// </summary>
        private static string? EdgeWarning(double refX, double refY, int sceneW, int sceneH)
        {
            if (sceneW <= 0 || sceneH <= 0) return null;

            double offX = Math.Abs(refX - sceneW / 2.0);
            double offY = Math.Abs(refY - sceneH / 2.0);
            if (offX <= sceneW * 0.20 && offY <= sceneH * 0.20) return null;

            // 남는 여유 = 가장 가까운 가장자리까지. 이 값이 정렬이 쓸 수 있는 전부다.
            double room = Math.Min(Math.Min(refX, sceneW - refX), Math.Min(refY, sceneH - refY));

            return $"기준이 화면 가운데({sceneW / 2}, {sceneH / 2})에서 많이 치우쳤습니다 — " +
                   $"가장 가까운 가장자리까지 {room:F0}px 뿐입니다. 정렬은 마크를 이 기준으로 되돌리는데, " +
                   "치우친 쪽으로는 고칠 여유가 없어 시작하자마자 \"너무 많이 벗어났습니다\"로 걸립니다. " +
                   "마크를 화면 한가운데 두고 다시 등록하세요.";
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
            SearchRadiusPx = entry.Definition.SearchRadiusPx;

            // MinScore 는 일부러 읽지 않는다 — 주인이 레시피이기 때문이다(2026-08-25).
            // 패턴 파일에서 되읽으면 패턴을 바꿀 때마다 합격 기준이 조용히 따라 바뀐다.
            // 파일에 쓰는 것은 "이 패턴을 등록할 때 어떤 기준이었나" 기록으로만 남긴다.
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
            : $"{_definition.TemplateWidth}×{_definition.TemplateHeight}px · ref " +
              $"{_definition.ReferenceX:F0}, {_definition.ReferenceY:F0}";

        /// <summary>지금 화면에서 한 번 찾는다.</summary>
        private void Find()
        {
            var frame = _frame();
            if (frame == null || _template == null) return;

            var scene = ToGray(frame);
            if (scene == null) return;

            // 해상도가 달라도 찾기는 된다 — 흔들리는 것은 기준 좌표뿐이다.
            // 조금 다르면 진행하고 오차만 알리고, 크게 다르면 막는다.
            var fit = _definition.CheckScene(scene.Width, scene.Height);
            if (!fit.CanFind)
            {
                Dialogs.Show(fit.Message, "해상도 불일치",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SceneWarning = fit.Fit == SceneFit.Close ? fit.Message : null;
            if (SceneWarning != null) _log("[PATTERN] " + SceneWarning, LogLevel.Warning);

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
            SceneWarning = null;
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
