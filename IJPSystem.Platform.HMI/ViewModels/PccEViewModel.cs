using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;   // MeteorHeadStatus — 폴링 결과 원본
using IJPSystem.Platform.Infrastructure.Print.Meteor;
using IJPSystem.Platform.Infrastructure.Print.Waveform;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>
    /// PCC-E(프린트 엔진) 화면 — <b>보기 전용</b>.
    ///
    /// <para>헤드가 안 뜰 때 볼 곳을 한 자리에 모은 화면이다. 실제로 문제가 되는 것은
    /// 늘 셋 중 하나였다: ① 엔진이 읽는 설정 파일이 우리가 생각한 파일이 아니거나,
    /// ② PCC 가 붙지 않았거나(DHCP 서버·NIC 이름), ③ 우리가 편집한 파형이 cfg 목록에
    /// 없어서 헤드가 고를 수 없거나.</para>
    ///
    /// <para>탭 구성은 Meteor Status Monitor 를 따른다 — 현장에서 그 도구를 보던 사람이
    /// 같은 자리에서 같은 것을 찾을 수 있어야 한다.</para>
    ///
    /// <para><b>고치는 것은 둘뿐이다</b>: 로그 상세 항목(cfg 의 LogCtrlBits 한 줄)과
    /// 로그 파일 비우기. cfg 의 나머지는 Meteor 설치와 현장 편집이 관리하므로 건드리지 않는다.
    /// 헤드를 움직이는 명령(Head Power·Force PD·ReInit·Spit)은 넣지 않았다 —
    /// 그런 동작은 이 장비의 시퀀스·안전 흐름을 거쳐야 한다.</para>
    /// </summary>
    public class PccEViewModel : ViewModelBase, IDisposable
    {
        private readonly MainViewModel _mainVM;
        private readonly WaveformRepository _repo = new();

        public PccEViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;

            RefreshCommand          = new RelayCommand(_ => Refresh());
            OpenConfigFolderCommand = new RelayCommand(_ => OpenConfigFolder(), _ => ConfigExists);
            StartEngineCommand      = _startEngine = new RelayCommand(_ => StartEngine(), _ => CanStartEngine);
            SelectTabCommand        = new RelayCommand(p => SelectedTab = p as string ?? "STATUS");

            ReloadLogCommand      = new RelayCommand(_ => ReloadLog());
            ClearLogCommand       = new RelayCommand(_ => ClearLog());
            PurgeLogCommand       = new RelayCommand(_ => PurgeLog(),        _ => HasLogFile);
            SaveLogModulesCommand = new RelayCommand(_ => SaveLogModules(),  _ => ConfigExists && IsLogModulesDirty);

            // 헤드 폴링은 MainViewModel 이 한다. 결과가 바뀌면 이 화면도 같이 갱신한다.
            _mainVM.PropertyChanged += OnMainChanged;

            Refresh();
        }

        /// <summary>화면을 떠날 때 폴링 구독을 끊는다 — 안 끊으면 지나간 화면이
        /// 살아남아 500ms 마다 표를 계속 다시 만든다.</summary>
        public void Dispose() => _mainVM.PropertyChanged -= OnMainChanged;

        private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.LastHeadStatus)) return;

            foreach (string p in new[] { nameof(HasHeadStatus), nameof(PccAttachText),
                                         nameof(PrinterStateText), nameof(HeadPowerText),
                                         nameof(HeadDetailText), nameof(CanStartEngine) })
                OnPropertyChanged(p);

            // 엔진이 붙으면 버튼이 스스로 꺼져야 한다 — 붙은 뒤에도 눌리면 다시 띄우려 든다.
            _startEngine.RaiseCanExecuteChanged();

            RefreshStatusTables();
        }

        /// <summary>상태바와 같은 헤드 연결 정보를 쓰기 위해 그대로 노출한다.</summary>
        public MainViewModel Main => _mainVM;

        public ICommand RefreshCommand { get; }
        public ICommand OpenConfigFolderCommand { get; }

        // ── 엔진 시작 ────────────────────────────────────────────────────
        //
        // 상태 모니터는 <b>이미 도는</b> 엔진에 붙기만 한다(PiOpenPrinter). 엔진 자체를 띄우는
        // 곳은 스핏 경로 하나뿐이라, 아무도 스핏을 누르지 않으면 화면은 영영 "엔진 미실행"이다.
        // 그때마다 Meteor 도구를 따로 띄우게 하는 대신 여기서 시작할 수 있게 한다.
        //
        // 노즐을 쏘는 명령이 아니다 — 엔진 프로세스를 올리고 cfg 를 읽힐 뿐이다.
        private readonly RelayCommand _startEngine;
        public ICommand StartEngineCommand { get; }

        /// <summary>이미 붙어 있으면 누를 필요가 없다 — 실물 헤드이고 아직 못 읽을 때만 켠다.</summary>
        public bool CanStartEngine =>
            _mainVM.HeadSource is Infrastructure.Devices.DropWatcher.MeteorStatusMonitor
            && ConfigExists
            && _mainVM.LastHeadStatus?.Reachable != true;

        private string _startEngineResult = "";
        /// <summary>직전 [엔진 시작] 결과. 로그에도 남지만 화면에서 바로 보여야 한다.</summary>
        public string StartEngineResult
        {
            get => _startEngineResult;
            private set => SetProperty(ref _startEngineResult, value);
        }

        private void StartEngine()
        {
            var src = _mainVM.HeadSource;
            if (src == null) { StartEngineResult = "헤드가 구성되지 않았습니다(DriverMode.Head)."; return; }

            var (ok, msg) = src.StartEngine(ConfigPath);
            StartEngineResult = msg;
            _mainVM.AddLog("[HEAD] 엔진 시작 — " + msg, ok ? LogLevel.Success : LogLevel.Warning);

            // 결과는 다음 폴링에서 올라온다(OnMainChanged) — 여기서 다시 열지 않는다.
            _startEngine.RaiseCanExecuteChanged();
        }

        // ── 설정 파일 ────────────────────────────────────────────────────
        private MeteorConfigFile _cfg = MeteorConfigFile.Load("");

        public string ConfigPath   => _cfg.FilePath;
        public bool   ConfigExists => _cfg.Exists && string.IsNullOrEmpty(_cfg.LoadError);

        /// <summary>파일이 없으면 헤드가 조용히 뜨지 않는다 — 화면에서 구분이 되어야 한다.</summary>
        public string ConfigStateText =>
            !_cfg.Exists                            ? "파일 없음 — AppConfig.json 의 MeteorConfigPath 를 확인하세요."
            : !string.IsNullOrEmpty(_cfg.LoadError) ? "읽기 실패 — " + _cfg.LoadError
            : "";

        public bool HasConfigProblem => !ConfigExists;

        public string RawText => _cfg.RawText;

        /// <summary>설정이 어디서 왔는지. 비어 있으면 기본 파일명을 쓴 것이다.</summary>
        public string ConfiguredValueText
        {
            get
            {
                string s = AppSettingsService.Current?.MeteorConfigPath ?? "";
                return string.IsNullOrWhiteSpace(s)
                    ? $"AppConfig.json 에 MeteorConfigPath 가 없음 → 기본값 {AppConstants.MeteorConfigFile}"
                    : $"AppConfig.json · MeteorConfigPath = \"{s}\"";
            }
        }

        // ── 요약값 ───────────────────────────────────────────────────────
        public string PccTypeText  => Dash(_cfg.PccType);
        public string HeadTypeText => Dash(_cfg.HeadType);

        public string GreyLevelText => _cfg.BitsPerPixel > 0
            ? $"{_cfg.GreyLevels}단계  (BitsPerPixel {_cfg.BitsPerPixel})"
            : "-";

        public string XdpiText => _cfg.Xdpi > 0 ? _cfg.Xdpi + " dpi" : "-";

        public string PlaneText => _cfg.PlanesPerHdc > 0
            ? $"HDC 당 {_cfg.PlanesPerHdc}면 · Plane1 = {Dash(_cfg.Plane1)}"
            : "-";

        /// <summary>인코더 배수. 600dpi + 1µm 인코더면 3/127 이 정상값이다.</summary>
        public string EncoderText => _cfg.Exists
            ? (_cfg.PrintClock == 0
                ? $"외부 인코더 · {_cfg.EncoderMultiplier}/{_cfg.EncoderDivider}" +
                  (_cfg.EncoderQuadrature ? " · 쿼드러처" : "")
                : $"내부 클럭 {_cfg.PrintClock} Hz")
            : "-";

        /// <summary>이 이름과 같은 네트워크 어댑터가 있어야 PCC 를 찾는다.</summary>
        public string AdapterText => Dash(_cfg.EthernetAdapter);

        public string DriverModeText
        {
            get
            {
                string mode = AppSettingsService.Current?.DriverMode?.Head ?? "None";
                return string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase)
                    ? "None — 헤드 미탑재로 두었습니다(연결 시도 안 함)"
                    : mode;
            }
        }

        // ── cfg 에 등록된 파형 ───────────────────────────────────────────
        public ObservableCollection<MeteorWaveformRef> Waveforms { get; } = new();

        public bool HasWaveforms => Waveforms.Count > 0;

        public string WaveformCountText => _cfg.Exists
            ? $"{Waveforms.Count}개 등록 · 기본 {_cfg.WaveformFileIdx}번"
            : "-";

        /// <summary>우리가 편집·보관하는 파형 폴더. cfg 목록과는 다른 자리다.</summary>
        public string OurWaveformRoot => _repo.RootDirectory;

        private int _ourWaveformCount;
        public int OurWaveformCount
        {
            get => _ourWaveformCount;
            private set => SetProperty(ref _ourWaveformCount, value);
        }

        /// <summary>
        /// 편집한 파형이 헤드까지 못 가는 상황을 화면에 드러낸다.
        /// cfg 의 Waveform 목록에 없는 파형은 헤드가 고를 방법이 없다.
        /// </summary>
        public bool HasWaveformGap => _cfg.Exists && OurWaveformCount > 0 && UnlistedCount > 0;

        private int _unlistedCount;
        public int UnlistedCount
        {
            get => _unlistedCount;
            private set => SetProperty(ref _unlistedCount, value);
        }

        public string WaveformGapText =>
            $"파형 폴더의 {OurWaveformCount}개 중 {UnlistedCount}개가 cfg 목록에 없습니다. " +
            "cfg 의 Waveform 목록에 없는 파형은 헤드가 고를 수 없습니다 — " +
            "[레시피에 적용]까지는 되지만 실제 토출에는 쓰이지 않습니다.";

        // ── 동작 ─────────────────────────────────────────────────────────
        public void Refresh()
        {
            string path = PathUtils.ResolveConfigPath(
                AppSettingsService.Current?.MeteorConfigPath, AppConstants.MeteorConfigFile);

            _cfg = MeteorConfigFile.Load(path);

            Waveforms.Clear();
            foreach (var w in _cfg.Waveforms) Waveforms.Add(w);

            // 우리 폴더의 파형이 cfg 목록에 있는지 — 이름(확장자 제외)으로 맞춰 본다.
            var listed = new HashSet<string>(
                Waveforms.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<WaveformEntry> ours;
            try { ours = _repo.List(); } catch { ours = Array.Empty<WaveformEntry>(); }

            OurWaveformCount = ours.Count;
            UnlistedCount    = ours.Count(e => !listed.Contains(e.Name));

            foreach (string p in new[]
            {
                nameof(ConfigPath), nameof(ConfigExists), nameof(ConfigStateText), nameof(HasConfigProblem),
                nameof(RawText), nameof(ConfiguredValueText), nameof(PccTypeText), nameof(HeadTypeText),
                nameof(GreyLevelText), nameof(XdpiText), nameof(PlaneText), nameof(EncoderText),
                nameof(AdapterText), nameof(DriverModeText), nameof(HasWaveforms), nameof(WaveformCountText),
                nameof(OurWaveformRoot), nameof(HasWaveformGap), nameof(WaveformGapText),
                nameof(LogPath), nameof(HasLogFile), nameof(LogStateText),
            })
                OnPropertyChanged(p);

            LoadLogModules();
            if (IsLogTab) ReloadLog();
        }

        private void OpenConfigFolder()
        {
            string? dir = Path.GetDirectoryName(ConfigPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[PCCE] 폴더 열기 실패: {ex.Message}", LogLevel.Warning);
            }
        }

        private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

        // ── 탭 ───────────────────────────────────────────────────────────
        // Meteor Status Monitor 의 탭 구성을 따른다 — 현장에서 그 도구를 보던 사람이
        // 같은 자리에서 같은 것을 찾을 수 있어야 한다.

        private string _selectedTab = "STATUS";
        public string SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (!SetProperty(ref _selectedTab, value)) return;
                foreach (string p in new[] { nameof(IsStatusTab), nameof(IsLogTab),
                                             nameof(IsSetupTab), nameof(IsConfigTab) })
                    OnPropertyChanged(p);

                if (value == "LOG") ReloadLog();
            }
        }

        public bool IsStatusTab => SelectedTab == "STATUS";
        public bool IsLogTab    => SelectedTab == "LOG";
        public bool IsSetupTab  => SelectedTab == "SETUP";
        public bool IsConfigTab => SelectedTab == "CONFIG";

        public ICommand SelectTabCommand { get; }

        // ── STATUS ───────────────────────────────────────────────────────
        // 폴링은 MainViewModel 한 곳에서만 한다. 화면이 또 PiOpenPrinter 를 부르면
        // 프린터를 뺏게 된다 — Meteor 는 한 프로세스만 소유할 수 있다.

        private MeteorHeadStatus? Head => _mainVM.LastHeadStatus;

        public bool HasHeadStatus => Head is { Reachable: true };

        public string PccAttachText => Head is { Reachable: true } h
            ? $"{h.PccsAttached} / {h.PccsRequired}"
            : "-";

        public string PrinterStateText => Head is { Reachable: true } h && h.PrinterState.Length > 0
            ? h.PrinterState : "-";

        public string HeadPowerText => Head is { Reachable: true } h && h.HeadPower.Length > 0
            ? h.HeadPower : "-";

        /// <summary>폴링이 아예 못 붙은 사유(엔진 미실행·점유중·DLL 없음).</summary>
        public string HeadDetailText => Head?.Detail ?? "아직 조회하지 않았습니다.";

        // ── 가상 모드 ────────────────────────────────────────────────────
        // 값이 실물이 아니라는 것이 화면에 계속 보여야 한다. 한 번 뜨고 마는 배너로는
        // 스크린샷만 보고 실물로 오해한다.

        /// <summary>지금 보이는 값이 만들어 낸 값인가.</summary>
        public bool IsSimulated => Head?.IsSimulated == true;

        /// <summary>고를 수 있는 상황. 실물이면 비어 있다.</summary>
        public IReadOnlyList<string> Scenarios => _mainVM.HeadSource?.Scenarios ?? Array.Empty<string>();

        public bool HasScenarios => Scenarios.Count > 0;

        /// <summary>
        /// 화면에서 고르는 상황. 정상만 보면 폴트 표시나 "주소 없음" 안내가
        /// 맞게 뜨는지 영영 확인되지 않는다.
        /// </summary>
        public string SelectedScenario
        {
            get => _mainVM.HeadSource?.Scenario ?? "";
            set
            {
                var src = _mainVM.HeadSource;
                if (src == null || string.IsNullOrEmpty(value) || src.Scenario == value) return;

                src.Scenario = value;
                OnPropertyChanged();
                _mainVM.AddLog($"[PCCE] 가상 상황: {value}", LogLevel.Info);
                // 다음 폴링(500ms)까지 기다리지 않고 바로 바뀐 것이 보이게 한다.
                RefreshStatusTables();
            }
        }

        // 표 세 개(Printer / PCC / HDC·Head)와 선택 콤보. Meteor Status Monitor 의
        // Status 탭과 같은 배치다 — 같은 화면을 보던 사람이 같은 자리에서 찾을 수 있어야 한다.

        public ObservableCollection<StatusRow> PrinterRows  { get; } = new();
        public ObservableCollection<StatusRow> PccRows      { get; } = new();
        public ObservableCollection<StatusRow> HdcRows      { get; } = new();
        public ObservableCollection<StatusRow> DatapathRows { get; } = new();

        public ObservableCollection<int> PccNumbers  { get; } = new();
        public ObservableCollection<int> HeadNumbers { get; } = new();

        private int _selectedPccNumber = 1;
        /// <summary>PCC 표에 띄울 대상. Monitor 의 PCC 스핀 컨트롤에 해당한다.</summary>
        public int SelectedPccNumber
        {
            get => _selectedPccNumber;
            set { if (SetProperty(ref _selectedPccNumber, value)) RefreshSelectedTables(); }
        }

        private int _selectedHeadNumber = 1;
        /// <summary>HDC/Head 표에 띄울 대상.</summary>
        public int SelectedHeadNumber
        {
            get => _selectedHeadNumber;
            set { if (SetProperty(ref _selectedHeadNumber, value)) RefreshSelectedTables(); }
        }

        private MeteorPccStatus? SelectedPcc =>
            Head?.Pccs.FirstOrDefault(p => p.Number == SelectedPccNumber);

        private MeteorHdcStatus? SelectedHdc =>
            Head?.Hdcs.FirstOrDefault(x => x.PccNumber  == SelectedPccNumber
                                        && x.HeadNumber == SelectedHeadNumber);

        /// <summary>선택한 PCC 의 폴트를 풀어 쓴 것. 숫자만 띄우면 아무도 못 읽는다.</summary>
        public ObservableCollection<string> SelectedPccFaults { get; } = new();

        public bool HasSelectedPccFault => SelectedPccFaults.Count > 0;

        /// <summary>self-clearing 비트라 폴링할 때마다 봐야 놓치지 않는다.</summary>
        public bool SelectedPccDataTransferError => SelectedPcc?.DataTransferError == true;

        /// <summary>
        /// 헤드 전원 표시등. 회색=꺼짐 / 보라=인가 중 / 초록=정상 / 빨강=폴트.
        /// 화면에서 색만 보고 판단하게 되므로 판정 규칙을 여기 한 곳에 둔다.
        /// </summary>
        public string HeadPowerLamp
        {
            get
            {
                var h = Head;
                if (h is not { Reachable: true }) return "Off";

                if (h.Pccs.Any(p => p.HasFault))                      return "Fault";
                if (h.Pccs.Any(p => p.HeadPowerInProgress))           return "Init";
                if (h.Hdcs.Any(x => x.State.Contains("FAULT",
                        StringComparison.OrdinalIgnoreCase)))         return "Fault";

                bool on = !h.HeadPower.Contains("OFF", StringComparison.OrdinalIgnoreCase)
                          && h.HeadPower.Length > 0;
                return on ? "On" : "Off";
            }
        }

        public string HeadPowerLampText => HeadPowerLamp switch
        {
            "Fault" => "폴트",
            "Init"  => "인가 중",
            "On"    => "정상",
            _       => "꺼짐",
        };

        /// <summary>PCC 까지 읽혔나. 못 읽었으면 표가 비고, 그 이유를 따로 띄운다.</summary>
        public bool HasPccDetail => Head?.Pccs.Count > 0;
        public bool HasHdcDetail => Head?.Hdcs.Count > 0;

        private void RefreshStatusTables()
        {
            var h = Head;

            PrinterRows.Clear();
            if (h is { Reachable: true })
            {
                PrinterRows.Add(new StatusRow("PCCs attached",   $"{h.PccsAttached} / {h.PccsRequired}"));
                PrinterRows.Add(new StatusRow("PCCs present",    string.IsNullOrEmpty(h.PccsPresent) ? "없음" : h.PccsPresent));
                PrinterRows.Add(new StatusRow("Printer State",   h.PrinterState));
                PrinterRows.Add(new StatusRow("Head Power",      h.HeadPower));
                PrinterRows.Add(new StatusRow("Print Frequency", h.PrintSpeed.ToString()));
                PrinterRows.Add(new StatusRow("PD Count",        h.PdCount.ToString()));
                PrinterRows.Add(new StatusRow("Print Count",     h.PrintCount.ToString()));
                PrinterRows.Add(new StatusRow("PD Count 2",      h.PdCount2.ToString()));
                PrinterRows.Add(new StatusRow("Print Count 2",   h.PrintCount2.ToString()));
            }

            DatapathRows.Clear();
            if (h is { Reachable: true })
            {
                // Monitor 의 cmds/dwords/nproc 은 다른 호출에서 나온다. 여기서는 우리가
                // 실제로 읽을 수 있는 값만 — 문서가 어디까지 갔는지로 같은 판단을 할 수 있다.
                DatapathRows.Add(new StatusRow("Preload 보낸 문서", h.PreloadDocsSent.ToString()));
                DatapathRows.Add(new StatusRow("FIFO 보낸 문서",    h.FifoDocsSent.ToString()));
                DatapathRows.Add(new StatusRow("Lane1 대기",       h.DocsQueuedLane1.ToString()));
                DatapathRows.Add(new StatusRow("Lane2 대기",       h.DocsQueuedLane2.ToString()));
                DatapathRows.Add(new StatusRow("문서 수",           h.DocCount.ToString()));
                DatapathRows.Add(new StatusRow("잡 수",             h.JobCount.ToString()));
            }

            // 선택 목록 — 실제로 읽힌 것만 고를 수 있게 한다.
            SyncNumbers(PccNumbers,  h?.Pccs.Select(p => p.Number).ToList());
            SyncNumbers(HeadNumbers, h?.Hdcs.Where(x => x.PccNumber == SelectedPccNumber)
                                            .Select(x => x.HeadNumber).ToList());

            RefreshSelectedTables();

            foreach (string p in new[] { nameof(HasPccDetail), nameof(HasHdcDetail),
                                         nameof(HeadPowerLamp), nameof(HeadPowerLampText),
                                         nameof(IsSimulated), nameof(HasScenarios),
                                         nameof(SelectedScenario) })
                OnPropertyChanged(p);
        }

        private void RefreshSelectedTables()
        {
            var p = SelectedPcc;
            PccRows.Clear();
            if (p != null)
            {
                PccRows.Add(new StatusRow("Status Bits",   PccFaultDecoder.FormatStatusBits(p.StatusBits)));
                PccRows.Add(new StatusRow("Status Bits2",  PccFaultDecoder.FormatStatusBits(p.StatusBits2)));
                PccRows.Add(new StatusRow("Fault Register", $"0x{p.FaultRegister:X8}"));
                PccRows.Add(new StatusRow("PD Count",       p.PdCount.ToString()));
                PccRows.Add(new StatusRow("Print Count",    p.PrintCount.ToString()));
                PccRows.Add(new StatusRow("Encoder",        p.EncoderCount.ToString()));
                PccRows.Add(new StatusRow("Abs X-count",    p.AbsXCount.ToString()));
                PccRows.Add(new StatusRow("IP",             string.IsNullOrEmpty(p.IpAddress) ? "주소 없음" : p.IpAddress));
                PccRows.Add(new StatusRow("Firmware",       $"0x{p.FwVersion:X8}"));
                PccRows.Add(new StatusRow("FPGA",           $"0x{p.FpgaVersion:X8}"));
                PccRows.Add(new StatusRow("Max HDCs",       p.MaxHdcs.ToString()));
            }

            SelectedPccFaults.Clear();
            foreach (var f in PccFaultDecoder.Decode(p?.FaultRegister ?? 0))
                SelectedPccFaults.Add($"{f.Title} — {f.Description}");

            var d = SelectedHdc;
            HdcRows.Clear();
            if (d != null)
            {
                HdcRows.Add(new StatusRow("Head State",    d.State));
                HdcRows.Add(new StatusRow("Status Bits",   PccFaultDecoder.FormatStatusBits(d.StatusBits)));
                HdcRows.Add(new StatusRow("Status Bits2",  PccFaultDecoder.FormatStatusBits(d.StatusBits2)));
                HdcRows.Add(new StatusRow("Preload 사용",  d.PreloadDataUsedPercent + " %"));
                HdcRows.Add(new StatusRow("FIFO 사용",     d.FifoDataUsedPercent + " %"));
                HdcRows.Add(new StatusRow("DDram dwords A", d.DdramDwordsA.ToString()));
                HdcRows.Add(new StatusRow("DDram dwords B", d.DdramDwordsB.ToString()));
                HdcRows.Add(new StatusRow("Head dwords A",  d.HeadDwordsA.ToString()));
                HdcRows.Add(new StatusRow("Head dwords B",  d.HeadDwordsB.ToString()));
                HdcRows.Add(new StatusRow("Images queued",  d.ImagesQueuedA.ToString()));
                HdcRows.Add(new StatusRow("Docs printed",   d.DocsPrintedA.ToString()));
            }

            foreach (string s in new[] { nameof(HasSelectedPccFault),
                                         nameof(SelectedPccDataTransferError) })
                OnPropertyChanged(s);
        }

        /// <summary>선택 목록을 새 목록으로 맞춘다. 고른 번호가 사라졌으면 첫 번호로 되돌린다.</summary>
        private void SyncNumbers(ObservableCollection<int> target, List<int>? source)
        {
            var want = source ?? new List<int>();
            if (want.SequenceEqual(target)) return;

            target.Clear();
            foreach (int v in want) target.Add(v);

            if (want.Count == 0) return;

            if (ReferenceEquals(target, PccNumbers) && !want.Contains(_selectedPccNumber))
            {
                _selectedPccNumber = want[0];
                OnPropertyChanged(nameof(SelectedPccNumber));
            }
            else if (ReferenceEquals(target, HeadNumbers) && !want.Contains(_selectedHeadNumber))
            {
                _selectedHeadNumber = want[0];
                OnPropertyChanged(nameof(SelectedHeadNumber));
            }
        }

        // ── LOG (Debug / Errors) ─────────────────────────────────────────

        private readonly EngineLogView _log = new();

        public ObservableCollection<EngineLogEntry> LogLines { get; } = new();

        private bool _errorsOnly;
        /// <summary>Errors 보기. 따로 모으는 게 아니라 같은 로그를 거른 것이다.</summary>
        public bool ErrorsOnly
        {
            get => _errorsOnly;
            set { if (SetProperty(ref _errorsOnly, value)) FillLogLines(); }
        }

        public string LogPath => _cfg.Exists ? _cfg.LogFilePath : "";

        public bool HasLogFile => !string.IsNullOrEmpty(LogPath) && File.Exists(LogPath);

        public string LogStateText
        {
            get
            {
                if (!_cfg.Exists)            return "설정 파일을 먼저 찾아야 합니다.";
                if (!_cfg.LogToDisk)         return "cfg 의 [Test] LogToDisk = 0 — 엔진이 로그 파일을 쓰지 않습니다.";
                if (string.IsNullOrEmpty(LogPath)) return "cfg 에 [Test] LogFile 이 없습니다.";
                if (!File.Exists(LogPath))   return "로그 파일이 아직 없습니다 — 엔진이 한 번도 안 떴을 수 있습니다.";
                return $"{LogLines.Count}줄" + (_log.HasErrors ? "  ·  오류 있음" : "");
            }
        }

        public ICommand ReloadLogCommand { get; }
        public ICommand ClearLogCommand  { get; }
        public ICommand PurgeLogCommand  { get; }

        /// <summary>파일 끝부분을 다시 읽는다. 엔진이 열어 둔 채로도 읽힌다.</summary>
        public void ReloadLog()
        {
            _log.LoadTail(LogPath);
            FillLogLines();
        }

        private void FillLogLines()
        {
            LogLines.Clear();
            foreach (var e in ErrorsOnly ? _log.Errors() : _log.All()) LogLines.Add(e);

            OnPropertyChanged(nameof(LogStateText));
            OnPropertyChanged(nameof(HasLogFile));
            OnPropertyChanged(nameof(LogPath));
        }

        /// <summary>화면 버퍼만 비운다 — 파일은 그대로다.</summary>
        private void ClearLog()
        {
            _log.Clear();
            FillLogLines();
        }

        /// <summary>디스크의 로그 내용을 비운다. 되돌릴 수 없어 먼저 묻는다.</summary>
        private void PurgeLog()
        {
            string? dir = string.IsNullOrEmpty(LogPath) ? null : Path.GetDirectoryName(LogPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            if (Dialogs.Show(
                    $"{dir}\n\n이 폴더의 로그 파일 내용을 비웁니다.\n지난 오류 기록이 사라집니다. 진행할까요?",
                    "로그 파일 비우기", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            int n = EngineLogView.PurgeLogFiles(dir);
            _mainVM.AddLog($"[PCCE] 로그 파일 {n}개를 비웠습니다.", LogLevel.Warning);
            ReloadLog();
        }

        // ── SETUP (로그 상세 항목) ───────────────────────────────────────

        public ObservableCollection<LogModuleVm> LogModules { get; } = new();

        private PrintEngineLogModules _savedModules = PrintEngineLogModules.None;

        /// <summary>화면에서 고른 조합.</summary>
        public PrintEngineLogModules SelectedModules
        {
            get
            {
                var m = PrintEngineLogModules.None;
                foreach (var vm in LogModules) if (vm.IsChecked) m |= vm.Module;
                return m;
            }
        }

        public string LogModuleValueText => PrintEngineLogModuleSettings.Format(SelectedModules);

        public bool IsLogModulesDirty => SelectedModules != _savedModules;

        /// <summary>가동 중에 켜면 안 되는 항목이 선택됐나.</summary>
        public bool HasHeavyModule => PrintEngineLogModuleSettings.IsHeavy(SelectedModules);

        public ICommand SaveLogModulesCommand { get; }

        private void OnLogModuleToggled()
        {
            foreach (string p in new[] { nameof(SelectedModules), nameof(LogModuleValueText),
                                         nameof(IsLogModulesDirty), nameof(HasHeavyModule) })
                OnPropertyChanged(p);
        }

        private void LoadLogModules()
        {
            foreach (var vm in LogModules) vm.Toggled -= OnLogModuleToggled;
            LogModules.Clear();

            _savedModules = _cfg.Exists ? PrintEngineLogModuleSettings.Read(_cfg)
                                        : PrintEngineLogModuleSettings.Default;

            foreach (var (module, label, desc) in PrintEngineLogModuleSettings.All)
            {
                var vm = new LogModuleVm(module, label, desc)
                {
                    IsChecked = (_savedModules & module) != 0,
                };
                vm.Toggled += OnLogModuleToggled;
                LogModules.Add(vm);
            }
            OnLogModuleToggled();
        }

        /// <summary>cfg 의 LogCtrlBits 한 줄만 바꿔 쓴다. 주석과 줄 순서는 그대로 둔다.</summary>
        private void SaveLogModules()
        {
            if (!ConfigExists) return;

            var picked = SelectedModules;

            // 헤드는 가상이어도 cfg 파일은 진짜로 바뀐다. 실장 cfg 를 가리키고 있으면
            // 그 파일이 고쳐지므로, 어느 파일인지 먼저 보여 준다.
            if (IsSimulated &&
                Dialogs.Show($"{ConfigPath}\n\n헤드는 가상이지만 이 파일은 실제로 바뀝니다. 진행할까요?",
                    "가상 모드 — 실제 파일 수정", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            if (PrintEngineLogModuleSettings.IsHeavy(picked) &&
                Dialogs.Show(
                    "선택한 항목 중에는 가동 중 켜면 호스트 PC 부하가 급증하는 것이 있습니다.\n" +
                    "인쇄 중이면 데이터 공급이 늦어 FIFO under-run 이 날 수 있습니다.\n\n그래도 저장할까요?",
                    "무거운 로그 항목", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            try
            {
                PrintEngineLogModuleSettings.Save(ConfigPath, picked);
                _savedModules = picked;
                _mainVM.AddLog($"[PCCE] 로그 항목 저장: {PrintEngineLogModuleSettings.Format(picked)}", LogLevel.Info);

                Dialogs.Show("저장했습니다.\n엔진이 다시 뜰 때 적용됩니다.", "저장 완료",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[PCCE] 로그 항목 저장 실패: {ex.Message}", LogLevel.Error);
                Dialogs.Show("저장하지 못했습니다.\n" + ex.Message, "저장 실패",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Refresh();
        }
    }

    /// <summary>상태 표의 한 줄. 이름 칸과 값 칸으로만 이루어진다.</summary>
    public sealed record StatusRow(string Label, string Value);

    /// <summary>로그 상세 항목 체크박스 한 개.</summary>
    public sealed class LogModuleVm : ViewModelBase
    {
        public LogModuleVm(PrintEngineLogModules module, string label, string description)
        {
            Module      = module;
            Label       = label;
            Description = description;
            IsHeavy     = PrintEngineLogModuleSettings.IsHeavy(module);
        }

        public PrintEngineLogModules Module { get; }
        public string Label { get; }
        public string Description { get; }

        /// <summary>가동 중에 켜면 안 되는 항목. 화면에서 눈에 띄게 표시한다.</summary>
        public bool IsHeavy { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (SetProperty(ref _isChecked, value)) Toggled?.Invoke(); }
        }

        public event Action? Toggled;
    }
}
