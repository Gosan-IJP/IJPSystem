using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Microsoft.Win32;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
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

        // ── 파일 경로 표시 ─────────────────────────────────────────────────
        private string _loadedDir  = "";
        private string _loadedBase = "";
        public string LoadedBaseName
        {
            get => _loadedBase;
            private set => SetProperty(ref _loadedBase, value);
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

        // ── Spit 파라미터 ──────────────────────────────────────────────────
        private int _voltageOffset;
        public int VoltageOffset
        {
            get => _voltageOffset;
            set => SetProperty(ref _voltageOffset, value);
        }

        private int _jettingFrequency;
        public int JettingFrequency
        {
            get => _jettingFrequency;
            set => SetProperty(ref _jettingFrequency, value);
        }

        private double _jettingTime;
        public double JettingTime
        {
            get => _jettingTime;
            private set { if (SetProperty(ref _jettingTime, value)) OnPropertyChanged(nameof(JettingTimeText)); }
        }
        public string JettingTimeText => $"{JettingTime:F3} s";

        private int _jettingDropCount = 1000;
        public int JettingDropCount
        {
            get => _jettingDropCount;
            set { if (SetProperty(ref _jettingDropCount, value)) OnPropertyChanged(nameof(JettingDropText)); }
        }
        public string JettingDropText => $"{JettingDropCount} Drops";

        // ── 히터 ──────────────────────────────────────────────────────────
        private double _currentTemp = 0.4;
        public double CurrentTemp
        {
            get => _currentTemp;
            set => SetProperty(ref _currentTemp, value);
        }

        private double _targetTemp;
        public double TargetTemp
        {
            get => _targetTemp;
            set => SetProperty(ref _targetTemp, value);
        }

        private bool _isHeaterOn;
        public bool IsHeaterOn
        {
            get => _isHeaterOn;
            private set { if (SetProperty(ref _isHeaterOn, value)) OnPropertyChanged(nameof(HeaterButtonText)); }
        }
        public string HeaterButtonText => IsHeaterOn ? "Heater OFF" : "Heater Control";

        // ── Spit 상태 ─────────────────────────────────────────────────────
        private bool _isSpitting;
        public bool IsSpitting
        {
            get => _isSpitting;
            private set => SetProperty(ref _isSpitting, value);
        }

        // ── 커맨드 ────────────────────────────────────────────────────────
        public ICommand LoadCommand          { get; }
        public ICommand SaveCommand          { get; }
        public ICommand ApplySpitCommand     { get; }
        public ICommand StartSpitCommand     { get; }
        public ICommand AbortSpitCommand     { get; }
        public ICommand HeaterControlCommand { get; }

        // 차트 갱신 이벤트 (View에서 구독)
        public event Action? ChartDataChanged;

        // ─────────────────────────────────────────────────────────────────
        public WaveformViewModel(MainViewModel mainVM)
        {
            _mainVM   = mainVM;
            AllSeries = new List<WaveformSeries> { SeriesComA, SeriesComB, SeriesVst };

            LoadCommand          = new RelayCommand(_ => ExecuteLoad());
            SaveCommand          = new RelayCommand(_ => ExecuteSave(), _ => !string.IsNullOrEmpty(_loadedBase));
            ApplySpitCommand     = new RelayCommand(_ => ExecuteApplySpit());
            StartSpitCommand     = new RelayCommand(async _ => await ExecuteStartSpitAsync(), _ => !IsSpitting);
            AbortSpitCommand     = new RelayCommand(async _ => await ExecuteAbortSpitAsync(), _ => IsSpitting);
            HeaterControlCommand = new RelayCommand(_ => ExecuteToggleHeater());

            AutoLoadForActiveRecipe();
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
            string targetPath = @"C:\Waveforms";
            if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);

            var dlg = new OpenFileDialog
            {
                Title            = "웨이브폼 파일 선택",
                Filter           = "Waveform Files|*.ComA;*.ComB;*.Vst|All Files|*.*",
                InitialDirectory = targetPath,
            };
            if (dlg.ShowDialog() != true) return;

            string dir      = Path.GetDirectoryName(dlg.FileName) ?? "";
            string baseName = ExtractBaseName(Path.GetFileName(dlg.FileName));

            LoadWaveformFiles(dir, baseName, auto: false);
        }

        private void LoadWaveformFiles(string dir, string baseName, bool auto)
        {
            bool any = false;
            any |= TryLoad(dir, baseName, "ComA", SeriesComA);
            any |= TryLoad(dir, baseName, "ComB", SeriesComB);
            any |= TryLoad(dir, baseName, "Vst",  SeriesVst);

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
                RefreshChart();
                RaiseSaveCanExecute();
                string logMsg = auto
                    ? $"[WAVEFORM] 레시피 웨이브폼 자동 로드: {baseName}"
                    : $"[WAVEFORM] 로드: {baseName}";
                _mainVM.AddLog(logMsg, LogLevel.Success);
            }
        }

        private bool TryLoad(string dir, string baseName, string type, WaveformSeries target)
        {
            string path = Path.Combine(dir, $"{baseName}.{type}");
            if (!File.Exists(path)) return false;

            try
            {
                var file = WaveformParser.Parse(path);
                // repeats:1 — 파일 내 전체 펄스 시퀀스를 1회만 그린다(MetWaveEpson 과 동일).
                target.LoadFromFile(file, repeats: 1);
                _mainVM.AddLog($"[WAVEFORM] {type} 파싱 완료 ({file.Pulses.Count} pulse)", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[WAVEFORM] {type} 로드 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("LOG-WAVEFORM-LOAD-FAIL");
                return false;
            }
        }

        private static string ExtractBaseName(string filename)
        {
            foreach (var ext in new[] { ".ComA", ".ComB", ".Vst" })
                if (filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return filename[..^ext.Length];
            return Path.GetFileNameWithoutExtension(filename);
        }

        // ── 저장 ──────────────────────────────────────────────────────────
        private void ExecuteSave()
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

        // ── Apply Spit ────────────────────────────────────────────────────
        private void ExecuteApplySpit()
        {
            _mainVM.AddLog($"[WAVEFORM] Spit 파라미터 적용 (Offset={VoltageOffset}%)", LogLevel.Info);
            Dialogs.Show("Spit Parameter 적용되었습니다.");
        }

        // ── Start / Abort Spit ────────────────────────────────────────────
        // 예전에는 플래그만 뒤집고 로그만 남겼다 — 버튼은 눌리는데 헤드는 아무것도 하지 않았다.
        // 헤드는 장비에 하나뿐이라 공용 SpitService 를 거친다(드랍와쳐·패턴 인쇄·P&ID 와 같은 것).
        private async Task ExecuteStartSpitAsync()
        {
            var settings = new SpitSettings
            {
                Nozzles     = Nozzle.NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles,
                FrequencyHz = JettingFrequency > 0 ? JettingFrequency : DefaultSpitFrequencyHz,
            };
            if (!SpitService.TryStart(settings, out string? reason))
            {
                _mainVM.AddLog($"[WAVEFORM] Spit — {reason}", LogLevel.Warning);
                return;
            }

            IsSpitting = SpitService.IsSpitting;
            RaiseSpitCanExecute();
            _mainVM.AddLog($"[WAVEFORM] Spit 시작: {JettingDropCount} Drops @ {settings.FrequencyHz}Hz", LogLevel.Info);
        }

        private async Task ExecuteAbortSpitAsync()
        {
            bool idle = await SpitService.StopAsync();
            IsSpitting = SpitService.IsSpitting;
            RaiseSpitCanExecute();

            // 정지 미확인을 성공처럼 넘기면 헤드가 계속 토출 중일 수 있다.
            _mainVM.AddLog(idle ? "[WAVEFORM] Spit 중단"
                                : "[WAVEFORM] Spit 중단 후에도 정지가 확인되지 않았습니다. 헤드 상태를 확인하세요.",
                           idle ? LogLevel.Warning : LogLevel.Error);
        }

        /// <summary>Jetting Frequency 가 비어 있을 때 쓸 기본 스핏 주파수[Hz].</summary>
        private const double DefaultSpitFrequencyHz = 1000;

        private void RaiseSpitCanExecute()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ((RelayCommand)StartSpitCommand).RaiseCanExecuteChanged();
                ((RelayCommand)AbortSpitCommand).RaiseCanExecuteChanged();
            });
        }

        private void RaiseSaveCanExecute()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                ((RelayCommand)SaveCommand).RaiseCanExecuteChanged());
        }

        // ── 히터 ──────────────────────────────────────────────────────────
        private void ExecuteToggleHeater()
        {
            IsHeaterOn = !IsHeaterOn;
            _mainVM.AddLog($"[WAVEFORM] 히터 {(IsHeaterOn ? "ON" : "OFF")}", LogLevel.Info);
        }

        private void RefreshChart() => ChartDataChanged?.Invoke();
    }
}
