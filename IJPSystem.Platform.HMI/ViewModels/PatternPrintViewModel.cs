using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Common.Enums;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Services;
using static IJPSystem.Platform.HMI.Common.Loc;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>
    /// Pattern Generator 화면 — 사각 영역(가로×세로)을 헤드팩으로 도장 인쇄하기 위한 파라미터를 입력받는다.
    /// 실제 인쇄 실행은 시퀀스/머신 레이어에서 수행하고 여기서는 사용자 입력값과 원점 캡처만 담당한다.
    /// </summary>
    public class PatternPrintViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;

        // ── 조그/모션 (Jog Control · Motion State) ──────────────────────
        // 공유 축 리스트. Motion State 위치 표시 및 조그 대상.
        public ObservableCollection<AxisViewModel> AxisList => _mainVM.SharedAxisList;

        // 이름 부분일치로 축을 찾는다 (예: "T AXIS" → "T")
        public AxisViewModel? AxisX  => ResolveByTag("X");
        public AxisViewModel? AxisY  => ResolveByTag("Y");
        public AxisViewModel? AxisZ  => ResolveByTag("Z");
        public AxisViewModel? AxisT => ResolveByTag("T");
        private AxisViewModel? ResolveByTag(string tag) =>
            AxisList.FirstOrDefault(a => a.Info?.Name != null &&
                a.Info.Name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);

        // 조그 단위(UNIT): 0=Cont, 0.01=10µm, 0.1=100µm, 1.0=1000µm — 조그 시 대상 축에 적용
        private double _jogUnit = 0;
        public double JogUnit
        {
            get => _jogUnit;
            set
            {
                if (SetProperty(ref _jogUnit, value))
                    OnPropertyChanged(nameof(JogUnitIndex));
            }
        }

        // 콤보박스(Continuous / 10µm / 100µm / 1000µm) 선택 인덱스 ↔ JogUnit 매핑
        public int JogUnitIndex
        {
            get => _jogUnit switch { 0.01 => 1, 0.1 => 2, 1.0 => 3, _ => 0 };
            set => JogUnit = value switch { 1 => 0.01, 2 => 0.1, 3 => 1.0, _ => 0.0 };
        }

        // 조그 속도 배율(SPEED)
        private double _jogSpeedScale = 1.0;
        public double JogSpeedScale
        {
            get => _jogSpeedScale;
            set
            {
                if (SetProperty(ref _jogSpeedScale, value))
                {
                    OnPropertyChanged(nameof(IsJogSpeedSlow));
                    OnPropertyChanged(nameof(IsJogSpeedNormal));
                    OnPropertyChanged(nameof(IsJogSpeedFast));
                }
            }
        }
        public bool IsJogSpeedSlow   { get => _jogSpeedScale == 0.25; set { if (value) JogSpeedScale = 0.25; } }
        public bool IsJogSpeedNormal { get => _jogSpeedScale == 1.0;  set { if (value) JogSpeedScale = 1.0; } }
        public bool IsJogSpeedFast   { get => _jogSpeedScale == 2.0;  set { if (value) JogSpeedScale = 2.0; } }

        // ── Motion 패널 (Home / Absolute·Relative / Axis / Target / Move) ──
        private static readonly string[] _motionAxisTags = { "X", "Y", "Z", "T" };

        // Axis 콤보 선택(0=X,1=Y,2=Z,3=T)
        private int _motionAxisIndex = 0;
        public int MotionAxisIndex
        {
            get => _motionAxisIndex;
            set
            {
                if (SetProperty(ref _motionAxisIndex, Math.Clamp(value, 0, 3)))
                    OnPropertyChanged(nameof(SelectedMotionAxis));
            }
        }

        // 좌표 모드 콤보(0=Absolute, 1=Relative)
        private int _motionModeIndex = 0;
        public int MotionModeIndex
        {
            get => _motionModeIndex;
            set => SetProperty(ref _motionModeIndex, value);
        }

        // 입력 가능한 목표 위치(mm) — TextBox 양방향 바인딩
        private double _motionTarget;
        public double MotionTarget
        {
            get => _motionTarget;
            set => SetProperty(ref _motionTarget, value);
        }

        // +/- 버튼 증분(mm)
        private const double MotionStep = 1.0;

        // 현재 선택된 Motion 대상 축
        public AxisViewModel? SelectedMotionAxis => ResolveByTag(_motionAxisTags[Math.Clamp(_motionAxisIndex, 0, 3)]);

        public ICommand MotionMoveCommand     { get; private set; } = null!;
        public ICommand MotionStepUpCommand   { get; private set; } = null!;
        public ICommand MotionStepDownCommand { get; private set; } = null!;

        // 티칭 포인트 바로가기 — 활성 레시피의 READY / PRINT START / PRINT END / PURGE 좌표로 이동
        public ICommand MoveReadyCommand      { get; private set; } = null!;
        public ICommand MovePrintStartCommand { get; private set; } = null!;
        public ICommand MovePrintEndCommand   { get; private set; } = null!;
        public ICommand MovePurgeCommand      { get; private set; } = null!;

        // 포인트 이동 중 중복 클릭 차단
        private bool _isPointMoving;
        public bool IsPointMoving
        {
            get => _isPointMoving;
            private set
            {
                if (!SetProperty(ref _isPointMoving, value)) return;
                (MoveReadyCommand      as RelayCommand)?.RaiseCanExecuteChanged();
                (MovePrintStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (MovePrintEndCommand   as RelayCommand)?.RaiseCanExecuteChanged();
                (MovePurgeCommand      as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        // ── Spit (노즐 토출 애니메이션) ───────────────────────────────
        // 버튼 클릭 시마다 ON/OFF 토글 → Fluidics Head 노즐 스트림 동작.
        private bool _isSpitting;
        public bool IsSpitting
        {
            get => _isSpitting;
            private set => SetProperty(ref _isSpitting, value);
        }
        public ICommand SpitCommand { get; private set; } = null!;

        // ── Purge 압력 (kPa) ──────────────────────────────────────────
        // 현재값(읽기 전용 표시) / 셋팅값(입력) / 적용된 셋팅값(Set Value 시 캡처)
        private double _purgeApplied;
        private double _purgeCurrent;
        public double PurgeCurrent  { get => _purgeCurrent;  private set => SetProperty(ref _purgeCurrent, value); }
        private double _purgeSetpoint;
        public double PurgeSetpoint { get => _purgeSetpoint; set => SetProperty(ref _purgeSetpoint, value); }
        private bool _isPurgeOn;
        public bool IsPurgeOn       { get => _isPurgeOn;     private set => SetProperty(ref _isPurgeOn, value); }
        public ICommand SetPurgeCommand    { get; private set; } = null!;
        public ICommand TogglePurgeCommand { get; private set; } = null!;
        public ICommand PurgeStepUpCommand   { get; private set; } = null!;
        public ICommand PurgeStepDownCommand { get; private set; } = null!;
        private const double PurgeStep = 0.1;   // kPa

        // ── Meniscus 압력 (Pa) ────────────────────────────────────────
        private double _meniscusApplied;
        private double _meniscusCurrent;
        public double MeniscusCurrent  { get => _meniscusCurrent;  private set => SetProperty(ref _meniscusCurrent, value); }
        private double _meniscusSetpoint;
        public double MeniscusSetpoint { get => _meniscusSetpoint; set => SetProperty(ref _meniscusSetpoint, value); }
        private bool _isMeniscusOn;
        public bool IsMeniscusOn       { get => _isMeniscusOn;     private set => SetProperty(ref _isMeniscusOn, value); }
        public ICommand SetMeniscusCommand    { get; private set; } = null!;
        public ICommand ToggleMeniscusCommand { get; private set; } = null!;
        public ICommand MeniscusStepUpCommand   { get; private set; } = null!;
        public ICommand MeniscusStepDownCommand { get; private set; } = null!;
        private const double MeniscusStep = 10.0;   // Pa

        // ── 메니스커스 DMD 실장치(Modbus RTU / 시리얼 상태머신) ────────
        // 연결 성공 시 상태머신이 백그라운드 폴링/쓰기를 실제 장치로 수행, 실패 시 mock.
        // UI 는 Pa, 상태머신은 kPa → 1 kPa = 1000 Pa 환산.
        private IJPSystem.Platform.Infrastructure.Devices.Meniscus.MeniscusStateMachine? _meniscus;
        private bool _meniscusConnected;
        private bool _meniscusErrLogged;
        private const double PaPerKpa = 1000.0;

        /// <summary>VV Control 패널(Final VV/Switching Pressure/Pump + 상태 LED) 로직.</summary>
        public IJPSystem.Platform.HMI.Print.VvControlViewModel Vv { get; }

        // ── Valve L / R (Fluidics 다이어그램 토글 → 디지털 출력) ──────
        // Valve L = Y003(DO_SOL_VV_INK_1, 바렐), Valve R = Y004(DO_SOL_VV_INK_2, 주사기)
        private const string DoValveL = "DO_SOL_VV_INK_1";
        private const string DoValveR = "DO_SOL_VV_INK_2";

        private bool _isValveLOn;
        public bool IsValveLOn { get => _isValveLOn; private set => SetProperty(ref _isValveLOn, value); }
        private bool _isValveROn;
        public bool IsValveROn { get => _isValveROn; private set => SetProperty(ref _isValveROn, value); }

        public ICommand ToggleValveLCommand { get; private set; } = null!;
        public ICommand ToggleValveRCommand { get; private set; } = null!;

        // ── Barrel 액위 센서 (X100=LOW, X101=HIGH) ───────────────────
        // 디지털 입력을 주기적으로 읽어 배럴 비주얼(LevelStatus)·센서 점에 반영.
        private const string DiLevelLow  = "DI_LEVEL_LOW";   // X100
        private const string DiLevelHigh = "DI_LEVEL_HIGH";  // X101
        private System.Threading.Timer? _levelPollTimer;

        private bool _levelSensorLow;
        /// <summary>LOW 액위 센서(X100) 감지 상태.</summary>
        public bool LevelSensorLow
        {
            get => _levelSensorLow;
            private set { if (SetProperty(ref _levelSensorLow, value)) OnPropertyChanged(nameof(BarrelLevel)); }
        }

        private bool _levelSensorHigh;
        /// <summary>HIGH 액위 센서(X101) 감지 상태.</summary>
        public bool LevelSensorHigh
        {
            get => _levelSensorHigh;
            private set { if (SetProperty(ref _levelSensorHigh, value)) OnPropertyChanged(nameof(BarrelLevel)); }
        }

        /// <summary>두 센서 조합으로 배럴 액위 비주얼을 결정.
        /// HIGH 감지=가득, LOW만 감지=중간, 둘 다 미감지=부족.</summary>
        public IJPSystem.Platform.Domain.Enums.LevelStatus BarrelLevel =>
            _levelSensorHigh ? IJPSystem.Platform.Domain.Enums.LevelStatus.HH
            : _levelSensorLow ? IJPSystem.Platform.Domain.Enums.LevelStatus.Set
            : IJPSystem.Platform.Domain.Enums.LevelStatus.Low;

        // ── Voltage offset (%) : -25 ~ 25 (빨간 범위) ─────────────────
        private const double VoltageOffsetMin = -25.0;
        private const double VoltageOffsetMax =  25.0;
        private double _voltageOffset;
        public double VoltageOffset
        {
            get => _voltageOffset;
            set => SetProperty(ref _voltageOffset, Math.Clamp(value, VoltageOffsetMin, VoltageOffsetMax));
        }
        public ICommand SetVoltageCommand { get; private set; } = null!;

        // Print Velocity 입력 클램프 범위 (mm/s). 화면에 범위 안내는 표시하지 않는다.
        private const double PrintVelocityMin = 30.0;
        private const double PrintVelocityMax = 100.0;

        // 인쇄 속도를 결정하는 스캔축(스테이지 이송). AutoPrintSequence.ScanAxisNo 와 동일해야 한다.
        private const string ScanAxisTag = "Y";

        // ── 헤드팩 선택 ───────────────────────────────────────────────
        public ObservableCollection<string> HeadPacks { get; } = new()
        {
            "Head Pack 1", "Head Pack 2", "Head Pack 3", "Head Pack 4",
        };

        private string _selectedHeadPack = "Head Pack 1";
        public string SelectedHeadPack
        {
            get => _selectedHeadPack;
            set => SetProperty(ref _selectedHeadPack, value);
        }

        // ── 파라미터 ──────────────────────────────────────────────────
        private int _nOverlapNz = 30;
        public int NOverlapNz
        {
            get => _nOverlapNz;
            set => SetProperty(ref _nOverlapNz, Math.Max(0, value));
        }

        private double _widthMm = 150.0;
        public double WidthMm
        {
            get => _widthMm;
            set => SetProperty(ref _widthMm, Math.Max(0, value));
        }

        private double _lengthMm = 150.0;
        public double LengthMm
        {
            get => _lengthMm;
            set => SetProperty(ref _lengthMm, Math.Max(0, value));
        }

        private int _usingHead = 1;
        public int UsingHead
        {
            get => _usingHead;
            set => SetProperty(ref _usingHead, Math.Max(1, value));
        }

        // ── DPI / Drop Pitch ─────────────────────────────────────────
        private int _dpi = 600;
        public int Dpi
        {
            get => _dpi;
            set
            {
                if (SetProperty(ref _dpi, Math.Max(1, value)))
                    OnPropertyChanged(nameof(DropPitchMm));
            }
        }

        public double DropPitchMm => DpiConverter.DpiToPitchMm(_dpi);

        // ── 인쇄 원점 관리자 (모달 + 저장) ────────────────────────────
        // HW 무관 코어(Platform.Application). 라이브 위치는 SharedAxisList 어댑터로 넘긴다.
        private IJPSystem.Platform.Application.Printing.PrintOriginManager? _originManager;

        /// <summary>인쇄 원점 관리자 — 모달 창이 공유해 Set/Reset 한다.</summary>
        public IJPSystem.Platform.Application.Printing.PrintOriginManager OriginManager
        {
            get
            {
                if (_originManager == null)
                {
                    string cfgDir = System.IO.Path.GetDirectoryName(PathUtils.GetConfigPath("x")) ?? "";
                    _originManager = new IJPSystem.Platform.Application.Printing.PrintOriginManager(
                        new SharedAxisStageAdapter(_mainVM), cfgDir);
                    _originManager.Load();   // 저장된 원점이 있으면 복원
                    _originManager.PrintOriginChanged += (_, _) => SyncOriginFromManager();
                    SyncOriginFromManager();
                }
                return _originManager;
            }
        }

        // 관리자 → 화면 원점 필드 동기화.
        private void SyncOriginFromManager()
        {
            var p = _originManager!.PrintOrigin;
            XOrigin = p.X; YOrigin = p.Y; ZOrigin = p.Z;
            IsOriginSet = true;
        }

        // SharedAxisList 의 라이브 위치를 IStagePosition 으로 노출하는 어댑터(HW 무관 코어에 주입).
        private sealed class SharedAxisStageAdapter : IJPSystem.Platform.Application.Printing.IStagePosition
        {
            private readonly MainViewModel _vm;
            public SharedAxisStageAdapter(MainViewModel vm) => _vm = vm;

            public IJPSystem.Platform.Application.Printing.AxisPoint GetCurrentPosition()
                => new(Pos("X"), Pos("Y"), Pos("Z"));

            private double Pos(string prefix)
            {
                var ax = _vm.SharedAxisList.FirstOrDefault(a =>
                    (a.Info?.Name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                return ax?.CurrentPos ?? 0.0;
            }
        }

        // ── 원점 (Set Print Origin 버튼으로 캡처) ─────────────────────
        private double _xOrigin;
        public double XOrigin { get => _xOrigin; private set => SetProperty(ref _xOrigin, value); }

        private double _yOrigin;
        public double YOrigin { get => _yOrigin; private set => SetProperty(ref _yOrigin, value); }

        private double _zOrigin;
        public double ZOrigin { get => _zOrigin; private set => SetProperty(ref _zOrigin, value); }

        private bool _isOriginSet = true;
        public bool IsOriginSet
        {
            get => _isOriginSet;
            private set
            {
                if (SetProperty(ref _isOriginSet, value))
                    (PrintCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        // ── Pattern Print 시퀀스 실행 상태 ────────────────────────────
        private System.Threading.CancellationTokenSource? _printCts;
        private bool _isPrinting;
        public bool IsPrinting
        {
            get => _isPrinting;
            private set
            {
                if (SetProperty(ref _isPrinting, value))
                {
                    (PrintCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (AbortCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        // ── 인쇄 진행 상태 표시(Status) ───────────────────────────────
        public enum PrintState { Idle, Running, Done, Stopped, Failed }

        private PrintState _statusState = PrintState.Idle;
        /// <summary>현재 인쇄 상태(Idle/Running/Done/Stopped/Failed). UI 색상/뱃지 바인딩.</summary>
        public PrintState StatusState
        {
            get => _statusState;
            private set
            {
                if (SetProperty(ref _statusState, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBrush));
                }
            }
        }

        /// <summary>상태 뱃지 텍스트(READY/PRINTING/DONE/STOPPED/FAILED).</summary>
        public string StatusText => _statusState switch
        {
            PrintState.Running => "PRINTING",
            PrintState.Done    => "DONE",
            PrintState.Stopped => "STOPPED",
            PrintState.Failed  => "FAILED",
            _                  => "READY",
        };

        /// <summary>상태 색상(녹/회/적).</summary>
        public System.Windows.Media.Brush StatusBrush => _statusState switch
        {
            PrintState.Running => MakeBrush("#10B981"),
            PrintState.Done    => MakeBrush("#22C55E"),
            PrintState.Stopped => MakeBrush("#F59E0B"),
            PrintState.Failed  => MakeBrush("#EF4444"),
            _                  => MakeBrush("#64748B"),
        };

        private static System.Windows.Media.Brush MakeBrush(string hex)
        {
            var b = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private string _statusMessage = "대기 중";
        /// <summary>현재 진행 단계 설명(번역된 step 이름 또는 결과 메시지).</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        private int _currentStep;
        /// <summary>현재 단계 번호(1-base, 미실행 시 0).</summary>
        public int CurrentStep
        {
            get => _currentStep;
            private set { if (SetProperty(ref _currentStep, value)) OnPropertyChanged(nameof(StepLabel)); }
        }

        private int _totalSteps;
        /// <summary>전체 단계 수.</summary>
        public int TotalSteps
        {
            get => _totalSteps;
            private set
            {
                if (SetProperty(ref _totalSteps, value))
                {
                    OnPropertyChanged(nameof(StepLabel));
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
        }

        /// <summary>"3 / 17" 형태의 단계 라벨.</summary>
        public string StepLabel => _totalSteps > 0 ? $"{_currentStep} / {_totalSteps}" : "-";

        /// <summary>진행률 0~100(%).</summary>
        public double ProgressPercent =>
            _totalSteps > 0 ? (double)_currentStep / _totalSteps * 100.0 : 0;

        // ── Print Velocity (활성 레시피의 스캔축(Y) Print.Vel) ──────────
        private double _printVelocity;
        public double PrintVelocity
        {
            get => _printVelocity;
            set => SetProperty(ref _printVelocity, Math.Clamp(value, PrintVelocityMin, PrintVelocityMax));
        }

        // ── Print data Path (DXF Rasterizer 창에 초기 경로로 전달) ────
        private string _printDataPath = "";
        /// <summary>Print data Path 입력창 (인쇄할 이미지/DXF 파일 경로).</summary>
        public string PrintDataPath
        {
            get => _printDataPath;
            set => SetProperty(ref _printDataPath, value);
        }

        // ── Load Image (Visual Monitor 에 띄울 이미지) ────────────────
        private string _loadedImagePath = "";
        /// <summary>불러온 이미지의 전체 경로. 비어 있으면 아직 불러오지 않은 상태.</summary>
        public string LoadedImagePath
        {
            get => _loadedImagePath;
            private set
            {
                if (!SetProperty(ref _loadedImagePath, value)) return;
                OnPropertyChanged(nameof(LoadedImageName));
                OnPropertyChanged(nameof(HasLoadedImage));
            }
        }
        /// <summary>불러온 이미지의 파일명(버튼 아래 표시용).</summary>
        public string LoadedImageName =>
            string.IsNullOrEmpty(_loadedImagePath) ? "" : System.IO.Path.GetFileName(_loadedImagePath);
        public bool HasLoadedImage => !string.IsNullOrEmpty(_loadedImagePath);

        // ── Commands ─────────────────────────────────────────────────
        public ICommand PrintCommand          { get; }
        public ICommand AbortCommand          { get; }
        public ICommand LoadImageCommand      { get; private set; } = null!;

        /// <summary>
        /// 화면 상단 Visual Monitor(라이브 뷰 + 크로스라인 + 뷰 툴). 공용 VisualMonitorViewModel 을 재사용한다.
        /// 기본 소스는 드랍와쳐. 라이브 시작/정지는 VisualMonitorView 의 Loaded/Unloaded 가 제어한다.
        /// </summary>
        public VisualMonitorViewModel Monitor { get; }

        public PatternPrintViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            Monitor = new VisualMonitorViewModel(mainVM, "Drop");   // 드랍와쳐 기본

            PrintCommand          = new RelayCommand(async _ => await RunPatternPrintAsync(),
                                                     _ => IsOriginSet && !IsPrinting);
            AbortCommand          = new RelayCommand(_ => _printCts?.Cancel(), _ => IsPrinting);

            // Motion 패널 (활성화는 버튼 IsEnabled="SelectedMotionAxis.CanMove" 바인딩으로 처리)
            MotionMoveCommand     = new RelayCommand(async _ => await MotionMoveAsync());
            MotionStepUpCommand   = new RelayCommand(_ => MotionTarget = Math.Round(MotionTarget + MotionStep, 3));
            MotionStepDownCommand = new RelayCommand(_ => MotionTarget = Math.Round(MotionTarget - MotionStep, 3));

            MoveReadyCommand      = new RelayCommand(async _ => await MoveToPointAsync(PointNames.Ready),
                                                     _ => !IsPointMoving && !IsPrinting);
            MovePrintStartCommand = new RelayCommand(async _ => await MoveToPointAsync(PointNames.PrintStart),
                                                     _ => !IsPointMoving && !IsPrinting);
            MovePrintEndCommand   = new RelayCommand(async _ => await MoveToPointAsync(PointNames.PrintEnd),
                                                     _ => !IsPointMoving && !IsPrinting);
            MovePurgeCommand      = new RelayCommand(async _ => await MoveToPointAsync(PointNames.Purge),
                                                     _ => !IsPointMoving && !IsPrinting);

            SpitCommand = new RelayCommand(_ => ToggleSpit());

            LoadImageCommand = new RelayCommand(_ => ExecuteLoadImage());

            // Purge 압력
            SetPurgeCommand    = new RelayCommand(_ => ApplyPurgeSetpoint());
            TogglePurgeCommand = new RelayCommand(_ => TogglePurge());
            PurgeStepUpCommand   = new RelayCommand(_ => PurgeSetpoint = Math.Round(PurgeSetpoint + PurgeStep, 3));
            PurgeStepDownCommand = new RelayCommand(_ => PurgeSetpoint = Math.Round(PurgeSetpoint - PurgeStep, 3));
            // Meniscus 압력
            SetMeniscusCommand    = new RelayCommand(_ => ApplyMeniscusSetpoint());
            ToggleMeniscusCommand = new RelayCommand(_ => ToggleMeniscus());
            MeniscusStepUpCommand   = new RelayCommand(_ => MeniscusSetpoint = Math.Round(MeniscusSetpoint + MeniscusStep, 0));
            MeniscusStepDownCommand = new RelayCommand(_ => MeniscusSetpoint = Math.Round(MeniscusSetpoint - MeniscusStep, 0));

            // Valve L/R (디지털 출력 토글)
            ToggleValveLCommand = new RelayCommand(_ => ToggleValve(DoValveL, !IsValveLOn, v => IsValveLOn = v, "Valve L"));
            ToggleValveRCommand = new RelayCommand(_ => ToggleValve(DoValveR, !IsValveROn, v => IsValveROn = v, "Valve R"));

            SetVoltageCommand = new RelayCommand(_ =>
                _mainVM.AddLog($"[PRINT] Voltage offset = {VoltageOffset:F2} % (범위 {VoltageOffsetMin}~{VoltageOffsetMax})", LogLevel.Info));

            RefreshPrintVelocity();
            RefreshValveStates();
            InitMeniscusDevice();

            // VV Control 패널(Final VV / Switching Pressure / Pump + 상태 LED) — 머신 IO 지연 바인딩.
            // MPC AL = DMD 상태머신 알람(현재는 HasError 로 대응, 전용 알람 레지스터 확정 시 교체),
            // 압력 = DMD 실측(kPa). 상태머신 미연결이면 각각 off / 0.
            Vv = new IJPSystem.Platform.HMI.Print.VvControlViewModel(
                     () => _mainVM.GetController()?.GetMachine()?.IO,
                     msg => _mainVM.AddLog(msg, LogLevel.Info),
                     mpcAlarmProvider: () => _meniscus?.State?.HasError ?? false,
                     pressureProvider: () => _meniscus?.State?.Pressure ?? 0.0);

            // 배럴 액위 센서(X100/X101) 주기 폴링 시작 (300ms)
            _levelPollTimer = new System.Threading.Timer(_ => PollLevelSensors(), null, 0, 300);
        }

        // ── Valve L / R 출력 제어 ─────────────────────────────────────
        /// <summary>지정 밸브 출력(Y100/Y101)을 on/off 하고 UI 상태를 갱신.</summary>
        private void ToggleValve(string index, bool on, Action<bool> setState, string label)
        {
            var io = _mainVM.GetController()?.GetMachine()?.IO;
            if (io == null)
            {
                _mainVM.AddLog($"[VALVE] {label} — IO 미연결", LogLevel.Warning);
                return;
            }
            try
            {
                io.SetOutput(index, on);
                setState(on);
                _mainVM.AddLog($"[VALVE] {label} ({index}) {(on ? "ON" : "OFF")}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[VALVE] {label} 출력 실패: {ex.Message}", LogLevel.Warning);
            }
        }

        /// <summary>현재 출력 상태를 읽어 밸브 토글 표시를 초기화(IO 연결 시).</summary>
        private void RefreshValveStates()
        {
            var io = _mainVM.GetController()?.GetMachine()?.IO;
            if (io == null) return;
            try
            {
                IsValveLOn = io.GetOutput(DoValveL);
                IsValveROn = io.GetOutput(DoValveR);
            }
            catch { /* IO 미연결/미초기화 시 무시 */ }
        }

        // ── Barrel 액위 센서 폴링 ─────────────────────────────────────
        /// <summary>X100/X101 디지털 입력을 주기적으로 읽어 배럴 액위 비주얼에 반영.</summary>
        private void PollLevelSensors()
        {
            var io = _mainVM.GetController()?.GetMachine()?.IO;
            if (io == null) return;
            bool low, high;
            try
            {
                low  = io.GetInput(DiLevelLow);
                high = io.GetInput(DiLevelHigh);
            }
            catch { return; }   // IO 미연결/미초기화 시 무시

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LevelSensorLow  = low;
                LevelSensorHigh = high;
            });
        }

        /// <summary>Spit — 클릭할 때마다 노즐 토출 애니메이션을 ON/OFF 토글한다.</summary>
        private void ToggleSpit()
        {
            IsSpitting = !IsSpitting;
            _mainVM.AddLog($"[PATTERN] Spit {(IsSpitting ? "ON" : "OFF")}", LogLevel.Info);
        }

        // ── Purge 압력 ───────────────────────────────────────────────
        /// <summary>Set Value — 입력한 셋팅값을 Purge 압력 명령으로 적용. 출력 ON 상태면 현재값에 즉시 반영.</summary>
        private void ApplyPurgeSetpoint()
        {
            _purgeApplied = PurgeSetpoint;
            if (IsPurgeOn) PurgeCurrent = _purgeApplied;
            _mainVM.AddLog($"[PRESSURE] Purge setpoint = {PurgeSetpoint:F3} kPa", LogLevel.Info);
        }
        /// <summary>Toggle Purge — 출력 ON/OFF. ON이면 적용된 셋팅값, OFF면 0 을 현재값으로.</summary>
        private void TogglePurge()
        {
            IsPurgeOn = !IsPurgeOn;
            PurgeCurrent = IsPurgeOn ? _purgeApplied : 0.0;
            _mainVM.AddLog($"[PRESSURE] Purge {(IsPurgeOn ? "ON" : "OFF")}", LogLevel.Info);
        }

        // ── Meniscus 압력 (실장치 연동 + mock 폴백) ───────────────────
        /// <summary>Set Value — 셋팅값을 Meniscus 압력 명령으로 적용. 연결 시 DMD에 쓰기, 미연결 시 mock.</summary>
        private void ApplyMeniscusSetpoint()
        {
            _meniscusApplied = MeniscusSetpoint;

            if (_meniscusConnected && _meniscus != null)
            {
                double kpa = MeniscusSetpoint / PaPerKpa;
                var sm = _meniscus;
                System.Threading.Tasks.Task.Run(() => sm.SetPressure(kpa));
                _mainVM.AddLog($"[MENISCUS] setpoint = {MeniscusSetpoint:F0} Pa ({kpa:F3} kPa)", LogLevel.Info);
                // 현재값은 상태머신 폴링(StateChanged)이 실제 측정값으로 갱신
            }
            else
            {
                if (IsMeniscusOn) MeniscusCurrent = _meniscusApplied;
                _mainVM.AddLog($"[PRESSURE] Meniscus setpoint = {MeniscusSetpoint:F0} Pa (mock)", LogLevel.Info);
            }
        }
        /// <summary>Toggle Meniscus — 출력 ON/OFF. 연결 시 제어 레지스터 쓰기, 미연결 시 mock.</summary>
        private void ToggleMeniscus()
        {
            IsMeniscusOn = !IsMeniscusOn;

            if (_meniscusConnected && _meniscus != null)
            {
                bool on = IsMeniscusOn;
                var sm = _meniscus;
                System.Threading.Tasks.Task.Run(() => sm.SetControl(on));
                _mainVM.AddLog($"[MENISCUS] {(on ? "ON" : "OFF")}", LogLevel.Info);
            }
            else
            {
                MeniscusCurrent = IsMeniscusOn ? _meniscusApplied : 0.0;
                _mainVM.AddLog($"[PRESSURE] Meniscus {(IsMeniscusOn ? "ON" : "OFF")} (mock)", LogLevel.Info);
            }
        }

        // ── 메니스커스 장치 연결 / 폴링(상태머신) ─────────────────────
        /// <summary>설정(AppConfig)에 따라 DMD Modbus RTU 상태머신을 백그라운드로 연결·폴링 시작.</summary>
        private void InitMeniscusDevice()
        {
            var cfg = IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Current;
            if (cfg == null || !cfg.MeniscusEnabled) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var dmdCfg = new IJPSystem.Platform.Infrastructure.Devices.Meniscus.DmdConfig
                    {
                        ComPort  = cfg.MeniscusComPort,
                        BaudRate = cfg.MeniscusBaudRate,
                        UnitId   = cfg.MeniscusUnitId
                    };
                    var sm = new IJPSystem.Platform.Infrastructure.Devices.Meniscus.MeniscusStateMachine(dmdCfg);
                    sm.StateChanged += OnMeniscusStateChanged;
                    _meniscus = sm;

                    sm.Init();                       // 시리얼 Modbus 연결
                    if (sm.State.Connected)
                    {
                        _mainVM.AddLog($"[MENISCUS] DMD 연결됨 — {cfg.MeniscusComPort} @ {cfg.MeniscusBaudRate}", LogLevel.Info);
                        sm.StartRead();              // 백그라운드 압력 폴링
                    }
                    else
                    {
                        _mainVM.AddLog($"[MENISCUS] DMD 미연결(mock) — {sm.State.ErrorMessage}", LogLevel.Warning);
                    }
                }
                catch (Exception ex)
                {
                    _meniscusConnected = false;
                    _mainVM.AddLog($"[MENISCUS] 연결 실패(mock 전환): {ex.Message}", LogLevel.Warning);
                }
            });
        }

        /// <summary>상태머신 상태 변경 알림 → 연결 플래그 갱신 + 현재 압력(Pa) UI 반영.</summary>
        private void OnMeniscusStateChanged(IJPSystem.Platform.Infrastructure.Devices.Meniscus.DmdState st)
        {
            _meniscusConnected = st.Connected && !st.HasError;

            // 에러는 발생 전이(edge)에서만 1회 로깅(폴링 스팸 방지)
            if (st.HasError && !_meniscusErrLogged)
            {
                _meniscusErrLogged = true;
                _mainVM.AddLog($"[MENISCUS] {st.ErrorMessage}", LogLevel.Warning);
            }
            else if (!st.HasError)
            {
                _meniscusErrLogged = false;
            }

            if (st.Connected && !st.HasError)
            {
                double pa = st.Pressure * PaPerKpa;
                System.Windows.Application.Current?.Dispatcher.Invoke(() => MeniscusCurrent = pa);
            }
        }

        /// <summary>
        /// 활성 레시피에 티칭된 포인트(READY/PRINT START/PRINT END/PURGE) 좌표로 이동한다.
        /// 좌표는 RecipeVM 의 활성 스냅샷에서 오므로, 레시피를 APPLY 하지 않았으면 이동할 축이 없다
        /// (MotionServiceAdapter 가 그 상황을 경고 로그로 남긴다).
        /// </summary>
        private async System.Threading.Tasks.Task MoveToPointAsync(string pointName)
        {
            if (_mainVM.HasActiveAlarm)
            {
                _mainVM.AddLog($"[PATTERN] {pointName} 이동 — 중단 (미해제 알람 존재)", LogLevel.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_mainVM.RecipeVM?.ActiveRecipeName))
            {
                _mainVM.AddLog($"[PATTERN] {pointName} 이동 — 중단 (적용된 레시피 없음)", LogLevel.Warning);
                return;
            }

            // 미원점 상태의 절대좌표 이동은 위험 — 인쇄 시작과 동일한 기준으로 막는다.
            var allAxes = _mainVM.GetController()?.GetMachine()?.Motion?.GetAllStatus();
            if (allAxes == null || allAxes.Count == 0)
            {
                _mainVM.AddLog($"[PATTERN] {pointName} 이동 — 중단 (축 정보 없음 — 모션 드라이버 확인)", LogLevel.Error);
                return;
            }
            var notHomed = allAxes.Where(a => !a.IsHomeDone).Select(a => a.AxisNo).ToList();
            if (notHomed.Count > 0)
            {
                _mainVM.AddLog(
                    $"[PATTERN] {pointName} 이동 — 중단 (INITIALIZE 미수행, 미원점 축: {string.Join(", ", notHomed)})",
                    LogLevel.Warning);
                return;
            }

            IsPointMoving = true;
            try
            {
                var motion = new MotionServiceAdapter(_mainVM);
                await motion.MoveToPointAsync(pointName, System.Threading.CancellationToken.None);
                _mainVM.AddLog($"[PATTERN] {pointName} 이동 완료", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[PATTERN] {pointName} 이동 실패 — {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsPointMoving = false;
            }
        }

        /// <summary>선택 축을 Absolute/Relative 모드로 MotionTarget 위치까지 이동.</summary>
        private async System.Threading.Tasks.Task MotionMoveAsync()
        {
            var ax = SelectedMotionAxis;
            if (ax == null)
            {
                _mainVM.AddLog("[PATTERN] Move — 대상 축을 찾을 수 없습니다.", LogLevel.Warning);
                return;
            }
            ax.IsAbsMode      = (_motionModeIndex == 0); // 0=Absolute, 1=Relative
            ax.TargetPosition = MotionTarget;
            await ax.MoveAsync();
        }

        /// <summary>
        /// Load Image — 이미지 파일을 골라 화면 상단 Visual Monitor 에 표시한다.
        /// 로드에 실패하면 경로 표시를 갱신하지 않아 화면과 라벨이 어긋나지 않게 한다.
        /// </summary>
        private void ExecuteLoadImage()
        {
            // 직전에 고른 이미지 폴더 → 없으면 그림 폴더
            string defaultDir = "";
            if (!string.IsNullOrEmpty(_loadedImagePath))
                defaultDir = System.IO.Path.GetDirectoryName(_loadedImagePath) ?? "";
            if (!System.IO.Directory.Exists(defaultDir))
                defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title            = "인쇄 이미지 선택",
                Filter           = "이미지 파일|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|모든 파일|*.*",
                InitialDirectory = defaultDir,
                Multiselect      = false,
            };
            if (dlg.ShowDialog() != true) return;

            if (Monitor.LoadImageFromFile(dlg.FileName))
                LoadedImagePath = dlg.FileName;
        }

        // 인쇄 원점 캡처는 PrintOriginWindow 모달 + PrintOriginManager 로 이관됨
        // (현재 위치 실시간 표시 + Set/Reset + 저장). SharedAxisStageAdapter 가 라이브 위치를 제공한다.

        /// <summary>
        /// 활성(APPLY된) 레시피의 스캔축 Printing 프로파일 속도를 화면에 반영한다.
        /// 인쇄 속도를 결정하는 건 스캔축(Y, 스테이지 이송)이다 — X는 스와스 스텝오버 축이라 무관.
        /// (AutoPrintSequence.ScanAxisNo 와 반드시 같은 축이어야 함)
        ///
        /// 뷰모델이 캐시되어 생성자에서 한 번만 읽으면 레시피를 바꿔 APPLY 해도 옛 값이 남으므로,
        /// 화면 진입(Loaded) 때마다 다시 호출한다.
        /// </summary>
        public void RefreshPrintVelocity()
        {
            var scanAxis = _mainVM.SharedAxisList.FirstOrDefault(a =>
                (a.Info?.Name ?? "").StartsWith(ScanAxisTag, StringComparison.OrdinalIgnoreCase));
            var cfg = scanAxis == null ? null : _mainVM.RecipeVM?.GetActiveMotionConfig(scanAxis.Info.AxisNo);

            // 활성 스냅샷에 스캔축 Printing 프로파일이 없으면(레시피 미적용 등) 범위 상한으로 표시
            PrintVelocity = cfg?.Printing?.Velocity ?? PrintVelocityMax;
        }

        /// <summary>
        /// Print 버튼 → Pattern Print 시퀀스(PatternPrintSequence) 실행.
        /// 사전조건(머신 초기화·적용 레시피·알람 없음·원점복귀) 확인 후 단계별 실행.
        /// Abort(=_printCts.Cancel)로 중단 가능.
        /// </summary>
        private async System.Threading.Tasks.Task RunPatternPrintAsync()
        {
            if (IsPrinting) return;

            var machine = _mainVM.GetController()?.GetMachine();
            if (machine == null)
            {
                _mainVM.AddLog("[SEQ] PATTERN PRINT — 중단 (머신 미초기화)", LogLevel.Warning);
                Dialogs.Show("머신이 초기화되지 않았습니다.\n\n시스템 초기화 완료 후 다시 시도하세요.",
                    T("Log_PrereqDialogTitle"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            // 시퀀스는 활성 레시피의 티칭 포인트(PRINT START/END 등)를 참조
            if (string.IsNullOrEmpty(_mainVM.RecipeVM?.ActiveRecipeName))
            {
                _mainVM.AddLog("[SEQ] PATTERN PRINT — 중단 (적용된 레시피 없음)", LogLevel.Warning);
                return;
            }
            if (_mainVM.HasActiveAlarm)
            {
                _mainVM.AddLog("[SEQ] PATTERN PRINT — 중단 (미해제 알람 존재)", LogLevel.Warning);
                return;
            }
            var allAxes = machine.Motion?.GetAllStatus();
            if (allAxes == null || allAxes.Count == 0)
            {
                _mainVM.AddLog("[SEQ] PATTERN PRINT — 중단 (축 정보 없음 — 모션 드라이버 확인)", LogLevel.Error);
                return;
            }
            var notHomed = allAxes.Where(a => !a.IsHomeDone).Select(a => a.AxisNo).ToList();
            if (notHomed.Count > 0)
            {
                _mainVM.AddLog($"[SEQ] PATTERN PRINT — 중단 (INITIALIZE 미수행, 미원점 축: {string.Join(", ", notHomed)})", LogLevel.Warning);
                // 오토런(MainDashboard.CheckPrerequisites)과 동일한 안내 팝업.
                Dialogs.Show(T("Log_PrereqNotHomed", string.Join(", ", notHomed)),
                    T("Log_PrereqDialogTitle"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            IsPrinting = true;
            _mainVM.SetSequenceRunning(true);   // 실행 중 화면 전환 차단

            var motion = new MotionServiceAdapter(_mainVM);
            var steps  = PatternPrintSequence.Build(machine, motion);
            _printCts  = new System.Threading.CancellationTokenSource();
            var token  = _printCts.Token;

            // 진행 상태 초기화
            TotalSteps    = steps.Count;
            CurrentStep   = 0;
            StatusState   = PrintState.Running;
            StatusMessage = "인쇄 준비 중…";

            _mainVM.AddLog(
                $"[SEQ] PATTERN PRINT — 시작 ({steps.Count} 단계, {SelectedHeadPack}, " +
                $"W={WidthMm:F1}×L={LengthMm:F1}mm, {Dpi}dpi)", LogLevel.Info);

            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var step = steps[i];

                    // 상태 업데이트 (step.Name 은 번역 키 → Loc.T 로 번역)
                    CurrentStep   = i + 1;
                    StatusMessage = IJPSystem.Platform.HMI.Common.Loc.T(step.Name);
                    OnPropertyChanged(nameof(ProgressPercent));

                    await SequenceStepLogger.RunAsync(step, "PatternPrint", token, _mainVM.AddLog);
                }
                CurrentStep   = steps.Count;
                OnPropertyChanged(nameof(ProgressPercent));
                StatusState   = PrintState.Done;
                StatusMessage = "인쇄 완료";
                _mainVM.AddLog("[SEQ] PATTERN PRINT — 완료", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                StatusState   = PrintState.Stopped;
                StatusMessage = "사용자 STOP 으로 중단됨";
                _mainVM.AddLog("[SEQ] PATTERN PRINT — 사용자 STOP 으로 중단됨", LogLevel.Warning);
            }
            catch (TimeoutException ex)
            {
                StatusState   = PrintState.Failed;
                StatusMessage = $"타임아웃: {ex.Message}";
                _mainVM.AddLog($"[SEQ] PATTERN PRINT — 타임아웃: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM?.RaiseAlarm("SEQ-MOTION-TIMEOUT");
            }
            catch (Exception ex)
            {
                StatusState   = PrintState.Failed;
                StatusMessage = $"실패: {ex.Message}";
                _mainVM.AddLog($"[SEQ] PATTERN PRINT — 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM?.RaiseAlarm("SEQ-STEP-FAIL");
            }
            finally
            {
                _printCts?.Dispose();
                _printCts = null;
                IsPrinting = false;
                _mainVM.SetSequenceRunning(false);
            }
        }
    }
}
