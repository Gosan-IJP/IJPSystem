using IJPSystem.Drivers.Motion;
using IJPSystem.Machines.Pulse;
using IJPSystem.Platform.Domain;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.IO;
using IJPSystem.Platform.Domain.Models.Log;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Infrastructure.Repositories;
using IJPSystem.Platform.HMI.Common;
using static IJPSystem.Platform.HMI.Common.Loc;
using IJPSystem.Platform.HMI.Views;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly PulseController _controller;

        private DispatcherTimer _fastTimer;
        private DispatcherTimer _slowTimer;

        private MainDashboardViewModel _mainDashboardVM;
        private PatternPrintViewModel? _patternPrintVM;
        private MotorControlViewModel? _motorControlVM;
        private NJIViewModel? _njiVM;
        public ObservableCollection<AxisViewModel> SharedAxisList { get; } = new();
        private LogWindowView? _logWindowView;

        private bool _hasActiveAlarm;
        public bool HasActiveAlarm
        {
            get => _hasActiveAlarm;
            set
            {
                _hasActiveAlarm = value;
                OnPropertyChanged(nameof(HasActiveAlarm));
                OnPropertyChanged(nameof(MachineStatusText));
            }
        }

        private bool _isStandby;
        public bool IsStandby
        {
            get => _isStandby;
            set
            {
                _isStandby = value;
                OnPropertyChanged(nameof(IsStandby));
                OnPropertyChanged(nameof(MachineStatusText));
            }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                _isRunning = value;
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(MachineStatusText));
            }
        }

        // ── 시퀀스 "실제 실행 중" 상태 (전역, 일시정지/정지 상태는 false) ──
        // 화면 전환 차단에만 사용. SequenceVM/PnidVM 이 자체 상태 변화 시 SetSequenceRunning 호출.
        private bool _isSequenceRunning;
        public bool IsSequenceRunning
        {
            get => _isSequenceRunning;
            private set => SetProperty(ref _isSequenceRunning, value);
        }
        public void SetSequenceRunning(bool active) => IsSequenceRunning = active;

        // 모든 실행 경로를 통합 — Sequence/Pnid 화면(IsSequenceRunning) + 메인 대시보드 Auto Print(IsRunning, paused 아님)
        // MainWindow.Closing 의 운전 중 종료 차단 게이트에 사용
        public bool IsOperationRunning =>
            IsSequenceRunning ||
            (_mainDashboardVM?.IsRunning == true && _mainDashboardVM?.IsPaused != true);

        // ── StatusBar ─────────────────────────────────────────────────────────
        public string MachineStatusText => HasActiveAlarm ? "ALARM"
                                         : IsRunning     ? "RUNNING"
                                         : IsStandby     ? "STANDBY"
                                                         : "IDLE";

        private bool _ioConnected;
        public bool IOConnected
        {
            get => _ioConnected;
            private set => SetProperty(ref _ioConnected, value);
        }

        private bool _motionConnected;
        public bool MotionConnected
        {
            get => _motionConnected;
            private set => SetProperty(ref _motionConnected, value);
        }

        private bool _visionConnected;
        public bool VisionConnected
        {
            get => _visionConnected;
            private set => SetProperty(ref _visionConnected, value);
        }

        private string _lastLogMessage = "System Ready...";
        public string LastLogMessage
        {
            get => _lastLogMessage;
            private set => SetProperty(ref _lastLogMessage, value);
        }

        private UserRole _currentUserRole = UserRole.Engineer;
        public UserRole CurrentUserRole
        {
            get => _currentUserRole;
            set
            {
                SetProperty(ref _currentUserRole, value);
                OnPropertyChanged(nameof(UserStatusText));
                OnPropertyChanged(nameof(IsEngineerMode)); // 누락 수정
                OnPropertyChanged(nameof(LoginButtonText));
                (ExitCommand as RelayCommand)?.RaiseCanExecuteChanged(); // 권한 게이트 재평가
            }
        }

        public string LoginButtonText =>
            CurrentUserRole == UserRole.Operator ? "LOGIN" : "LOGOUT";

        private object? _currentView;
        public object? CurrentView
        {
            get => _currentView;
            set
            {
                if (ReferenceEquals(_currentView, value)) return;
                // 초기화 화면을 벗어나면 진행 중인 초기화 시퀀스를 중단 —
                // 백그라운드로 계속 대기하다 자동 진행(READY 이동 등)되는 것을 방지.
                if (_currentView is InitializeView oldInit &&
                    oldInit.DataContext is InitializeViewModel oldInitVm)
                    oldInitVm.Abort();
                // 화면 전환 시 이전 ViewModel 의 Timer/이벤트 정리 (메모리 누수 방지)
                // RecipeVM 등 재사용 객체는 IDisposable 미구현이므로 자동으로 건너뜀
                (_currentView as IDisposable)?.Dispose();
                SetProperty(ref _currentView, value);
            }
        }

        private string _currentRecipeName = "Default";
        public string CurrentRecipeName
        {
            get => _currentRecipeName;
            set => SetProperty(ref _currentRecipeName, value);
        }

        private string _selectedMenu = "MAIN";
        public string SelectedMenu
        {
            get => _selectedMenu;
            set
            {
                if (SetProperty(ref _selectedMenu, value))
                {
                    // 서브메뉴가 없는 화면(알람이력)에서는 우측 서브메뉴 영역 자체를 숨김
                    OnPropertyChanged(nameof(IsSubMenuVisible));
                    OnPropertyChanged(nameof(SubMenuColumnWidth));
                }
            }
        }

        // 알람이력 화면은 서브메뉴 버튼이 없으므로 우측 패널/컬럼을 접는다
        public bool IsSubMenuVisible => _selectedMenu != "ALARM";
        public System.Windows.GridLength SubMenuColumnWidth
            => _selectedMenu == "ALARM" ? new System.Windows.GridLength(0) : new System.Windows.GridLength(220);

        private string _selectedSubMenu = "";
        public string SelectedSubMenu
        {
            get => _selectedSubMenu;
            set => SetProperty(ref _selectedSubMenu, value);
        }

        private AlarmViewModel _alarmVM;
        public AlarmViewModel AlarmVM => _alarmVM;

        public LogViewModel LogVM { get; } = new LogViewModel();

        public string UserStatusText => $"USER: {CurrentUserRole.ToString().ToUpper()}";
        public bool IsEngineerMode =>
            CurrentUserRole == UserRole.Engineer ||
            CurrentUserRole == UserRole.Admin;

        private string[] _languages = { "KO", "EN" };
        private int _langIndex = 0;          // 초기 언어 = KO

        private string _currentLanguage = "KO";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (SetProperty(ref _currentLanguage, value))
                {
                    if (RecipeVM != null)
                        RecipeVM.CurrentLanguage = value;
                    OnPropertyChanged(nameof(UserStatusText));
                }
            }
        }

        private bool _isMotorSubMenuVisible;
        public bool IsMotorSubMenuVisible
        {
            get => _isMotorSubMenuVisible;
            set => SetProperty(ref _isMotorSubMenuVisible, value);
        }

        private bool _isVisionSubMenuVisible;
        public bool IsVisionSubMenuVisible
        {
            get => _isVisionSubMenuVisible;
            set => SetProperty(ref _isVisionSubMenuVisible, value);
        }

        private bool _isPrintSubMenuVisible;
        public bool IsPrintSubMenuVisible
        {
            get => _isPrintSubMenuVisible;
            set => SetProperty(ref _isPrintSubMenuVisible, value);
        }

        private string _machineTitle = string.Empty;
        public string MachineTitle
        {
            get => _machineTitle;
            set => SetProperty(ref _machineTitle, value);
        }

        public RecipeViewModel RecipeVM { get; }
        public string DisplayMachineName => _controller?.GetMachine()?.MachineName ?? "UNKNOWN DEVICE";
        public string SystemTime => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public ObservableCollection<IOViewModel> dgInputList { get; } = new();
        public ObservableCollection<IOViewModel> dgOutputList { get; } = new();
        public ObservableCollection<IOViewModel> agInputList { get; } = new();
        public ObservableCollection<IOViewModel> agOutputList { get; } = new();
        public ObservableCollection<LogModel> SystemLogs { get; } = new();

        public ICommand MoveWindowCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand ToggleLanguageCommand { get; }
        public ICommand OpenLogWindowCommand { get; }

        public MainViewModel(PulseController controller)
        {
            _controller = controller;
            var machine = _controller.GetMachine();
            MachineTitle = _controller.GetMachine().MachineName.ToUpper();

            _slowTimer = new DispatcherTimer();
            _fastTimer = new DispatcherTimer();

            InitializeSharedAxes();

            // _alarmVM 을 먼저 생성 — RecipeVM/MainDashboardVM 의 raiseAlarm 람다가
            // 생성자 내에서 호출돼도 알람이 유실되지 않도록 의존성 순서 보장
            _alarmVM = new AlarmViewModel(this.AddLog);
            _alarmVM.PropertyChanged += OnAlarmViewModelPropertyChanged;

            // AlarmVM ctor 내 LoadHistoryFromDatabase 가 PropertyChanged 를 발화했지만
            // 구독 전이라 놓쳤을 수 있음. 활성 알람이면 명시적으로 sync 호출해서 초기 상태 반영.
            // (활성 아닐 땐 SyncSystemStatusWithAlarm 가 "Cleared" 로그를 남기므로 호출 회피)
            if (_alarmVM.HasActiveAlarm)
                SyncSystemStatusWithAlarm();

            RecipeVM = new RecipeViewModel(SharedAxisList, this.AddLog, code => _alarmVM.RaiseAlarm(code));

            var motionAdapter = new Services.MotionServiceAdapter(this);
            _mainDashboardVM = new MainDashboardViewModel(
                    this.AddLog,
                    this.UpdateSystemStatus,
                    machine,
                    RecipeVM.ActiveRecipeName,
                    motionAdapter,
                    raiseAlarm: code => _alarmVM.RaiseAlarm(code),
                    getPointAxisMm: motionAdapter.GetAxisPositionMm,
                    hasActiveAlarm: () => HasActiveAlarm,
                    getSwathCount: () => RecipeVM.ActiveSwath,
                    getHeadLength: () => RecipeVM.ActiveHeadLength,
                    getPrintDirection: () => RecipeVM.ActivePrintDirection
                );

            RecipeVM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(RecipeViewModel.ActiveRecipeName))
                {
                    CurrentRecipeName = RecipeVM.ActiveRecipeName;
                    _mainDashboardVM.ActiveRecipeName = RecipeVM.ActiveRecipeName;
                }
            };
            RecipeVM.CurrentLanguage = this.CurrentLanguage;

            _mainDashboardVM.PropertyChanged += OnDashboardViewModelPropertyChanged;

            MoveWindowCommand = new RelayCommand<string>(ExecuteMoveWindow);
            ExitCommand = new RelayCommand(_ => OnExit(), _ => CanExit());
            ClearLogCommand = new RelayCommand(_ => OnClearLog());
            LogoutCommand = new RelayCommand(_ => OnLogOut());
            ToggleLanguageCommand = new RelayCommand(_ => ExecuteToggleLanguage());
            OpenLogWindowCommand = new RelayCommand(_ => ExecuteOpenLogWindow());

            InitializeIOList();
            ExecuteMoveWindow("MAIN");
            StartTimers();

            // 초기 상태: 대기(Standby)
            IsStandby = true;
            _controller.GetMachine().SetSystemStatus(MachineState.Standby);

            AddLog(T("Log_SystemInit"), LogLevel.Success);

            // 드라이버 실제 로드 상태를 화면 로그에 표시(실장/가상·연결여부 즉시 확인용)
            LogDriverStatus();
        }

        /// <summary>
        /// 각 드라이버가 실제로 어떤 구현으로 로드됐는지(실장 vs Virtual)와 연결 상태를 화면 로그에 남긴다.
        /// - Virtual* 타입이면 "가상" → AppConfig.json 의 DriverMode 가 실장으로 안 먹은 것.
        /// - 실장 타입인데 미연결이면 SDK/하드웨어 문제(echo 강등) → C:\Logs 의 상세 로그 확인.
        /// </summary>
        private void LogDriverStatus()
        {
            var m = _controller?.GetMachine();
            if (m == null) return;

            // 앱이 실제로 읽은 AppConfig 경로와 파싱된 DriverMode 값을 화면 로그에 표시.
            // → Virtual 로 뜨면 "이 경로의 파일"을 Comizoa 로 고쳐야 함이 즉시 드러남.
            try
            {
                var dm = IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Current?.DriverMode;
                string cfgPath = IJPSystem.Platform.Common.Utilities.PathUtils.GetConfigPath("AppConfig.json");
                AddLog($"[CONFIG] {cfgPath} → IO={dm?.IO}, Motion={dm?.Motion}, Vision={dm?.Vision}", LogLevel.Info);
            }
            catch { /* 진단 로그 실패는 무시 */ }

            void Report(string tag, object? drv, bool connected)
            {
                string kind = drv?.GetType().Name ?? "None";
                bool isVirtual = kind.StartsWith("Virtual") || kind == "None";
                string mode = isVirtual ? "가상(Virtual)" : "실장";
                LogLevel level = isVirtual ? LogLevel.Warning
                               : connected ? LogLevel.Success
                                           : LogLevel.Error;   // 실장인데 미연결 = SDK/HW 문제
                AddLog($"[{tag}] {kind} — {mode}, 연결={(connected ? "OK" : "실패/미연결")}", level);
            }

            Report("IO",     m.IO,     m.IO?.IsConnected     ?? false);
            Report("MOTION", m.Motion, m.Motion?.IsConnected ?? false);
            Report("VISION", m.Vision, m.Vision?.IsConnected ?? false);
        }

        private void StartTimers()
        {
            _fastTimer = new DispatcherTimer(DispatcherPriority.Render);
            _fastTimer.Interval = TimeSpan.FromMilliseconds(100);
            _fastTimer.Tick += (s, e) =>
            {
                foreach (var axis in SharedAxisList)
                    axis.UpdateMotorStatus();
            };

            _slowTimer = new DispatcherTimer(DispatcherPriority.Background);
            _slowTimer.Interval = TimeSpan.FromMilliseconds(500);
            _slowTimer.Tick += (s, e) =>
            {
                OnPropertyChanged(nameof(SystemTime));
                Task.Run(() => UpdateIOStates());
                UpdateDriverConnections();
            };

            _fastTimer.Start();
            _slowTimer.Start();
        }

        private void InitializeSharedAxes()
        {
            var motionDriver = _controller?.GetMachine()?.Motion;
            var configs = _controller?.GetMachine()?.Config?.MotionAxisList;

            if (motionDriver != null && configs != null)
            {
                foreach (var config in configs)
                    SharedAxisList.Add(new AxisViewModel(motionDriver, config, this));
            }
        }

        public void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            if (System.Windows.Application.Current?.Dispatcher is null) return;

            // UI 모델과 sink 양쪽이 동일한 시각을 사용하도록 한 번 캡처
            var time = DateTime.Now;

            System.Windows.Application.Current!.Dispatcher.Invoke(() =>
            {
                var log = new LogModel { Message = message, Level = level, Time = time };
                SystemLogs.Add(log);
                // 메인창 UI는 롤링 버퍼(최근 100개만) — 전체 히스토리는 LogWindow에서 DB 직접 조회
                if (SystemLogs.Count > 100) SystemLogs.RemoveAt(0);
                LastLogMessage = message;
            });

            // 두 sink 모두 적재 — txt 는 fail-safe 백업, DB 는 화면 필터/검색용
            LoggerService.WriteToFile(level.ToString(), message);
            SystemLogRepository.Write(time, level.ToString(), message);
        }

        private void InitializeIOList()
        {
            var machine = _controller?.GetMachine();
            if (machine?.IO == null) return;

            var allIOs = machine.IO.GetAllIOInfo();
            if (allIOs == null) return;

            foreach (var io in allIOs)
            {
                var vm = new IOViewModel
                {
                    Address = io.Address ?? "",
                    Index = io.Index ?? "",
                    Description = io.Description ?? "",
                    IoCategory = io.IoCategory ?? "",
                    ContactType = io.ContactType?.ToUpper() == "N.C" ? IOContactType.NC : IOContactType.NO
                };

                string category = vm.IoCategory.ToLower().Replace(" ", "");
                if (category.Contains("digital"))
                {
                    if (category.Contains("input"))
                        dgInputList.Add(vm);
                    else if (category.Contains("output"))
                    {
                        vm.ToggleCommand = new RelayCommand(_ => ExecuteForceOutput(vm));
                        dgOutputList.Add(vm);
                    }
                }
                else if (category.Contains("analog"))
                {
                    vm.Mode = IOMode.Analog;
                    if (vm.Address.StartsWith("X"))
                        agInputList.Add(vm);
                    else if (vm.Address.StartsWith("Y"))
                        agOutputList.Add(vm);
                }
            }
        }

        private void UpdateIOStates()
        {
            var ioDriver = _controller?.GetMachine()?.IO;
            if (ioDriver == null) return;

            void UpdateList(ObservableCollection<IOViewModel> list)
            {
                foreach (var vm in list.ToList())
                {
                    if (string.IsNullOrEmpty(vm.Index) || string.IsNullOrEmpty(vm.Address)) continue;

                    bool isAnalog = vm.IoCategory?.ToLower().Contains("analog") ?? false;
                    if (isAnalog)
                    {
                        vm.AnalogValue = vm.Address!.StartsWith("X")
                            ? ioDriver.GetAnalogInput(vm.Index!)
                            : ioDriver.GetAnalogOutput(vm.Index!);
                    }
                    else
                    {
                        vm.HardwareSignal = vm.Address!.StartsWith("Y")
                            ? ioDriver.GetOutput(vm.Index!)
                            : ioDriver.GetInput(vm.Index!);
                    }
                }
            }

            UpdateList(dgInputList);
            UpdateList(dgOutputList);
            UpdateList(agInputList);
            UpdateList(agOutputList);
        }

        private void UpdateDriverConnections()
        {
            var machine = _controller?.GetMachine();
            if (machine == null) return;
            IOConnected     = machine.IO?.IsConnected     ?? false;
            MotionConnected = machine.Motion?.IsConnected ?? false;
            VisionConnected = machine.Vision?.IsConnected ?? false;
        }

        private void ExecuteForceOutput(IOViewModel vm)
        {
            if (string.IsNullOrEmpty(vm.Index)) return;

            bool nextState = !vm.HardwareSignal;
            string onOff = nextState ? T("Msg_ForceOutputOn") : T("Msg_ForceOutputOff");
            string desc  = vm.Description ?? string.Empty;
            if (Dialogs.Show(
                T("Msg_ForceOutputConfirm", desc, onOff),
                T("Msg_ForceOutputTitle"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _controller.GetMachine().IO.SetOutput(vm.Index, nextState);
                AddLog(T("Log_ManualControl", desc, onOff), LogLevel.Warning);
            }
        }

        // 마지막으로 성공한 화면 전환의 메뉴/서브메뉴 — 차단 시 라디오 버튼 시각 상태 복원에 사용
        private string _confirmedMenu    = "MAIN";
        private string _confirmedSubMenu = "AUTO_PRINT";

        private void ExecuteMoveWindow(string? destination)
        {
            if (string.IsNullOrEmpty(destination)) return;
            string target = destination.ToUpper();

            // 0-A. AUTO PRINT 실제 실행 중 (일시정지/정지 상태는 허용) — 대시보드/LOG 만 허용
            bool autoPrintActive = _mainDashboardVM?.IsRunning == true
                                && _mainDashboardVM?.IsPaused != true;
            if (autoPrintActive &&
                target != "MAIN" && target != "AUTO_PRINT" && target != "LOG")
            {
                AddLog($"[NAV] AUTO PRINT 실행 중 — '{target}' 화면 전환 거부됨", LogLevel.Warning);
                SelectedMenu    = "MAIN";
                SelectedSubMenu = "AUTO_PRINT";
                CollapseAllSubMenus();
                Dialogs.Show(
                    "AUTO PRINT 실행 중에는 다른 화면으로 전환할 수 없습니다.\nPAUSE 또는 STOP 후 다시 시도하세요.",
                    "화면 전환 차단",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 0-B. 다른 시퀀스(SequenceVM / PnidVM Auto 시퀀스 등) 실제 실행 중 — LOG만 허용
            // 일시정지/정지 상태일 때는 IsSequenceRunning=false 로 갱신되어 차단 해제
            if (IsSequenceRunning && target != "LOG")
            {
                AddLog($"[NAV] 시퀀스 실행 중 — '{target}' 화면 전환 거부됨", LogLevel.Warning);
                // 사용자가 원래 있던 화면으로 라디오 버튼 시각 복원
                SelectedMenu    = _confirmedMenu;
                SelectedSubMenu = _confirmedSubMenu;
                Dialogs.Show(
                    "시퀀스 실행 중에는 다른 화면으로 전환할 수 없습니다.\n일시정지 또는 정지 후 다시 시도하세요.",
                    "화면 전환 차단",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. 권한 체크
            if ((target == "MAINTENANCE" || target == "RECIPE" || target == "MOTOR" ||
                 target == "IO" || target == "MOTOR_INFO") && !IsEngineerMode)
            {
                var loginWin = new LoginWindow { Owner = System.Windows.Application.Current.MainWindow };
                if (loginWin.ShowDialog() == true)
                {
                    CurrentUserRole = loginWin.ResultRole;
                    AddLog(T("Log_LoginRole", CurrentUserRole), LogLevel.Success);
                }
                else return;
            }

            // 2. 서브메뉴 아코디언 처리 + 화면 전환
            switch (target)
            {
                // ── MAIN ──────────────────────────────────────────────
                case "MAIN":
                case "AUTO_PRINT":
                    CollapseAllSubMenus();
                    SelectedMenu    = "MAIN";
                    SelectedSubMenu = "AUTO_PRINT";
                    CurrentView = _mainDashboardVM;
                    AddLog(T("Log_MoveAutoPrint"), LogLevel.Info);
                    break;

                case "INITIALIZE":
                    CollapseAllSubMenus();
                    SelectedMenu    = "MAIN";
                    SelectedSubMenu = "INITIALIZE";
                    CurrentView = new InitializeView { DataContext = new InitializeViewModel(this) };
                    AddLog(T("Log_MoveInitialize"), LogLevel.Info);
                    break;

                // ── PRINT ─────────────────────────────────────────────
                case "PRINT":
                    if (IsPrintSubMenuVisible)
                    {
                        IsPrintSubMenuVisible = false;
                    }
                    else
                    {
                        CollapseAllSubMenus();
                        IsPrintSubMenuVisible = true;
                        SelectedMenu    = "PRINT";
                        SelectedSubMenu = "PATTERN_PRINT";
                        _patternPrintVM ??= new PatternPrintViewModel(this);
                        CurrentView = _patternPrintVM;
                        AddLog(T("Log_Waveform"), LogLevel.Info);
                    }
                    break;

                case "WAVEFORM":
                    IsPrintSubMenuVisible = true;
                    SelectedMenu    = "PRINT";
                    SelectedSubMenu = "WAVEFORM";
                    CurrentView = new WaveformViewModel(this);
                    AddLog(T("Log_Waveform"), LogLevel.Info);
                    break;

                case "PATTERN_PRINT":
                    IsPrintSubMenuVisible = true;
                    SelectedMenu    = "PRINT";
                    SelectedSubMenu = "PATTERN_PRINT";
                    _patternPrintVM ??= new PatternPrintViewModel(this);
                    CurrentView = _patternPrintVM;
                    AddLog(T("Log_PatternPrint"), LogLevel.Info);
                    break;

                case "PRINT_DROP_WATCHER":
                    IsPrintSubMenuVisible = true;
                    SelectedMenu    = "PRINT";
                    SelectedSubMenu = "PRINT_DROP_WATCHER";
                    CurrentView = new DropWatcherViewModel(this);
                    AddLog(T("Log_MoveDropWatcher"), LogLevel.Info);
                    break;

                case "PRINT_GLASS_VIEW":
                    IsPrintSubMenuVisible = true;
                    SelectedMenu    = "PRINT";
                    SelectedSubMenu = "PRINT_GLASS_VIEW";
                    CurrentView = new GlassViewModel(this);
                    AddLog(T("Log_MoveGlassView"), LogLevel.Info);
                    break;

                case "PRINT_INITIALIZE":
                    IsPrintSubMenuVisible = true;
                    SelectedMenu    = "PRINT";
                    SelectedSubMenu = "PRINT_INITIALIZE";
                    CurrentView = new InitializeView { DataContext = new InitializeViewModel(this) };
                    AddLog(T("Log_MoveInitialize"), LogLevel.Info);
                    break;

                case "MAINTENANCE":
                case "IO":
                    CollapseAllSubMenus();
                    SelectedMenu = "MAINTENANCE";
                    SelectedSubMenu = "IO";
                    CurrentView = new IOMonitorView { DataContext = new IOMonitorViewModel(this) };
                    AddLog(T("Log_MoveIO"), LogLevel.Info);
                    break;

                case "MOTOR":
                    if (IsMotorSubMenuVisible)
                    {
                        IsMotorSubMenuVisible = false;
                    }
                    else
                    {
                        IsVisionSubMenuVisible = false;
                        IsMotorSubMenuVisible  = true;
                        SelectedMenu    = "MAINTENANCE";
                        SelectedSubMenu = "AXIS_CONTROL";
                        _motorControlVM ??= new MotorControlViewModel(this);
                        CurrentView = _motorControlVM;
                        AddLog(T("Log_MoveMotor"), LogLevel.Info);
                    }
                    break;

                case "AXIS_CONTROL":
                    IsMotorSubMenuVisible  = true;
                    IsVisionSubMenuVisible = false;
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "AXIS_CONTROL";
                    _motorControlVM ??= new MotorControlViewModel(this);
                    CurrentView = _motorControlVM;
                    AddLog(T("Log_MoveAxisControl"), LogLevel.Info);
                    break;

                case "POSITION_TEACH":
                    IsMotorSubMenuVisible  = true;
                    IsVisionSubMenuVisible = false;
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "POSITION_TEACH";
                    CurrentView = new MotorTeachingViewModel(this);
                    AddLog(T("Log_MovePositionTeach"), LogLevel.Info);
                    break;

                case "VISION":
                    if (IsVisionSubMenuVisible)
                    {
                        IsVisionSubMenuVisible = false;
                    }
                    else
                    {
                        IsMotorSubMenuVisible  = false;
                        IsVisionSubMenuVisible = true;
                        SelectedMenu    = "MAINTENANCE";
                        // NJI 버튼은 숨김 상태이므로 비전 메뉴 기본 화면은 Glass View
                        SelectedSubMenu = "GLASS_VIEW";
                        CurrentView = new GlassViewModel(this);
                        AddLog(T("Log_MoveGlassView"), LogLevel.Info);
                    }
                    break;

                case "NJI":
                    IsVisionSubMenuVisible = true;
                    IsMotorSubMenuVisible  = false;
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "NJI";
                    _njiVM ??= new NJIViewModel(this);
                    CurrentView = _njiVM;
                    AddLog(T("Log_MoveNJI"), LogLevel.Info);
                    break;

                case "GLASS_VIEW":
                    IsVisionSubMenuVisible = true;
                    IsMotorSubMenuVisible  = false;
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "GLASS_VIEW";
                    CurrentView = new GlassViewModel(this);
                    AddLog(T("Log_MoveGlassView"), LogLevel.Info);
                    break;

                case "DROP_WATCHER":
                    IsVisionSubMenuVisible = true;
                    IsMotorSubMenuVisible  = false;
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "DROP_WATCHER";
                    CurrentView = new DropWatcherViewModel(this);
                    AddLog(T("Log_MoveDropWatcher"), LogLevel.Info);
                    break;

                case "VISUAL_MONITOR":
                    IsVisionSubMenuVisible = true;
                    IsMotorSubMenuVisible  = false;
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "VISUAL_MONITOR";
                    CurrentView = new VisualMonitorViewModel(this);
                    AddLog("[VISION] Visual Monitor 이동", LogLevel.Info);
                    break;

                case "PNID":
                    CollapseAllSubMenus();
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "PNID";
                    CurrentView = new PnidView { DataContext = new PnidViewModel(this) };
                    AddLog(T("Log_MovePNID"), LogLevel.Info);
                    break;

                case "SEQUENCE":
                    CollapseAllSubMenus();
                    SelectedMenu    = "MAINTENANCE";
                    SelectedSubMenu = "SEQUENCE";
                    CurrentView = new SequenceViewModel(this);
                    AddLog(T("Log_Sequence"), LogLevel.Info);
                    break;

                case "RECIPE":
                    CollapseAllSubMenus();
                    SelectedMenu    = "RECIPE";
                    SelectedSubMenu = "MOTOR_INFO";
                    CurrentView = RecipeVM;
                    RecipeVM.CurrentDataType = RecipeDataType.Motor;
                    AddLog(T("Log_MoveRecipe"), LogLevel.Info);
                    break;

                case "MOTOR_INFO":
                    SelectedMenu    = "RECIPE";
                    SelectedSubMenu = "MOTOR_INFO";
                    CurrentView = RecipeVM;
                    RecipeVM.CurrentDataType = RecipeDataType.Motor;
                    AddLog(T("Log_MoveMotorInfo"), LogLevel.Info);
                    break;

                case "TEACH_INFO":
                    SelectedMenu    = "RECIPE";
                    SelectedSubMenu = "TEACH_INFO";
                    CurrentView = RecipeVM;
                    RecipeVM.CurrentDataType = RecipeDataType.Teach;
                    AddLog(T("Log_MoveTeachPointInfo"), LogLevel.Info);
                    break;

                case "OTHER_INFO":
                    SelectedMenu = "RECIPE";
                    SelectedSubMenu = "OTHER_INFO";
                    CurrentView = RecipeVM;
                    RecipeVM.CurrentDataType = RecipeDataType.Other;
                    AddLog(T("Log_OtherInfo"), LogLevel.Info);
                    break;

                case "ALARM":
                    CollapseAllSubMenus();
                    SelectedMenu    = "ALARM";
                    SelectedSubMenu = "";
                    CurrentView = new AlarmHistoryView { DataContext = this };
                    AddLog(T("Log_MoveAlarm"), LogLevel.Info);
                    break;

                case "LOG":
                    if (ExecuteOpenLogWindow())
                        AddLog(T("Log_LogWindowOpened"), LogLevel.Info);
                    break;

                default:
                    CollapseAllSubMenus();
                    SelectedMenu = "MAIN"; 
                    CurrentView = _mainDashboardVM;
                    AddLog(T("Log_UnknownMenu", destination), LogLevel.Warning);
                    break;
            }

            // 차단되지 않고 정상 전환된 경우 — 마지막 화면 상태 갱신 (다음 차단 시 복원용)
            if (target != "LOG")
            {
                _confirmedMenu    = SelectedMenu;
                _confirmedSubMenu = SelectedSubMenu;
            }
        }

        /// <summary>모든 서브메뉴 그룹을 접습니다.</summary>
        private void CollapseAllSubMenus()
        {
            IsMotorSubMenuVisible  = false;
            IsVisionSubMenuVisible = false;
            IsPrintSubMenuVisible  = false;
        }

        private void ChangeUserRole(UserRole newRole)
        {
            CurrentUserRole = newRole;
            AddLog(T("Log_RoleChanged", newRole), LogLevel.Info);
        }

        private void OnClearLog()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SystemLogs.Clear();
                AddLog(T("Log_LogCleared"), LogLevel.Info);
            });
        }

        // 종료 가능 조건 — Engineer/Admin 권한
        // 운전중에는 버튼을 활성 상태로 두고 클릭 시 MainWindow.Closing 에서 메세지로 차단
        // (왜 안 되는지 사용자가 알 수 있도록 비활성화 대신 메시지 안내 방식 채택)
        private bool CanExit() => IsEngineerMode;

        private void OnExit()
        {
            // 다이얼로그/정리는 MainWindow.Closing에서 일원화 처리
            System.Windows.Application.Current.MainWindow?.Close();
        }

        // MainWindow.Closing에서 호출 — 종료 직전 ViewModel 측 정리
        public void OnApplicationClosing()
        {
            AddLog(T("Log_ExitAttempt"), LogLevel.Fatal);

            // DispatcherTimer 정지 — Tick 핸들러가 VM 참조를 잡고 있어 미정지 시 GC 누수
            _fastTimer.Stop();
            _slowTimer.Stop();

            // 종료 전 램프 소등 (드라이버 정리는 App.OnExit에서 일괄 처리)
            _controller?.GetMachine()?.SetSystemStatus(MachineState.Idle);
        }

        private void OnAlarmViewModelPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AlarmViewModel.HasActiveAlarm))
            {
                SyncSystemStatusWithAlarm();
                // 런 중 알람이 발생/해제되면 대시보드 시퀀스를 일시정지/재개
                _mainDashboardVM?.OnAlarmActiveChanged(_alarmVM.HasActiveAlarm);
                // 초기화 화면에서 알람이 발생하면 초기화 시퀀스를 중단(대기 후 자동 진행 방지)
                if (_alarmVM.HasActiveAlarm &&
                    CurrentView is InitializeView initView &&
                    initView.DataContext is InitializeViewModel initVm)
                    initVm.Abort();
            }
        }

        private void SyncSystemStatusWithAlarm()
        {
            this.HasActiveAlarm = _alarmVM.HasActiveAlarm;

            if (!this.HasActiveAlarm)
            {
                this.IsStandby = true;
                this.IsRunning = false;
                _controller.GetMachine().SetSystemStatus(MachineState.Standby); 
                AddLog(T("Log_AlarmCleared"), LogLevel.Info);
            }
            else
            {
                this.IsStandby = false;
                this.IsRunning = false;
                _controller.GetMachine().SetSystemStatus(MachineState.Alarm);   
            }
        }

        private void OnDashboardViewModelPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainDashboardViewModel.IsRunning))
                SyncSystemStatusWithDashboard();
        }

        private void SyncSystemStatusWithDashboard()
        {
            this.IsRunning = _mainDashboardVM.IsRunning;

            if (this.IsRunning)
            {
                this.IsStandby = false;
                _controller.GetMachine().SetSystemStatus(MachineState.Running);  
            }
            else if (!HasActiveAlarm)
            {
                this.IsStandby = true;
                _controller.GetMachine().SetSystemStatus(MachineState.Standby);  
            }
        }

        /// <summary>초기화(INITIALIZE) 수행 시 호출 — 대시보드 오토런도 초기 상태로 리셋.</summary>
        public void ResetAutoRunForInitialize() => _mainDashboardVM?.ResetForInitialize();

        public PulseController GetController() => _controller;

        private void OnLogOut()
        {
            // Operator 상태이면 로그인 동작 — LoginWindow 띄워 권한 상승
            if (CurrentUserRole == UserRole.Operator)
            {
                var loginWin = new LoginWindow { Owner = System.Windows.Application.Current.MainWindow };
                if (loginWin.ShowDialog() == true)
                {
                    CurrentUserRole = loginWin.ResultRole;
                    AddLog(T("Log_LoginRole", CurrentUserRole), LogLevel.Success);
                }
                return;
            }

            // Engineer/Admin 상태이면 로그아웃 동작 — Operator 로 전환
            var result = Dialogs.Show(T("Msg_LogoutConfirm"), T("Msg_LogoutTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                ChangeUserRole(UserRole.Operator);
                AddLog(T("Log_LogoutToOperator"), LogLevel.Info);
                SelectedMenu = "MAIN";
                ExecuteMoveWindow("MAIN");
            }
        }

        private void ExecuteToggleLanguage()
        {
            _langIndex = (_langIndex + 1) % _languages.Length;
            CurrentLanguage = _languages[_langIndex];

            var langFile = CurrentLanguage switch
            {
                "KO" => "Common/Resources/Languages/ko-KR.xaml",
                "EN" => "Common/Resources/Languages/en-US.xaml",
                _ => "Common/Resources/Languages/ko-KR.xaml"
            };

            var newDict = new ResourceDictionary
            {
                Source = new Uri(langFile, UriKind.Relative)
            };

            var mergedDicts = System.Windows.Application.Current.Resources.MergedDictionaries;
            var existing = mergedDicts.FirstOrDefault(d =>
                d.Source?.OriginalString.Contains("Languages") == true);

            if (existing != null) mergedDicts.Remove(existing);
            mergedDicts.Add(newDict);

            // 언어 사전 교체 후 — Steps 의 Name (이미 번역된 캐시) 을 새 언어로 다시 풀어줘야 함.
            // 항상 살아있는 MainDashboardVM, 그리고 현재 CurrentView 에 있을 수 있는
            // SequenceViewModel / InitializeViewModel 도 함께 처리.
            _mainDashboardVM?.RefreshStepNames();

            if (CurrentView is SequenceViewModel seqVm)
            {
                seqVm.RefreshStepNames();
            }
            else if (CurrentView is InitializeView initView &&
                     initView.DataContext is InitializeViewModel initVm)
            {
                initVm.RefreshStepNames();
            }
        }

        private bool ExecuteOpenLogWindow()
        {
            // Admin 권한 전용 — 모든 진입 경로(메뉴/버튼)에서 차단
            if (CurrentUserRole != UserRole.Admin)
            {
                Dialogs.Show(
                    "로그 화면은 관리자(Admin) 권한으로만 접근할 수 있습니다.",
                    "권한 부족",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                // 단일 인스턴스 — 이미 떠 있으면 활성화만
                if (_logWindowView != null &&
                    System.Windows.Application.Current.Windows.Cast<Window>().Any(w => w == _logWindowView))
                {
                    LogVM.Refresh();
                    _logWindowView.Activate();
                    return true;
                }

                _logWindowView = new LogWindowView { DataContext = LogVM };

                // Owner — Application.Current.MainWindow 가 LoginWindow 일 수 있어 실제 MainWindow 검색
                var owner = System.Windows.Application.Current.Windows
                    .OfType<MainWindow>()
                    .FirstOrDefault(w => w.IsLoaded);
                if (owner != null)
                {
                    _logWindowView.Owner = owner;
                    _logWindowView.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    _logWindowView.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }

                _logWindowView.Closed += (_, __) => _logWindowView = null;

                LogVM.Refresh();   // 열 때마다 최신 데이터로 갱신
                _logWindowView.Show();
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Show($"로그 창을 여는 중 오류가 발생했습니다: {ex.Message}");
                return false;
            }
        }

        public void UpdateSystemStatus(bool isAlarmActive)
        {
            HasActiveAlarm = isAlarmActive;
        }
    }
}