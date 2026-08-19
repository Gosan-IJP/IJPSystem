using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Print.Waveform;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class WaveformViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;

        // ── 시리즈 ────────────────────────────────────────────────────────
        public WaveformSeries SeriesComA { get; } = new()
        {
            Name   = "ComA",
            Stroke = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            DashArray = new DoubleCollection { 6, 3 },
        };
        public WaveformSeries SeriesComB { get; } = new()
        {
            Name   = "ComB",
            Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            DashArray = new DoubleCollection { 6, 3 },
        };
        public WaveformSeries SeriesVst { get; } = new()
        {
            Name   = "Vst",
            Stroke = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            DashArray = new DoubleCollection { 2, 4 },
            StrokeThickness = 1.2,
        };

        public IReadOnlyList<WaveformSeries> AllSeries { get; }

        // MetWaveEpson 처럼 채널을 위아래로 나눠 그린다 — 한 그래프에 겹치면
        // 어느 선이 어느 채널인지 색으로만 구분해야 해서 모양 비교가 어렵다.
        public IReadOnlyList<WaveformSeries> ComASeries { get; }
        public IReadOnlyList<WaveformSeries> ComBSeries { get; }

        /// <summary>두 그래프가 같은 시간 눈금을 쓰도록 하는 상한 [µs].</summary>
        public double ChartMaxTimeUs
        {
            get
            {
                double span = Math.Max(Editor.Document.ComA.TotalTimeUs, Editor.Document.ComB.TotalTimeUs);
                if (span <= 0) return 0;                       // 0 이면 차트가 알아서 정한다
                return Math.Ceiling(span / 10.0) * 10 + 2;
            }
        }

        // ── 파일 경로 표시 ─────────────────────────────────────────────────
        private string _loadedDir  = "";
        private string _loadedBase = "";
        public string LoadedBaseName
        {
            get => _loadedBase;
            private set { if (SetProperty(ref _loadedBase, value)) OnPropertyChanged(nameof(TitleText)); }
        }

        // ── 시리즈 가시성 ──────────────────────────────────────────────────
        public bool IsComAVisible
        {
            get => SeriesComA.IsVisible;
            set { SeriesComA.IsVisible = value; OnPropertyChanged(); RefreshChart(); }
        }
        public bool IsComBVisible
        {
            get => SeriesComB.IsVisible;
            set { SeriesComB.IsVisible = value; OnPropertyChanged(); RefreshChart(); }
        }
        public bool IsVstVisible
        {
            get => SeriesVst.IsVisible;
            set { SeriesVst.IsVisible = value; OnPropertyChanged(); RefreshChart(); }
        }

        // ── 편집 세션 ─────────────────────────────────────────────────────

        private bool _isDirty;

        /// <summary>저장하지 않은 변경이 있는가. 제목의 <c>*</c> 와 Save 활성 조건.</summary>
        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (!SetProperty(ref _isDirty, value)) return;
                OnPropertyChanged(nameof(TitleText));
                RaiseEditCanExecute();
            }
        }

        /// <summary>제목줄 — 어떤 파일을 보고 있고 저장했는지가 한 줄에 있어야 한다.</summary>
        public string TitleText =>
            $"File: {(string.IsNullOrEmpty(LoadedBaseName) ? "(새 파형)" : LoadedBaseName)}{(IsDirty ? " *" : "")}";

        /// <summary>
        /// Save — 고친 게 있거나 아직 한 번도 저장한 적 없을 때만. 방금 불러온 파형은
        /// 둘 다 아니므로 꺼진다(MetWaveEpson 과 같은 조건).
        /// </summary>
        public bool CanSaveFile => IsDirty || SelectedWaveform == null;

        // ── 파형 목록(파일 관리) ───────────────────────────────────────────
        // MetWaveEpson 의 Waveform 콤보 + Import / Remove / Rename / Make Default 와 같은 자리.
        private readonly WaveformRepository _repo = new();

        public ObservableCollection<WaveformEntry> WaveformList { get; } = new();

        // 목록을 새로 채울 때 선택이 바뀌면서 파일을 다시 읽는 것을 막는다.
        private bool _suppressSelectionLoad;

        private WaveformEntry? _selectedWaveform;
        public WaveformEntry? SelectedWaveform
        {
            get => _selectedWaveform;
            set
            {
                // 고친 것을 두고 다른 파형으로 넘어가면 그대로 사라진다 — 먼저 묻는다.
                if (!_suppressSelectionLoad && value != null && !ReferenceEquals(value, _selectedWaveform)
                    && !ConfirmDiscardIfDirty("다른 파형을 열면"))
                {
                    RestoreSelectionLater();
                    return;
                }

                if (!SetProperty(ref _selectedWaveform, value)) return;
                RaiseFileCanExecute();
                RaiseEditCanExecute();
                if (_suppressSelectionLoad || value == null) return;
                LoadWaveformFiles(_repo.RootDirectory, value.Name, auto: false);
            }
        }

        /// <summary>어느 폴더의 목록을 보고 있는지 — 설비마다 경로가 달라 화면에 보여야 한다.</summary>
        public string WaveformRoot => _repo.RootDirectory;

        /// <summary>
        /// PCC 가 읽는 PrintEngine 설정 파일. 우리가 만드는 파일이 아니라 위치만 보여 준다.
        /// </summary>
        public string MeteorConfigPath { get; } =
            PathUtils.GetConfigPath(AppConstants.MeteorConfigFile);

        public bool HasMeteorConfig => File.Exists(MeteorConfigPath);

        /// <summary>파일이 없으면 헤드가 조용히 가상으로 떨어진다 — 화면에서 구분이 되어야 한다.</summary>
        public string MeteorConfigText =>
            HasMeteorConfig ? MeteorConfigPath : MeteorConfigPath + "   (파일 없음)";

        // ── 커맨드 ────────────────────────────────────────────────────────
        /// <summary>
        /// 구동 파형 편집기 — Vst · 전압 조정 모드 · GL 배정표 · 세그먼트 그리드.
        /// 화면 그래프는 파일이 아니라 <b>이 편집기가 계산한 값</b>을 그린다.
        /// </summary>
        public Print.WaveformEditorViewModel Editor { get; } = new();

        public ICommand LoadCommand          { get; }
        public ICommand ApplyToRecipeCommand { get; }
        public ICommand ImportCommand        { get; }
        public ICommand RemoveCommand        { get; }
        public ICommand RenameCommand        { get; }
        public ICommand MakeDefaultCommand   { get; }

        // 편집 명령 — 파형 위 버튼 줄
        public ICommand NewCommand           { get; }
        public ICommand SaveCommand          { get; }
        public ICommand SaveAsCommand        { get; }
        public ICommand ScaleVoltageCommand  { get; }
        public ICommand InsertPulseCommand   { get; }
        public ICommand DeletePulseCommand   { get; }

        // 차트 갱신 이벤트 (View에서 구독)
        public event Action? ChartDataChanged;

        // ─────────────────────────────────────────────────────────────────
        public WaveformViewModel(MainViewModel mainVM)
        {
            _mainVM   = mainVM;
            AllSeries = new List<WaveformSeries> { SeriesComA, SeriesComB, SeriesVst };
            ComASeries = new List<WaveformSeries> { SeriesComA, SeriesVst };
            ComBSeries = new List<WaveformSeries> { SeriesComB, SeriesVst };

            LoadCommand          = new RelayCommand(_ => ExecuteLoad());
            ApplyToRecipeCommand = new RelayCommand(_ => ExecuteApplyToRecipe(), _ => !string.IsNullOrEmpty(_loadedBase));
            ImportCommand        = new RelayCommand(_ => ExecuteImport());
            RemoveCommand        = new RelayCommand(_ => ExecuteRemove(),      _ => SelectedWaveform != null);
            RenameCommand        = new RelayCommand(_ => ExecuteRename(),      _ => SelectedWaveform != null);
            MakeDefaultCommand   = new RelayCommand(_ => ExecuteMakeDefault(), _ => SelectedWaveform is { IsDefault: false });

            NewCommand           = new RelayCommand(_ => ExecuteNew());
            SaveCommand          = new RelayCommand(_ => ExecuteSaveFile(), _ => CanSaveFile);
            SaveAsCommand        = new RelayCommand(_ => ExecuteSaveAs());
            ScaleVoltageCommand  = new RelayCommand(_ => ExecuteScaleVoltage());
            InsertPulseCommand   = new RelayCommand(_ => ExecuteInsertPulse(), _ => Editor.CanInsertPulse);
            DeletePulseCommand   = new RelayCommand(_ => ExecuteDeletePulse(), _ => Editor.CanDeletePulse);

            // 편집값이 바뀌면 파일을 다시 읽지 않고 계산 결과로 그래프를 갱신한다 —
            // 화면 그래프와 헤드로 내려갈 값이 같은 계산에서 나와야 한다.
            Editor.Changed += RedrawFromEditor;

            // 사용자가 고친 것만 "저장 안 됨"으로 센다(로드는 제외).
            Editor.Edited += () => IsDirty = true;

            RefreshWaveformList();
            AutoLoadForActiveRecipe();
            SelectLoadedInList();
        }

        /// <summary>편집기 문서 → 차트 시리즈. Vst 는 문서 값으로 수평선을 긋는다.</summary>
        private void RedrawFromEditor()
        {
            var doc = Editor.Document;

            SeriesComA.Points = EpsonWaveformCalculator.BuildTrace(doc.ComA, doc.Vst)
                                    .Select(p => (p.TimeUs, p.Volts)).ToList();
            SeriesComB.Points = EpsonWaveformCalculator.BuildTrace(doc.ComB, doc.Vst)
                                    .Select(p => (p.TimeUs, p.Volts)).ToList();

            double span = Math.Max(doc.ComA.TotalTimeUs, doc.ComB.TotalTimeUs);
            if (span > 0) SeriesVst.SetFlat(doc.Vst, span);

            RefreshChart();
        }

        // ── 자동 로드 (화면 진입 시) ──────────────────────────────────────
        private void AutoLoadForActiveRecipe()
        {
            string recipeName = _mainVM.RecipeVM.ActiveRecipeName;
            if (string.IsNullOrEmpty(recipeName)) return;

            string? fullBasePath = _mainVM.RecipeVM.GetWaveformPath(recipeName);
            if (string.IsNullOrEmpty(fullBasePath)) return;

            string dir      = Path.GetDirectoryName(fullBasePath) ?? "";
            string baseName = Path.GetFileName(fullBasePath);
            if (!Directory.Exists(dir)) return;

            LoadWaveformFiles(dir, baseName, auto: true);
        }

        // ── 파일 로드 ─────────────────────────────────────────────────────
        private void ExecuteLoad()
        {
            var dlg = new OpenFileDialog
            {
                Title            = "웨이브폼 파일 선택",
                Filter           = "Waveform Files|*.ComA;*.ComB;*.Vst|All Files|*.*",
                InitialDirectory = _repo.RootDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string dir      = Path.GetDirectoryName(dlg.FileName) ?? "";
            string baseName = WaveformRepository.BaseNameOf(Path.GetFileName(dlg.FileName));

            LoadWaveformFiles(dir, baseName, auto: false);

            // 목록 폴더 밖의 파일을 열었으면 선택이 비어야 한다 — 목록과 화면이 어긋나면
            // Rename·Remove 가 다른 파형에 걸린다.
            SelectLoadedInList();
        }

        private void LoadWaveformFiles(string dir, string baseName, bool auto)
        {
            var fileA = TryLoad(dir, baseName, "ComA", SeriesComA);
            var fileB = TryLoad(dir, baseName, "ComB", SeriesComB);
            var fileV = TryLoad(dir, baseName, "Vst",  SeriesVst);
            bool any = fileA != null || fileB != null || fileV != null;

            // 편집기에 같은 내용을 올린다. 이후 그래프는 편집기 계산 결과가 그린다 —
            // 파일 파싱값과 편집 계산값이 갈라지면 화면과 실제 토출이 달라진다.
            if (fileA != null || fileB != null)
                Editor.Load(Print.WaveformDocumentBuilder.Build(fileA, fileB, baseName));

            if (SeriesVst.Points.Count == 0 && SeriesComA.Points.Count > 0)
            {
                double vstV = SeriesComA.Points[0].V;
                double maxT = SeriesComA.Points.Max(p => p.T);
                SeriesVst.SetFlat(vstV, maxT);
            }

            if (any)
            {
                _loadedDir     = dir;
                LoadedBaseName = baseName;
                IsDirty        = false;      // 방금 읽은 그대로다
                RefreshChart();
                RaiseSaveCanExecute();
                RaiseEditCanExecute();
                string logMsg = auto
                    ? $"[WAVEFORM] 레시피 웨이브폼 자동 로드: {baseName}"
                    : $"[WAVEFORM] 로드: {baseName}";
                _mainVM.AddLog(logMsg, LogLevel.Success);
            }
        }

        /// <summary>파싱한 파일을 돌려준다(편집기 구성에 쓴다). 없거나 실패하면 null.</summary>
        private Models.WaveformFile? TryLoad(string dir, string baseName, string type, WaveformSeries target)
        {
            string path = Path.Combine(dir, $"{baseName}.{type}");
            if (!File.Exists(path)) return null;

            try
            {
                var file = WaveformParser.Parse(path);
                // repeats:1 — 파일 내 전체 펄스 시퀀스를 1회만 그린다(MetWaveEpson 과 동일).
                target.LoadFromFile(file, repeats: 1);
                _mainVM.AddLog($"[WAVEFORM] {type} 파싱 완료 ({file.Pulses.Count} pulse)", LogLevel.Info);
                return file;
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[WAVEFORM] {type} 로드 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("LOG-WAVEFORM-LOAD-FAIL");
                return null;
            }
        }

        // ── 레시피에 적용 — 파형 파일을 쓰는 게 아니라, 적용 중인 레시피가
        // 이 파형을 쓰도록 경로만 기록한다.
        private void ExecuteApplyToRecipe()
        {
            if (string.IsNullOrEmpty(_loadedBase)) return;

            string recipeName = _mainVM.RecipeVM.ActiveRecipeName;
            if (string.IsNullOrEmpty(recipeName))
            {
                Dialogs.Show("적용 중인 레시피가 없습니다.\n레시피를 먼저 적용해 주세요.",
                    "저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fullBasePath = Path.Combine(_loadedDir, _loadedBase);
            _mainVM.RecipeVM.SetWaveformPath(recipeName, fullBasePath);
            Dialogs.Show($"[{recipeName}] 레시피에 웨이브폼이 저장되었습니다.", "저장 완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── 편집 명령 (New / Save / Save As / Insert · Delete Pulse) ──────

        private void ExecuteNew()
        {
            if (!ConfirmDiscardIfDirty("새 파형을 만들면")) return;

            Editor.Load(EpsonWaveformDocument.CreateDefault());

            _loadedDir     = _repo.RootDirectory;
            LoadedBaseName = "";
            ClearSelection();
            IsDirty = false;          // 아직 고친 것은 없지만 파일이 없으므로 Save 는 켜진다

            _mainVM.AddLog("[WAVEFORM] 새 파형", LogLevel.Info);
        }

        private void ExecuteSaveFile()
        {
            // 이름이 아직 없으면 Save As 로 넘긴다.
            if (SelectedWaveform == null || string.IsNullOrEmpty(LoadedBaseName)) { ExecuteSaveAs(); return; }
            SaveTo(LoadedBaseName);
        }

        private void ExecuteSaveAs()
        {
            string suggested = string.IsNullOrEmpty(LoadedBaseName) ? "New Waveform" : LoadedBaseName + "_2";
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "저장할 이름을 입력하세요.", "다른 이름으로 저장", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;

            name = WaveformRepository.SanitizeName(name);
            if (_repo.Find(name) != null)
            {
                var ask = Dialogs.Show($"[{name}] 파형이 이미 있습니다. 덮어쓸까요?", "덮어쓰기",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes) return;
            }

            SaveTo(name);
        }

        /// <summary>실제 저장. 파일은 PCC 가 그대로 읽으므로 쓰기 전에 한 번 더 확인한다.</summary>
        private bool SaveTo(string name)
        {
            var doc = Editor.Document;

            if (doc.ComA.Pulses.Count == 0)
            {
                Dialogs.Show("펄스가 없어 저장할 수 없습니다.", "저장 실패",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 배정이 빠진 GL 은 그 레벨로 토출이 아예 안 된다 — 저장 전에 한 번 묻는다.
            if (Editor.HasUnassignedGreyLevel)
            {
                var ask = Dialogs.Show($"{Editor.UnassignedGreyLevelsText}\n\n그대로 저장할까요?",
                    "배정되지 않은 그레이 레벨", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes) return false;
            }

            // 화면 배정표는 노즐 행 A/B 를 구분하지 않는다. 원래 다르던 파일이면 같아진다.
            if (doc.HadAsymmetricRowMasks)
            {
                var ask = Dialogs.Show(
                    "이 파일은 노즐 행 A/B 의 그레이 레벨 마스크가 서로 다릅니다.\n" +
                    "화면 배정표는 행을 구분하지 않으므로 저장하면 두 행이 같아집니다.\n\n계속할까요?",
                    "노즐 행 마스크", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes) return false;
            }

            string basePath = Path.Combine(_repo.RootDirectory, name);
            try
            {
                var written = EpsonWaveformWriter.Save(doc, basePath);

                doc.Name       = name;
                _loadedDir     = _repo.RootDirectory;
                LoadedBaseName = name;
                IsDirty        = false;
                doc.HadAsymmetricRowMasks = false;   // 방금 우리가 맞춰 썼다

                RefreshWaveformList(name);
                RaiseSaveCanExecute();

                _mainVM.AddLog($"[WAVEFORM] 저장: {string.Join(" · ", written.Select(Path.GetFileName))}",
                    LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[WAVEFORM] 저장 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show($"저장하지 못했습니다.\n{ex.Message}", "저장 실패",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Scale Voltage — Vst 기준 진폭을 배율만큼 키우거나 줄인다.
        /// 대기 전압(Vst)은 그대로 두므로 헤드가 늘 같은 전압에서 대기한다.
        /// </summary>
        private void ExecuteScaleVoltage()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Vst 기준 진폭을 몇 %로 할까요?\n(100 = 그대로, 90 = 10% 줄임)",
                "Scale Voltage", "100");
            if (string.IsNullOrWhiteSpace(input)) return;

            if (!double.TryParse(input.Trim().TrimEnd('%'), out double percent) || percent <= 0)
            {
                Dialogs.Show("0 보다 큰 숫자를 입력하세요.", "Scale Voltage",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Editor.ScaleVoltage(percent / 100.0)) return;
            _mainVM.AddLog($"[WAVEFORM] 진폭 {percent:F1}% 로 조정", LogLevel.Info);
        }

        private void ExecuteInsertPulse()
        {
            if (!Editor.InsertPulse()) return;
            RaiseEditCanExecute();
            _mainVM.AddLog($"[WAVEFORM] 펄스 삽입 — 총 {Editor.PulseCount}개", LogLevel.Info);
        }

        private void ExecuteDeletePulse()
        {
            int index = Editor.SelectedPulseIndex;
            if (!Editor.DeletePulse()) return;
            RaiseEditCanExecute();
            _mainVM.AddLog($"[WAVEFORM] 펄스 {index + 1} 삭제 — 총 {Editor.PulseCount}개", LogLevel.Warning);
        }

        /// <summary>저장하지 않은 변경이 있으면 묻는다. 버려도 좋다고 하면 true.</summary>
        private bool ConfirmDiscardIfDirty(string what)
        {
            if (!IsDirty) return true;

            var ask = Dialogs.Show($"저장하지 않은 변경이 있습니다.\n{what} 그 변경은 사라집니다.\n\n계속할까요?",
                "저장하지 않은 변경", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return ask == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 콤보가 이미 새 항목을 그려 버린 뒤라 그 자리에서 되돌리면 화면이 어긋난다 —
        /// 한 박자 뒤에 원래 항목으로 되돌린다.
        /// </summary>
        private void RestoreSelectionLater()
        {
            var keep = _selectedWaveform;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _suppressSelectionLoad = true;
                try { SetProperty(ref _selectedWaveform, keep, nameof(SelectedWaveform)); }
                finally { _suppressSelectionLoad = false; }
            }));
        }

        private void ClearSelection()
        {
            _suppressSelectionLoad = true;
            try { SelectedWaveform = null; }
            finally { _suppressSelectionLoad = false; }
        }

        private void RaiseEditCanExecute()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(CanSaveFile));
                ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
                ((RelayCommand)InsertPulseCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeletePulseCommand).RaiseCanExecuteChanged();
            });
        }

        // ── 파형 파일 관리 ────────────────────────────────────────────────

        /// <summary>목록을 다시 읽는다. 선택은 이름으로 되살린다(항목 객체가 새로 만들어지므로).</summary>
        private void RefreshWaveformList(string? selectName = null)
        {
            string keep = selectName ?? SelectedWaveform?.Name ?? LoadedBaseName;

            _suppressSelectionLoad = true;
            try
            {
                WaveformList.Clear();
                foreach (var e in _repo.List()) WaveformList.Add(e);
                SelectedWaveform = WaveformList.FirstOrDefault(
                    e => string.Equals(e.Name, keep, StringComparison.OrdinalIgnoreCase));
            }
            finally { _suppressSelectionLoad = false; }

            RaiseFileCanExecute();
        }

        /// <summary>로드된 파형이 이 폴더의 것이면 목록 선택도 맞춘다. 다른 폴더면 선택을 비운다.</summary>
        private void SelectLoadedInList()
        {
            if (string.IsNullOrEmpty(LoadedBaseName)) return;

            bool sameDir = !string.IsNullOrEmpty(_loadedDir) &&
                           string.Equals(Path.GetFullPath(_loadedDir).TrimEnd('\\'),
                                         Path.GetFullPath(_repo.RootDirectory).TrimEnd('\\'),
                                         StringComparison.OrdinalIgnoreCase);

            _suppressSelectionLoad = true;
            try
            {
                SelectedWaveform = sameDir
                    ? WaveformList.FirstOrDefault(e =>
                        string.Equals(e.Name, LoadedBaseName, StringComparison.OrdinalIgnoreCase))
                    : null;
            }
            finally { _suppressSelectionLoad = false; }
        }

        private void ExecuteImport()
        {
            var dlg = new OpenFileDialog
            {
                Title            = "가져올 웨이브폼 파일 선택",
                Filter           = "Waveform Files|*.ComA;*.ComB;*.Vst|All Files|*.*",
                InitialDirectory = Directory.Exists(_loadedDir) ? _loadedDir : _repo.RootDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var e = _repo.Import(dlg.FileName);
                RefreshWaveformList(e.Name);
                _mainVM.AddLog($"[WAVEFORM] 가져오기: {e.Name} → {_repo.RootDirectory}", LogLevel.Success);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[WAVEFORM] 가져오기 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show($"가져오기에 실패했습니다.\n{ex.Message}", "가져오기 실패",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteRemove()
        {
            var e = SelectedWaveform;
            if (e == null) return;

            // 적용 중인 레시피가 가리키는 파형을 지우면 그 레시피는 파형 없이 남는다.
            string extra = IsInActiveRecipe(e)
                ? $"\n\n※ 적용 중인 레시피 [{_mainVM.RecipeVM.ActiveRecipeName}] 가 이 파형을 쓰고 있습니다."
                : "";

            var ask = Dialogs.Show($"[{e.Name}] 파형 파일을 삭제할까요?{extra}", "파형 삭제",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;

            try
            {
                _repo.Remove(e);
                RefreshWaveformList("");
                _mainVM.AddLog($"[WAVEFORM] 삭제: {e.Name}", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[WAVEFORM] 삭제 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show($"삭제에 실패했습니다.\n{ex.Message}", "삭제 실패",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteRename()
        {
            var e = SelectedWaveform;
            if (e == null) return;

            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                $"[{e.Name}]의 새 이름을 입력하세요.", "파형 이름 변경", e.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == e.Name) return;

            bool wasInRecipe = IsInActiveRecipe(e);
            try
            {
                var renamed = _repo.Rename(e, newName);

                // 레시피는 파형을 경로로 붙잡고 있다 — 이름만 바꾸면 링크가 끊긴다.
                if (wasInRecipe)
                {
                    _mainVM.RecipeVM.SetWaveformPath(_mainVM.RecipeVM.ActiveRecipeName, renamed.BasePath);
                    _mainVM.AddLog($"[WAVEFORM] 레시피 [{_mainVM.RecipeVM.ActiveRecipeName}] 의 파형 경로도 함께 변경",
                        LogLevel.Info);
                }

                RefreshWaveformList(renamed.Name);
                if (string.Equals(LoadedBaseName, e.Name, StringComparison.OrdinalIgnoreCase))
                    LoadedBaseName = renamed.Name;

                _mainVM.AddLog($"[WAVEFORM] 이름 변경: {e.Name} → {renamed.Name}", LogLevel.Success);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[WAVEFORM] 이름 변경 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show($"이름을 바꾸지 못했습니다.\n{ex.Message}", "이름 변경 실패",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteMakeDefault()
        {
            var e = SelectedWaveform;
            if (e == null) return;

            _repo.MakeDefault(e);
            RefreshWaveformList(e.Name);
            _mainVM.AddLog($"[WAVEFORM] 기본 파형: {e.Name}", LogLevel.Info);
        }

        /// <summary>적용 중인 레시피가 기록해 둔 그 파형인가.</summary>
        private bool IsInActiveRecipe(WaveformEntry e)
        {
            string recipe = _mainVM.RecipeVM.ActiveRecipeName;
            if (string.IsNullOrEmpty(recipe)) return false;

            string? path = _mainVM.RecipeVM.GetWaveformPath(recipe);
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(e.BasePath),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void RaiseFileCanExecute()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ((RelayCommand)RemoveCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RenameCommand).RaiseCanExecuteChanged();
                ((RelayCommand)MakeDefaultCommand).RaiseCanExecuteChanged();
            });
        }

        private void RaiseSaveCanExecute()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                ((RelayCommand)ApplyToRecipeCommand).RaiseCanExecuteChanged());
        }

        private void RefreshChart() => ChartDataChanged?.Invoke();
    }
}
