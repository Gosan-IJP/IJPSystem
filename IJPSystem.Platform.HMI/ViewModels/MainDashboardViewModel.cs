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
        private readonly Func<int>? _getPrintDirection;   // 0=단방향, 1=양방향

        // 실장 구조 — 헤드: X(갠트리, 크로스스캔) + Z(승강) / 스테이지: Y(스캔 이송) + T(정렬 회전).
        // 메인 대시보드 애니메이션은 이 축들의 실측 위치·티칭 좌표로 구동한다.
        private const string ScanAxis  = "Y";   // 스캔(스테이지 이송)
        private const string StepAxis  = "X";   // 스와스 스텝오버(갠트리 크로스스캔)
        private const string LiftAxis  = "Z";   // 헤드 승강
        // 정렬축(T)은 대시보드 애니메이션에서 다루지 않는다 — 보정각이 화면에서 식별되지 않는 반면
        // 글라스를 회전시키면 인쇄 표시가 깨진다. T 는 MOTOR POSITION 패널의 숫자로 확인.

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

        /// <summary>
        /// 글라스 정렬 자리(마크1)의 스캔축 좌표[mm]. 티칭이 없으면 NaN.
        ///
        /// <para>정렬 구간은 인쇄 구간보다 훨씬 멀리 있다 — 이 장비는 인쇄가 Y 120~270mm 인데
        /// 정렬 자리가 274mm, 마크2 는 434mm 다. 대시보드가 인쇄 축척(2.9px/mm)으로 그리면
        /// 마크2 가 화면 밖 650px 지점이라 <b>정렬 이동이 통째로 안 보인다</b>. 그래서 정렬
        /// 구간만 따로 그리고, 그 기준점이 이 값이다.</para>
        /// </summary>
        public double GlassAlignScanMm { get; private set; } = double.NaN;
        public double PrintEndScanMm   { get; private set; } = double.NaN;
        public bool   HasPrintRange => !double.IsNaN(PrintStartScanMm)
                                    && !double.IsNaN(PrintEndScanMm)
                                    && Math.Abs(PrintEndScanMm - PrintStartScanMm) > 0.001;
        public bool   HasReadyMapping => !double.IsNaN(ReadyScanMm)
                                      && !double.IsNaN(PrintStartScanMm)
                                      && Math.Abs(PrintStartScanMm - ReadyScanMm) > 0.001;

        // ── 헤드 승강(Z) 티칭 좌표 ─────────────────────────────────────────
        // PRINT HEAD UP / DOWN 포인트의 Z 값. 있으면 애니메이션이 실측 Z 에 동기되고,
        // 없으면 View 가 기존 스크립트(스텝 진입 후 0.7초 Lerp)로 폴백한다.
        public double HeadUpLiftMm   { get; private set; } = double.NaN;
        public double HeadDownLiftMm { get; private set; } = double.NaN;
        public bool   HasLiftMapping => !double.IsNaN(HeadUpLiftMm)
                                     && !double.IsNaN(HeadDownLiftMm)
                                     && Math.Abs(HeadDownLiftMm - HeadUpLiftMm) > 0.001;

        // ── 스와스 스텝오버(X) 매핑 ────────────────────────────────────────
        // X 는 절대 티칭 좌표가 없고 패스 사이 상대이동(MoveAxisRelative headLength)이라,
        // 시퀀스 시작 시점의 X 를 원점으로 잡아 (현재X − 시작X) / 전체이동량 으로 진행률을 만든다.
        //   전체이동량 = headLength × (swath − 1)
        // ※ headLength 는 지금 레시피 화면에서 수동 입력. 향후 노즐/헤드 정보로 산출하게 되면
        //   StepSpanMm 계산 한 곳만 바꾸면 된다.
        public double StepOriginMm { get; private set; } = double.NaN;
        public double StepSpanMm   { get; private set; } = double.NaN;
        public bool   HasStepMapping => !double.IsNaN(StepOriginMm)
                                     && !double.IsNaN(StepSpanMm)
                                     && Math.Abs(StepSpanMm) > 0.001;

        // View 60fps 프레임 콜백이 매 프레임 호출. 예전엔 매 호출마다 GetActualPosition(=EtherCAT 상태읽기)을
        // 수행해, 프린팅(Y 이송) 중 모션제어와 버스 경합으로 프레임 스파이크(버벅임)가 발생했다.
        // → 하드웨어 실측을 스로틀(≈25Hz)해 캐시로 반환한다. 60fps 애니메이션엔 충분히 매끄럽고
        //   하드웨어 부하는 1/2 이하로 줄어든다.
        // 읽기 주기를 둘로 나눈다 — GetAllStatus()/GetActualPosition 은 모두 하드웨어를 실제로
        // 읽으므로(EtherCAT), 4축을 전부 25Hz 로 읽으면 스캔 중 버스 경합이 4배가 된다.
        //   · 스캔축(Y) : 40ms(≈25Hz) — 애니메이션이 매 프레임 따라가야 하는 유일한 축
        //   · X/Z/T     : 200ms(5Hz)  — 스텝오버·승강·정렬은 이산적이고 느려 5Hz 로 충분
        // 결과: 초당 읽기 25 → 40 회. 예전(25)보다 늘지만 4배(100)는 피한다.
        private readonly Dictionary<string, double> _livePos = new();
        private long _scanReadTick;
        private long _slowReadTick;
        private const long LiveScanThrottleMs = 40;    // 스캔축 ≈25Hz
        private const long SlowAxisThrottleMs = 200;   // 그 외 축 5Hz

        private static readonly string[] SlowAxes = { StepAxis, LiftAxis };

        private void RefreshLivePositions()
        {
            var motion = _machine.Motion;
            if (motion == null) return;
            long now = Environment.TickCount64;

            if (now - _scanReadTick >= LiveScanThrottleMs || _scanReadTick == 0)
            {
                _scanReadTick = now;
                _livePos[ScanAxis] = motion.GetActualPosition(ScanAxis);
            }

            if (now - _slowReadTick >= SlowAxisThrottleMs || _slowReadTick == 0)
            {
                _slowReadTick = now;
                foreach (var ax in SlowAxes) _livePos[ax] = motion.GetActualPosition(ax);
            }
        }

        /// <summary>축 실측 위치[mm 또는 deg]. View 프레임 콜백에서 매 프레임 호출해도 안전(스로틀 캐시).</summary>
        public double GetLiveAxisPos(string axisNo)
        {
            RefreshLivePositions();
            return _livePos.TryGetValue(axisNo, out var v) ? v : 0.0;
        }

        public double GetLiveScanMm()   => GetLiveAxisPos(ScanAxis);
        public double GetLiveStepMm()   => GetLiveAxisPos(StepAxis);
        public double GetLiveLiftMm()   => GetLiveAxisPos(LiftAxis);

        private void CachePrintRange()
        {
            ReadyScanMm      = _getPointAxisMm?.Invoke(PointNames.Ready,      ScanAxis) ?? double.NaN;
            PrintStartScanMm = _getPointAxisMm?.Invoke(PointNames.PrintStart, ScanAxis) ?? double.NaN;
            PrintEndScanMm   = _getPointAxisMm?.Invoke(PointNames.PrintEnd,   ScanAxis) ?? double.NaN;
            GlassAlignScanMm = _getPointAxisMm?.Invoke(PointNames.GlassAlign, ScanAxis) ?? double.NaN;
            OnPropertyChanged(nameof(ReadyScanMm));
            OnPropertyChanged(nameof(GlassAlignScanMm));
            OnPropertyChanged(nameof(PrintStartScanMm));
            OnPropertyChanged(nameof(PrintEndScanMm));
            OnPropertyChanged(nameof(HasPrintRange));
            OnPropertyChanged(nameof(HasReadyMapping));

            // 헤드 승강(Z) — 티칭 포인트에서 직접.
            HeadUpLiftMm   = _getPointAxisMm?.Invoke(PointNames.PrintHeadUp,   LiftAxis) ?? double.NaN;
            HeadDownLiftMm = _getPointAxisMm?.Invoke(PointNames.PrintHeadDown, LiftAxis) ?? double.NaN;
            OnPropertyChanged(nameof(HeadUpLiftMm));
            OnPropertyChanged(nameof(HeadDownLiftMm));
            OnPropertyChanged(nameof(HasLiftMapping));

            // 스와스 스텝오버(X) — 현재 X 를 원점으로, 전체 이동량은 headLength × (swath−1).
            int    swath  = Math.Max(1, _getSwathCount?.Invoke() ?? 1);
            double headLen = _getHeadLength?.Invoke() ?? 0.0;
            StepOriginMm = _machine.Motion != null ? GetLiveAxisPos(StepAxis) : double.NaN;
            StepSpanMm   = (swath > 1 && headLen > 0) ? headLen * (swath - 1) : double.NaN;
            OnPropertyChanged(nameof(StepOriginMm));
            OnPropertyChanged(nameof(StepSpanMm));
            OnPropertyChanged(nameof(HasStepMapping));
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

        // 알람으로 멈춘 것인지(=STOP 버튼으로 멈춘 것과 구분). 알람 해제 시 자동 재개하지 않기 위해 필요.
        private bool _pausedByAlarm;
        // 현재 알람이 떠 있는지 — 알람 상태에서 START 를 눌러 재개하는 것을 막는다.
        private bool _alarmActive;

        // MainViewModel 이 AlarmVM.HasActiveAlarm 변경 시 호출
        public void OnAlarmActiveChanged(bool isAlarmActive)
        {
            _alarmActive = isAlarmActive;
            (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();

            if (!IsRunning) return;

            if (isAlarmActive && !IsPaused)
            {
                IsPaused = true;
                _pausedByAlarm = true;
                StopAllMotion();
                // 진행 중 step 의 await 를 즉시 깨움 → 외부 루프가 게이트에서 대기
                _stepCts?.Cancel();
                _logAction?.Invoke(T("Log_AutoPrintAlarmPause"), LogLevel.Warning);
            }
            else if (!isAlarmActive && IsPaused && _pausedByAlarm)
            {
                // ★알람이 풀려도 자동으로 재개하지 않는다(실장 2026-08-04).
                //   예전엔 여기서 게이트를 열어, 알람 이력 화면에서 해제하는 순간
                //   조작자가 메인 화면을 보고 있지 않은 상태로 설비가 다시 움직였다.
                //   재개는 반드시 사람이 메인 화면에서 START 를 누르거나 초기화를 실행해야 한다.
                _pausedByAlarm = false;
                _logAction?.Invoke(T("Log_AutoPrintAlarmResume"), LogLevel.Warning);
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

        // 프린팅 방향(애니메이션이 참조) — 시퀀스 생성 시 1회 확정. true=양방향(왕복 프린트), false=단방향(프린트 후 복귀).
        private bool _isBidirectional = true;
        public bool IsBidirectional
        {
            get => _isBidirectional;
            set => SetProperty(ref _isBidirectional, value);
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

        // 진행 중인 시퀀스 스텝 번호(1-based, 대기=0). 화면 전환 후 재진입한 View 가
        // 애니메이션을 현재 스텝 기준으로 복원할 때 사용한다(AutoPrintStepChanged 와 동기).
        public int CurrentStepNumber { get; private set; }
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

        // 온도 알람 (2호기 X008/X009). 1호기는 미배선이라 항상 false.
        private bool _isTempHighAlarm;
        public bool IsTempHighAlarm
        {
            get => _isTempHighAlarm;
            set => SetProperty(ref _isTempHighAlarm, value);
        }

        private bool _isTempLowAlarm;
        public bool IsTempLowAlarm
        {
            get => _isTempLowAlarm;
            set => SetProperty(ref _isTempLowAlarm, value);
        }

        // 온도 알람 rising-edge 추적(100ms 폴링에서 알람 1회만 발생시키기 위함)
        private bool _tempHighPrev;
        private bool _tempLowPrev;

        // MOTOR POSITION 패널 — 축 개수와 무관하게 설정(MotionAxisList) 기반으로 표시.
        // 1호기(3축)/2호기(6축)를 같은 바이너리로 지원하기 위해 고정 X/Y/Z/T 속성 대신 컬렉션 사용.
        public System.Collections.ObjectModel.ObservableCollection<MotorPositionVm> MotorPositions { get; }
            = new System.Collections.ObjectModel.ObservableCollection<MotorPositionVm>();
        private readonly System.Collections.Generic.Dictionary<string, MotorPositionVm> _motorPosMap = new();
        private bool _motorPosBuilt;

        // ── 드라이브(서보) 준비 표시등 ──────────────────────────────
        // 냉부팅 직후엔 서보 드라이브가 폴트로 올라올 수 있어, 이 상태에서 초기화를 누르면
        // ServoOn 이 EtherCAT 에러(-20280)로 실패한다. 작업자가 누르기 전에 눈으로 확인하도록
        // 모터 포지션 패널에 3색 표시등을 둔다. "Fault"=🔴 / "Connecting"=🟡 / "Ready"=🟢.
        private string _motorReadyState = "Connecting";
        public string MotorReadyState
        {
            get => _motorReadyState;
            set => SetProperty(ref _motorReadyState, value);
        }

        // 패널에 짧게 표기할 캡션
        private string _motorReadyText = "연결 대기중";
        public string MotorReadyText
        {
            get => _motorReadyText;
            set => SetProperty(ref _motorReadyText, value);
        }

        // 툴팁에 표기할 상세(폴트 축 등)
        private string _motorReadyDetail = "드라이브 연결 대기중";
        public string MotorReadyDetail
        {
            get => _motorReadyDetail;
            set => SetProperty(ref _motorReadyDetail, value);
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
            Func<double>? getHeadLength = null,
            Func<int>? getPrintDirection = null)
        {
            _logAction       = logAction;
            _onAlarmChanged  = onAlarmChanged;
            _raiseAlarm      = raiseAlarm;
            _getPointAxisMm  = getPointAxisMm;
            _hasActiveAlarm  = hasActiveAlarm;
            _getSwathCount   = getSwathCount;
            _getHeadLength   = getHeadLength;
            _getPrintDirection = getPrintDirection;
            _machine = machine;
            _motion = motion;

            ActiveRecipeName = initialActiveRecipe;

            // 시작 — 정지 상태면 시퀀스 시작, 일시정지 상태면 재개.
            // 연속 여부는 IsContinuousMode 토글로 결정(별도 연속 버튼 없음).
            StartCommand = new RelayCommand(async _ =>
            {
                // 알람이 떠 있는 동안에는 시작도 재개도 막는다 — 원인을 두고 다시 움직이면 안 된다.
                if (_alarmActive)
                {
                    _logAction?.Invoke(T("Log_AutoPrintAlarmBlocked"), LogLevel.Warning);
                    return;
                }
                if (IsRunning && IsPaused)
                {
                    IsPaused = false;
                    _pausedByAlarm = false;
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
            }, _ => !_alarmActive && (!IsRunning || IsPaused));   // 알람 중에는 버튼 자체를 비활성

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
            bool bidi = (_getPrintDirection?.Invoke() ?? 1) == 1;   // 1=양방향, 0=단방향
            // 애니메이션(OnFrameTick)이 참조하는 SwathCount/IsBidirectional 을 시퀀스 생성 시 1회 확정.
            // (센서 100ms 폴링으로 매번 읽지 않음 — 값은 사이클 시작 때만 바뀌므로 폴링 불필요)
            SwathCount = swath;
            IsBidirectional = bidi;
            foreach (var def in AutoPrintSequence.Build(_machine, _motion, swath, headLen, bidi))
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

        // 사이클 시작 파라미터 1줄 — 재현에 필요한 값(레시피·프린팅수·헤드길이·방향·드라이버)을
        // 한 줄로 남긴다. 이게 없으면 로그만 받아서는 어떤 조건으로 돌았는지 알 수 없다.
        private void LogCycleParameters(int cycle, int totalSteps)
        {
            var dm = AppSettingsService.Current?.DriverMode;
            _logAction(
                $"[SEQ] AutoPrint 사이클 {cycle} 시작 — 레시피 '{ActiveRecipeName}', " +
                $"프린팅수 {SwathCount}, 헤드길이 {(_getHeadLength?.Invoke() ?? 0):F1}mm, " +
                $"{(IsBidirectional ? "양방향" : "단방향")}, 연속운전 {(IsContinuousMode ? "ON" : "OFF")}, " +
                $"스텝 {totalSteps}개, 드라이버 IO={dm?.IO}/Motion={dm?.Motion}/Vision={dm?.Vision}",
                LogLevel.Info);
        }


        /// <summary>
        /// 번역 키로 스텝 번호를 찾는다. 없으면 0.
        ///
        /// <para><b>화면이 번호를 코드에 박지 않게 하려고 둔다.</b> 대시보드 애니메이션은
        /// "8번이 인쇄" 같은 상수로 돌아갔는데, 시퀀스 앞에 단계가 하나라도 끼면 전부 어긋난다 —
        /// 실제로 글라스 정렬 16단계를 넣자 애니메이션이 통째로 밀렸다(2026-08-26).</para>
        /// </summary>
        public int StepNumberOf(string nameKey)
        {
            foreach (var s in Steps)
                if (string.Equals(s.NameKey, nameKey, StringComparison.Ordinal)) return s.Number;
            return 0;
        }

        private bool _isAligning;

        /// <summary>
        /// 지금 글라스 정렬 구간인가.
        ///
        /// <para>정렬은 인쇄 경로 밖에서 ±피듀셜 간격만큼 오간다. 그 움직임을 인쇄 진행률로
        /// 옮기면 대시보드에서 글라스가 튄다 — 정렬 중에는 화면을 그대로 둔다.</para>
        /// </summary>
        public bool IsAligning
        {
            get => _isAligning;
            private set => SetProperty(ref _isAligning, value);
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
            IsAligning = false;
            CurrentStepNumber = 0;
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

                // 스텝을 <b>먼저</b> 만든다 — 화면이 시작 신호를 받고 스텝 구성을 보고
                // 애니메이션 기준 번호를 잡기 때문이다. 순서가 반대면 <b>지난 사이클의 구성</b>을
                // 보게 되고, 정렬 사용 여부를 바꾼 직후 기준 번호가 전부 어긋나 글라스가
                // 통째로 안 움직였다(2026-08-26).
                BuildSteps();
                AutoPrintStarted?.Invoke();
                int total = Steps.Count;

                LogCycleParameters(cycle, total);

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
                        CurrentStepNumber = step.Number;   // 재진입 View 애니메이션 복원용
                        IsAligning = step.NameKey.StartsWith("Step_GlassAlign_", StringComparison.Ordinal);
                        ProcessProgress = (double)i / total * 100;
                        AutoPrintStepChanged?.Invoke(step.Number);

                        step.Status  = StepStatus.Running;
                        step.Elapsed = "-";

                        _stepCts?.Dispose();
                        _stepCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            await SequenceStepLogger.RunAsync(
                                step.Number, step.NameKey, step.Action,
                                "AutoPrint", _stepCts.Token, _logAction);
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
                IsAligning = false;
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
                _pausedByAlarm = false;
                CurrentStepNumber = 0;
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
            _pausedByAlarm    = false;   // 알람으로 멈춘 런도 초기화로 확실히 끝낸다
            _stepCts?.Cancel();
            _cts?.Cancel();

            // 진행 중 런이 없으면 즉시 정리(런이 있으면 런의 finally 에서 ApplyInitReset 실행).
            if (!IsRunning) ApplyInitReset();
        }

        private void ApplyInitReset()
        {
            IsError         = false;
            ProcessProgress = 0;
            IsAligning      = false;
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

            // (SwathCount 는 BuildSteps 에서 1회 확정 — 100ms 폴링 제거)

            // HMI 표기는 X/Y/Z/Q 인데 모션 드라이버는 회전축을 "T" 로 식별
            if (_machine.Motion != null)
            {
                UpdateMotorPositions();
                UpdateMotorReadyState();
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

            // ── 온도 알람 (2호기 X008/X009) — rising-edge 에서 1회만 발생 ──
            // 1호기는 미배선이라 항상 false → 아무 것도 하지 않음(동일 코드로 무해).
            IsTempHighAlarm = _machine.IsTempHighAlarm();
            IsTempLowAlarm  = _machine.IsTempLowAlarm();

            if (IsTempHighAlarm && !_tempHighPrev)
            {
                _machine.SetSystemStatus(MachineState.Alarm);
                _onAlarmChanged?.Invoke(true);
                _logAction?.Invoke(T("Log_TempHighAlarm"), LogLevel.Error);
                _raiseAlarm?.Invoke("SNS-TEMP-HIGH");
            }
            _tempHighPrev = IsTempHighAlarm;

            if (IsTempLowAlarm && !_tempLowPrev)
            {
                _machine.SetSystemStatus(MachineState.Alarm);
                _onAlarmChanged?.Invoke(true);
                _logAction?.Invoke(T("Log_TempLowAlarm"), LogLevel.Error);
                _raiseAlarm?.Invoke("SNS-TEMP-LOW");
            }
            _tempLowPrev = IsTempLowAlarm;
        }

        /// <summary>
        /// MOTOR POSITION 패널 갱신 — 설정된 축을 최초 1회 구성한 뒤 매 폴링마다 위치만 갱신.
        /// 축 목록이 MotionAxisList(설정) 기준이라 3축/6축을 코드 변경 없이 표시한다.
        /// </summary>
        private void UpdateMotorPositions()
        {
            var motion = _machine?.Motion;
            if (motion == null) return;

            if (!_motorPosBuilt)
            {
                var axes = _machine?.Config?.MotionAxisList;
                if (axes != null && axes.Count > 0)
                {
                    foreach (var a in axes)
                    {
                        if (string.IsNullOrEmpty(a.AxisNo)) continue;
                        var vm = new MotorPositionVm(a.AxisNo);
                        _motorPosMap[a.AxisNo] = vm;
                        MotorPositions.Add(vm);
                    }
                    _motorPosBuilt = true;
                }
            }

            foreach (var kv in _motorPosMap)
                kv.Value.Position = motion.GetActualPosition(kv.Key);
        }

        /// <summary>
        /// 드라이브(서보) 준비 표시등 상태 갱신.
        /// 🔴 Fault      : 연결됐지만 폴트(ServoFault|CtlrFault)인 축이 있음 → 초기화 금지.
        /// 🟡 Connecting : 연결 전 / 상태 미수신 → 대기.
        /// 🟢 Ready      : 연결 OK + 모든 축 폴트 없음 → 초기화 가능.
        /// (냉부팅 직후 Y드라이브가 ServoFault 로 올라오는 구간을 작업자에게 보여주기 위함.)
        /// </summary>
        private void UpdateMotorReadyState()
        {
            var motion = _machine?.Motion;
            if (motion == null || !motion.IsConnected)
            {
                MotorReadyState  = "Connecting";
                MotorReadyText   = "연결 대기중";
                MotorReadyDetail = "드라이브 연결 대기중";
                return;
            }

            var all = motion.GetAllStatus();
            if (all == null || all.Count == 0)
            {
                MotorReadyState  = "Connecting";
                MotorReadyText   = "연결 대기중";
                MotorReadyDetail = "드라이브 상태 수신 대기중";
                return;
            }

            var faulted = all.Where(s => s.IsAlarm).Select(s => s.AxisNo).ToArray();
            if (faulted.Length > 0)
            {
                MotorReadyState  = "Fault";
                MotorReadyText   = "드라이브 폴트";
                MotorReadyDetail = $"드라이브 폴트({string.Join(",", faulted)}) — 폴트 정리 후 초기화";
            }
            else
            {
                MotorReadyState  = "Ready";
                MotorReadyText   = "초기화 가능";
                MotorReadyDetail = "드라이브 폴트 없음 — 초기화 가능";
            }
        }

    }

    /// <summary>MOTOR POSITION 패널 한 축의 표시 항목(축 라벨 + 현재 위치).</summary>
    public sealed class MotorPositionVm : IJPSystem.Platform.Domain.Common.ViewModelBase
    {
        public string Label { get; }
        public MotorPositionVm(string label) => Label = label;

        private double _position;
        public double Position { get => _position; set => SetProperty(ref _position, value); }
    }

}