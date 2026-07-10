using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.Infrastructure.Config;
using static IJPSystem.Platform.HMI.Common.Loc;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class MainDashboardViewModel : ViewModelBase
    {
        private double _tactTime;
        private string _currentStepName = "IDLE";
        private double _processProgress;
        private DispatcherTimer _procTimer;
        private readonly Action<string, LogLevel> _logAction;
        private readonly Action<bool> _onAlarmChanged;
        private readonly Action<string>? _raiseAlarm;
        // (pointName, axisName) → mm — 활성 레시피의 스캔축 티칭 좌표 조회용
        private readonly Func<string, string, double?>? _getPointAxisMm;
        private readonly Func<bool>? _hasActiveAlarm;
        // 활성 레시피의 프린팅수(Swath) / 헤드길이 — 오토프린트 시퀀스 생성용
        private readonly Func<int>? _getSwathCount;
        private readonly Func<double>? _getHeadLength;

        // 프린팅 스캔(스테이지 이송) 축. 실장 구조: 헤드(X축)는 고정, 스테이지(Y축)가 이동하며 인쇄한다.
        // 메인 대시보드 애니메이션은 이 축의 모터 위치·티칭 좌표로 스테이지 이동/인쇄 진행을 구동한다.
        private const string ScanAxis = "Y";

        private readonly IMachine _machine;
        private readonly IMotionService _motion;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _stepCts;   // 스텝 단위 취소 (일시정지 시 사용)

        // 초기화(INITIALIZE) 수행에 의한 오토런 리셋 진행 플래그.
        // 진행 중이던 런을 취소한 뒤, 런의 finally 에서 ABORTED 대신 IDLE 초기상태로 정리하기 위함.
        private bool _resettingForInit;

        public ObservableCollection<SequenceStep> Steps { get; } = new();

        // 시퀀스 시작 시 캐싱되는 스캔축(ScanAxis) 티칭 좌표. View 가 GetLiveScanMm() 로
        // 스테이지 픽셀 위치·인쇄 진행률을 매핑할 때 사용:
        //   scan ∈ [PrintStart, PrintEnd] → 진행률 0..1 (스테이지가 고정 헤드 밑을 통과)
        public double ReadyScanMm      { get; private set; } = double.NaN;
        public double PrintStartScanMm { get; private set; } = double.NaN;
        public double PrintEndScanMm   { get; private set; } = double.NaN;
        public bool   HasPrintRange => !double.IsNaN(PrintStartScanMm)
                                    && !double.IsNaN(PrintEndScanMm)
                                    && Math.Abs(PrintEndScanMm - PrintStartScanMm) > 0.001;
        public bool   HasReadyMapping => !double.IsNaN(ReadyScanMm)
                                      && !double.IsNaN(PrintStartScanMm)
                                      && Math.Abs(PrintStartScanMm - ReadyScanMm) > 0.001;

        // View 60fps 프레임 타이머용 — 100ms 주기 캐시 대신 매 호출 스캔축(이송축) 실측치 반환
        public double GetLiveScanMm() => _machine.Motion?.GetActualPosition(ScanAxis) ?? 0.0;

        private void CachePrintRange()
        {
            ReadyScanMm      = _getPointAxisMm?.Invoke(PointNames.Ready,      ScanAxis) ?? double.NaN;
            PrintStartScanMm = _getPointAxisMm?.Invoke(PointNames.PrintStart, ScanAxis) ?? double.NaN;
            PrintEndScanMm   = _getPointAxisMm?.Invoke(PointNames.PrintEnd,   ScanAxis) ?? double.NaN;
            OnPropertyChanged(nameof(ReadyScanMm));
            OnPropertyChanged(nameof(PrintStartScanMm));
            OnPropertyChanged(nameof(PrintEndScanMm));
            OnPropertyChanged(nameof(HasPrintRange));
            OnPropertyChanged(nameof(HasReadyMapping));
        }

        // 일시정지 게이트 — OCE 없이 폴링으로 대기. 알람과 STOP 의미가 다르다:
        //   알람: 모터 즉시 정지 + 진행 step 취소 → 재개 시 같은 step 재실행
        //   STOP: 현재 step 은 끝까지 완료, 다음 step 진입 전 정지 → 재개 시 다음 step 부터
        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            private set
            {
                if (SetProperty(ref _isPaused, value))
                {
                    // 정지 상태든 일시정지 상태든 START 로 활성화되어야 하므로 둘 다 재평가
                    (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (StopCommand  as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        // MainViewModel 이 AlarmVM.HasActiveAlarm 변경 시 호출
        public void OnAlarmActiveChanged(bool isAlarmActive)
        {
            if (!IsRunning) return;

            if (isAlarmActive && !IsPaused)
            {
                IsPaused = true;
                StopAllMotion();
                // 진행 중 step 의 await 를 즉시 깨움 → 외부 루프가 게이트에서 대기 후 같은 step 재시도
                _stepCts?.Cancel();
                _logAction?.Invoke(T("Log_AutoPrintAlarmPause"), LogLevel.Warning);
            }
            else if (!isAlarmActive && IsPaused)
            {
                IsPaused = false;
                _logAction?.Invoke(T("Log_AutoPrintAlarmResume"), LogLevel.Info);
            }
        }

        private void StopAllMotion()
        {
            try
            {
                var allAxes = _machine.Motion?.GetAllStatus();
                if (allAxes == null) return;
                foreach (var ax in allAxes)
                    _ = _machine.Motion!.Stop(ax.AxisNo);
            }
            catch (Exception ex)
            {
                _logAction?.Invoke(T("Log_AutoPrintStopMotionError", ex.Message), LogLevel.Error);
            }
        }

        private string _selectedRecipeName = "None";
        public string SelectedRecipe
        {
            get => _selectedRecipeName;
            set => SetProperty(ref _selectedRecipeName, value);
        }
        
        #region Properties
        private int _totalCount;
        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        // 활성 레시피의 프린팅수(Swath) — 메인화면 표시용. 센서 폴링에서 갱신.
        private int _swathCount = 1;
        public int SwathCount
        {
            get => _swathCount;
            set => SetProperty(ref _swathCount, value);
        }

        public double TactTime
        {
            get => _tactTime;
            set => SetProperty(ref _tactTime, value);
        }

        public string CurrentStepName
        {
            get => _currentStepName;
            set => SetProperty(ref _currentStepName, value);
        }

        public double ProcessProgress
        {
            get => _processProgress;
            set => SetProperty(ref _processProgress, value);
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set => SetProperty(ref _isError, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (StopCommand  as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }
        private string _activeRecipeName = string.Empty;
        public string ActiveRecipeName
        {
            get => _activeRecipeName;
            set => SetProperty(ref _activeRecipeName, value);
        }

        // 연속 운전 모드(UI 토글, 단일 소스). 런 루프가 매 사이클 이 값을 실시간으로 읽으므로
        // 단일/연속 운전 중에 토글을 바꾸면 다음 사이클부터 즉시 반영된다.
        private bool _isContinuousMode;
        public bool IsContinuousMode
        {
            get => _isContinuousMode;
            set => SetProperty(ref _isContinuousMode, value);
        }

        #endregion

        #region Commands
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ResetCommand { get; }

        public ICommand OpenDoorCommand { get; }
        public ICommand CloseDoorCommand { get; }
        public ICommand VacuumOnCommand { get; }
        public ICommand VacuumOffCommand { get; }
        #endregion

        #region View 동기화 이벤트
        // View 가 시각 애니메이션을 시퀀스 사이클에 동기화하기 위해 구독.
        // Started → 매 스텝마다 StepChanged → 종료 시 Completed 또는 Aborted.
        public event Action? AutoPrintStarted;
        public event Action? AutoPrintAborted;
        public event Action? AutoPrintCompleted;
        public event Action<int>? AutoPrintStepChanged;
        #endregion

        #region 센서 / 모터 상태

        private bool _isDoorLocked;
        public bool IsDoorLocked
        {
            get => _isDoorLocked;
            set => SetProperty(ref _isDoorLocked, value);
        }

        private bool _isVacuumOn;
        public bool IsVacuumOn
        {
            get => _isVacuumOn;
            set => SetProperty(ref _isVacuumOn, value);
        }

        private bool _isGlassDetected;
        public bool IsGlassDetected
        {
            get => _isGlassDetected;
            set => SetProperty(ref _isGlassDetected, value);
        }

        private bool _isEmoActive;
        public bool IsEmoActive
        {
            get => _isEmoActive;
            set => SetProperty(ref _isEmoActive, value);
        }

        private double _motorXPosition;
        public double MotorXPosition
        {
            get => _motorXPosition;
            set => SetProperty(ref _motorXPosition, value);
        }

        private double _motorYPosition;
        public double MotorYPosition
        {
            get => _motorYPosition;
            set => SetProperty(ref _motorYPosition, value);
        }

        private double _motorZPosition;
        public double MotorZPosition
        {
            get => _motorZPosition;
            set => SetProperty(ref _motorZPosition, value);
        }

        private double _motorQPosition;
        public double MotorQPosition
        {
            get => _motorQPosition;
            set => SetProperty(ref _motorQPosition, value);
        }
        #endregion

        public MainDashboardViewModel(
            Action<string, LogLevel> logAction,
            Action<bool> onAlarmChanged,
            IMachine machine,
            string initialActiveRecipe,
            IMotionService motion,
            Action<string>? raiseAlarm = null,
            Func<string, string, double?>? getPointAxisMm = null,
            Func<bool>? hasActiveAlarm = null,
            Func<int>? getSwathCount = null,
            Func<double>? getHeadLength = null)
        {
            _logAction       = logAction;
            _onAlarmChanged  = onAlarmChanged;
            _raiseAlarm      = raiseAlarm;
            _getPointAxisMm  = getPointAxisMm;
            _hasActiveAlarm  = hasActiveAlarm;
            _getSwathCount   = getSwathCount;
            _getHeadLength   = getHeadLength;
            _machine = machine;
            _motion = motion;

            ActiveRecipeName = initialActiveRecipe;

            // 시작 — 정지 상태면 시퀀스 시작, 일시정지 상태면 재개.
            // 연속 여부는 IsContinuousMode 토글로 결정(별도 연속 버튼 없음).
            StartCommand = new RelayCommand(async _ =>
            {
                if (IsRunning && IsPaused)
                {
                    IsPaused = false;
                    _logAction?.Invoke(T("Log_AutoPrintResume"), LogLevel.Info);
                    return;
                }
                if (!IsRunning)
                {
                    // 반복 여부는 런 루프가 IsContinuousMode 를 실시간으로 읽어 결정
                    if (IsContinuousMode)
                        _logAction?.Invoke(T("Log_AutoPrintContinuousStart"), LogLevel.Info);
                    await RunAutoPrintAsync();
                }
            }, _ => !IsRunning || IsPaused);

            // STOP 은 취소가 아니라 일시정지 — 현재 step 끝까지 마무리 후 다음 step 진입 전 멈춤. 재시작은 START.
            // 연속 토글도 함께 끔 → 재개 시 현재 사이클까지만 마치고 반복 종료(연속 취소를 시각적으로도 반영).
            StopCommand = new RelayCommand(_ =>
            {
                if (IsRunning && !IsPaused)
                {
                    IsContinuousMode = false;      // 연속 반복 중지(토글 OFF)
                    IsPaused = true;
                    _logAction?.Invoke(T("Log_AutoPrintStopPause"), LogLevel.Warning);
                }
            }, _ => IsRunning && !IsPaused);

            ResetCommand = new RelayCommand(_ =>
            {
                IsError = false;
                _onAlarmChanged?.Invoke(false);
                _machine.SetSystemStatus(MachineState.Standby);
                _logAction?.Invoke(T("Log_ErrorReset"), LogLevel.Info);
            });

            OpenDoorCommand = new RelayCommand(_ =>
            {
                // 가동 중 도어 오픈은 안전상 차단
                if (IsRunning)
                {
                    _logAction?.Invoke(T("Log_DoorOpenBlocked"), LogLevel.Warning);
                    return;
                }
                _machine.OpenDoor();
                IsDoorLocked = false;
                _logAction?.Invoke(T("Log_DoorOpen"), LogLevel.Info);
            });

            CloseDoorCommand = new RelayCommand(_ =>
            {
                _machine.CloseDoor();
                IsDoorLocked = true;
                _logAction?.Invoke(T("Log_DoorClose"), LogLevel.Info);
            });

            VacuumOnCommand = new RelayCommand(_ =>
            {
                _machine.VacuumOn();
                IsVacuumOn = true;
                _logAction?.Invoke(T("Log_VacuumOn"), LogLevel.Info);
            });

            VacuumOffCommand = new RelayCommand(_ =>
            {
                _machine.VacuumOff();
                IsVacuumOn = false;
                _logAction?.Invoke(T("Log_VacuumOff"), LogLevel.Info);
            });

            // 시작 전에도 우측 패널에 절차를 미리 표시
            BuildSteps();

            // 100ms 주기 센서 폴링 — 타이머의 유일한 책임
            _procTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _procTimer.Tick += (_, _) => UpdateSensorStatus();
            _procTimer.Start();
        }

        // def.Name 은 번역 키 (Step_AutoPrint_*). NameKey 를 보존해 언어 변경 시
        // RefreshStepNames() 로 재번역할 수 있게 한다.
        private void BuildSteps()
        {
            Steps.Clear();
            int swath = _getSwathCount?.Invoke() ?? 1;
            double headLen = _getHeadLength?.Invoke() ?? 0;
            foreach (var def in AutoPrintSequence.Build(_machine, _motion, swath, headLen))
            {
                Steps.Add(new SequenceStep
                {
                    Number  = def.Number,
                    NameKey = def.Name,
                    Name    = Common.Loc.T(def.Name),
                    Action  = def.Action,
                });
            }
        }

        /// <summary>언어 변경 시 호출 — 진행 중이어도 표시명만 갱신</summary>
        public void RefreshStepNames()
        {
            foreach (var s in Steps)
                s.Name = Common.Loc.T(s.NameKey);
        }

        private void MarkRunningStepAs(StepStatus status)
        {
            var running = Steps.FirstOrDefault(s => s.Status == StepStatus.Running);
            if (running != null) running.Status = status;
        }

        private async Task RunAutoPrintAsync()
        {
            // 가동 중 재진입은 CanExecute 에서 막히므로 여기서는 사전 조건만 확인
            if (!CheckSafetyBeforeStart()) return;

            IsRunning  = true;
            IsError    = false;
            ProcessProgress = 0;
            CurrentStepName = "STARTING";
            CachePrintRange();
            _machine.SetSystemStatus(MachineState.Running);
            _logAction?.Invoke(T("Log_Start"), LogLevel.Success);

            _cts = new CancellationTokenSource();
            var startTime = DateTime.Now;
            bool success = false;
            int cycle = 0;

            try
            {
                // ── 연속 운전 루프 — IsContinuousMode(토글)를 매 사이클 실시간 확인해 반복 ──
                do
                {
                cycle++;
                startTime = DateTime.Now;

                // 각 사이클 시작 시 애니메이션/스텝 상태를 리셋
                AutoPrintStarted?.Invoke();
                BuildSteps();
                int total = Steps.Count;

                for (int i = 0; i < Steps.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var step = Steps[i];
                    bool stepCompleted = false;

                    // 알람 일시정지로 step 이 중단되면 IsPaused 가 풀린 후 같은 step 을 재시도
                    while (!stepCompleted)
                    {
                        // OCE 대신 폴링으로 IsPaused/CTS 를 감지 — UI 스레드 부담 최소화
                        if (IsPaused)
                        {
                            CurrentStepName = $"[{step.Number}/{total}] {step.Name}  (PAUSED)";
                            while (IsPaused && !_cts.Token.IsCancellationRequested)
                                await Task.Delay(100);
                        }
                        _cts.Token.ThrowIfCancellationRequested();   // STOP → 외부 catch

                        CurrentStepName = $"[{step.Number}/{total}] {step.Name}";
                        ProcessProgress = (double)i / total * 100;
                        AutoPrintStepChanged?.Invoke(step.Number);

                        step.Status  = StepStatus.Running;
                        step.Elapsed = "-";

                        _stepCts?.Dispose();
                        _stepCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            await step.Action(_stepCts.Token);
                            sw.Stop();
                            step.Elapsed = $"{sw.Elapsed.TotalSeconds:F1}s";
                            step.Status  = StepStatus.Done;
                            stepCompleted = true;
                        }
                        catch (OperationCanceledException)
                        {
                            // 메인 CTS 가 취소된 경우(STOP) 는 외부 catch 로 위임,
                            // 그 외(=_stepCts 만 취소)는 알람 일시정지 → 재개 후 같은 step 재시도
                            _cts.Token.ThrowIfCancellationRequested();
                            step.Status = StepStatus.Aborted;
                            _logAction?.Invoke(T("Log_AutoPrintStepAborted", step.Number), LogLevel.Warning);
                        }
                    }
                }

                // ── 한 사이클 완료 ──
                ProcessProgress = 100;
                TotalCount++;
                if (TotalCount >= 1000) TotalCount = 0;
                TactTime = Math.Round((DateTime.Now - startTime).TotalSeconds, 1);
                CurrentStepName = IsContinuousMode ? $"CYCLE {cycle} DONE  ·  TOTAL {TotalCount}" : "COMPLETED";
                _logAction?.Invoke(T("Log_AutoPrintCompleted", TactTime), LogLevel.Success);

                // 연속 토글이 켜져 있으면 다음 사이클 전 짧은 대기(취소 감지 포함)
                if (IsContinuousMode && !_cts.Token.IsCancellationRequested)
                    await Task.Delay(500, _cts.Token);
                }
                while (IsContinuousMode && !_cts.Token.IsCancellationRequested);

                CurrentStepName = "COMPLETED";
                _machine.SetSystemStatus(MachineState.Standby);
                success = true;
            }
            catch (OperationCanceledException)
            {
                MarkRunningStepAs(StepStatus.Aborted);
                CurrentStepName = "ABORTED";
                _machine.SetSystemStatus(MachineState.Standby);
            }
            catch (TimeoutException ex)
            {
                MarkRunningStepAs(StepStatus.Failed);
                IsError = true;
                CurrentStepName = "TIMEOUT";
                _machine.SetSystemStatus(MachineState.Alarm);
                _logAction?.Invoke(T("Log_AutoPrintTimeoutMsg", ex.Message), LogLevel.Error);
                _raiseAlarm?.Invoke("SEQ-MOTION-TIMEOUT"); 
            }
            catch (Exception ex)
            {
                MarkRunningStepAs(StepStatus.Failed);
                IsError = true;
                CurrentStepName = "ERROR";
                _machine.SetSystemStatus(MachineState.Alarm);
                _logAction?.Invoke(T("Log_AutoPrintFailureMsg", ex.Message), LogLevel.Error);
                _raiseAlarm?.Invoke("SEQ-AUTO-PRINT-FAIL");  
            }
            finally
            {
                IsRunning = false;
                IsPaused  = false;     // 다음 런을 위해 게이트 해제
                IsVacuumOn = _machine.IsGlassDetected();
                _stepCts?.Dispose();
                _stepCts = null;
                _cts?.Dispose();
                _cts = null;

                // 초기화 요청으로 취소된 경우, ABORTED 표시 대신 IDLE 초기상태로 정리.
                if (_resettingForInit)        ApplyInitReset();
                else if (success)             AutoPrintCompleted?.Invoke();
                else                          AutoPrintAborted?.Invoke();
            }
        }

        /// <summary>
        /// 초기화(INITIALIZE) 수행 시 호출 — 진행/일시정지 중이던 오토런을 완전히 중단하고
        /// 대시보드를 초기 상태(IDLE·진행률 0·카운트 0)로 되돌린다.
        /// STOP 은 일시정지(IsRunning 유지)이므로, 오토런을 멈춘 채 초기화하면 백그라운드 런과
        /// 화면 상태가 남는다. 초기화와 함께 오토런도 초기화해 정합성을 맞춘다.
        /// </summary>
        public void ResetForInitialize()
        {
            _resettingForInit = true;
            IsContinuousMode  = false;   // 연속 토글 OFF(반복 중단)
            IsPaused          = false;   // 일시정지 게이트 해제 → 루프가 취소를 감지
            _stepCts?.Cancel();
            _cts?.Cancel();

            // 진행 중 런이 없으면 즉시 정리(런이 있으면 런의 finally 에서 ApplyInitReset 실행).
            if (!IsRunning) ApplyInitReset();
        }

        private void ApplyInitReset()
        {
            IsError         = false;
            ProcessProgress = 0;
            TactTime        = 0;
            TotalCount      = 0;
            CurrentStepName = "IDLE";
            BuildSteps();                 // 스텝 상태(Done/Running/Aborted 표시) 리셋
            AutoPrintAborted?.Invoke();   // View 애니메이션도 초기 위치로 복귀
            _resettingForInit = false;
        }

        /// <summary>
        /// 가동 전 사전 조건 (디버그/릴리즈 공통):
        /// 미해제 알람 없음, 전체 축 원점복귀 완료(INITIAL 시퀀스 수행), 전체 축 서보 ON.
        /// </summary>
        private bool CheckPrerequisites()
        {
            var allAxes = _machine.Motion?.GetAllStatus();
            if (allAxes == null || allAxes.Count == 0)
            {
                _logAction?.Invoke(T("Log_AxisInfoMissing"), LogLevel.Error);
                return false;
            }

            // 사용자 요청(2026-07): 오토런 사전 체크는 '초기화(원점복귀) 완료' 하나만 유지.
            // 미해제 알람·서보ON·EMO·도어·압력스위치 체크는 제외.
            var notHomed = allAxes.Where(ax => !ax.IsHomeDone)
                                  .Select(ax => ax.AxisNo).ToList();
            if (notHomed.Count > 0)
            {
                string msg = T("Log_PrereqNotHomed", string.Join(", ", notHomed));
                _logAction?.Invoke(msg.Replace("\n\n", " — "), LogLevel.Error);
                Dialogs.Show(msg, T("Log_PrereqDialogTitle"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        // 오토런 전 체크는 초기화(원점복귀) 완료만 확인.
        // 기존 EMO/도어/압력스위치/알람/서보 체크는 사용자 요청으로 제외됨.
        private bool CheckSafetyBeforeStart()
        {
            return CheckPrerequisites();
        }

        public void UpdateSensorStatus()
        {
            if (_machine == null) return;

            IsGlassDetected = _machine.IsGlassDetected();
            IsDoorLocked = _machine.IsDoorLocked();
            IsEmoActive = _machine.IsEmoActive();

            // 활성 레시피 프린팅수 표시 갱신(APPLY 반영). SetProperty 라 값 변할 때만 알림.
            SwathCount = _getSwathCount?.Invoke() ?? 1;

            // HMI 표기는 X/Y/Z/Q 인데 모션 드라이버는 회전축을 "T" 로 식별
            if (_machine.Motion != null)
            {
                MotorXPosition = _machine.Motion.GetActualPosition("X");
                MotorYPosition = _machine.Motion.GetActualPosition("Y");
                MotorZPosition = _machine.Motion.GetActualPosition("Z");
                MotorQPosition = _machine.Motion.GetActualPosition("T");
            }

            // 시퀀스 도중 EMO 가 들어오면 메인 CTS 를 즉시 취소해 step 을 깨운다
            if (IsEmoActive && IsRunning)
            {
                _cts?.Cancel();
                _machine.VacuumOff();
                _machine.SetSystemStatus(MachineState.Emergency);
                _onAlarmChanged?.Invoke(true);
                _logAction?.Invoke(T("Log_EmoStopped"), LogLevel.Fatal);
                _raiseAlarm?.Invoke("SNS-EMO");
            }
        }

    }

}