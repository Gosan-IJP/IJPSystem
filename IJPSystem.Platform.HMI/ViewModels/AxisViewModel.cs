using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.IO;
using IJPSystem.Platform.Domain.Models.Motion; // AxisDeviceInfo 위치
using IJPSystem.Platform.HMI.Common;
using System;
using System.Windows;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class AxisViewModel : ViewModelBase
    {
        private readonly IMotionDriver _driver;
        private readonly MainViewModel _mainViewModel; 

        #region Properties (Config & Status)
        public AxisDeviceInfo Info { get; private set; }

        private double _targetPosition;
        public double TargetPosition
        {
            get => _targetPosition;
            set => SetProperty(ref _targetPosition, value);
        }
        // 3. 실시간 하드웨어 상태 (필드와 속성)
        private double _currentPos;
        public double CurrentPos 
        { 
            get => _currentPos;
            set
            {
                // 값이 거의 차이가 없으면 업데이트를 건너뜀 (Deadband 설정)
                if (Math.Abs(_currentPos - value) < 0.001) return;
                SetProperty(ref _currentPos, value);
            }
        }


        /// <summary>
        /// 위치 표시 단위. 회전축(T)은 deg, 직선축은 mm — 값은 MotorConfig 의 Unit 이다.
        ///
        /// <para>화면에 "mm" 를 글자로 박으면 회전축이 mm 로 보인다. 축이 자기 단위를
        /// 들고 있으니 표시도 여기서 받아 간다.</para>
        /// </summary>
        public string PosUnit => string.IsNullOrWhiteSpace(Info?.Unit) ? "mm" : Info.Unit;

        private bool _isServoOn;
        public bool IsServoOn
        {
            get => _isServoOn;
            set => SetProperty(ref _isServoOn, value); 
        }

        // AxisViewModel.cs에 아래 속성들을 추가하세요.
        private bool _isHomeDone;
        public bool IsHomeDone
        {
            get => _isHomeDone;
            set => SetProperty(ref _isHomeDone, value);
        }

        private bool _isInPosition;
        public bool IsInPosition
        {
            get => _isInPosition;
            set => SetProperty(ref _isInPosition, value);
        }

        private bool _isAlarm;
        public bool IsAlarm 
        { 
            get => _isAlarm; 
            set => SetProperty(ref _isAlarm, value); 
        }

        private bool _isMoving;
        public bool IsMoving 
        { 
            get => _isMoving; 
            set => SetProperty(ref _isMoving, value); 
        }
        private bool _isAbsMode = true; // 기본값: 절대좌표(ABS)
        public bool IsAbsMode
        {
            get => _isAbsMode;
            set
            {
                if (SetProperty(ref _isAbsMode, value))
                {
                    // 한쪽이 바뀌면 반대쪽(IsIncMode)도 바뀌었다고 UI에 알림
                    OnPropertyChanged(nameof(IsIncMode));
                }
            }
        }
        public bool IsUnitContinuity
        {
            get => JogUnit == 0;
            set { if (value) JogUnit = 0; }
        }

        public bool IsUnit10um
        {
            get => JogUnit == 0.01;
            set { if (value) JogUnit = 0.01; }
        }

        public bool IsUnit100um
        {
            get => JogUnit == 0.1;
            set { if (value) JogUnit = 0.1; }
        }

        private double _jogUnit = 0;
        public double JogUnit
        {
            get => _jogUnit;
            set
            {
                if (SetProperty(ref _jogUnit, value))
                {
                    OnPropertyChanged(nameof(IsUnitContinuity));
                    OnPropertyChanged(nameof(IsUnit10um));
                    OnPropertyChanged(nameof(IsUnit100um));
                    OnPropertyChanged(nameof(IsJogContinuity)); 
                }
            }
        }

        private bool _upperLimit;
        public bool UpperLimit   // 상한(+EL) 하드리밋
        {
            get => _upperLimit;
            set => SetProperty(ref _upperLimit, value);
        }

        private bool _lowerLimit;
        public bool LowerLimit   // 하한(-EL) 하드리밋
        {
            get => _lowerLimit;
            set => SetProperty(ref _lowerLimit, value);
        }

        private bool _homeSensor;
        public bool HomeSensor
        {
            get => _homeSensor;
            set => SetProperty(ref _homeSensor, value);
        }

        public bool IsJogContinuity
        {
            get => JogUnit == 0;
            set
            {if (value) JogUnit = 0;}
        }

        public bool IsIncMode
        {
            get => !_isAbsMode;
            set => IsAbsMode = !value;
        }
        
        private AxisStatus? _status;
        public AxisStatus? Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // Move/Jog 허용 조건 — 서보 ON · 알람 없음 · 모션 드라이버 연결됨.
        // 정비 중 서보 OFF/알람/미연결 상태에서 무반응 버튼을 눌러 생기는 혼란·오조작 방지.
        // UpdateMotorStatus(100ms)에서 변화 감지 시 PropertyChanged/RaiseCanExecuteChanged로 갱신.
        public bool CanMove => IsServoOn && !IsAlarm && _mainViewModel.MotionConnected;
        private bool _lastCanMove;

        // ── Move 진행률/남은거리 (절대·상대 이동 모두 지원) ──
        private double _moveStartPos;   // 이동 시작 시점 위치
        private double _moveTargetAbs;  // 목표 절대 위치 (INC면 시작위치 + 입력값)
        private bool   _moveSawMotion;  // 실제 모션(IsMoving) 관측 여부 — 완료 판정용

        private bool _isMoveActive;
        public bool IsMoveActive
        {
            get => _isMoveActive;
            private set { if (SetProperty(ref _isMoveActive, value)) OnPropertyChanged(nameof(CanJog)); }
        }

        // 조그 허용 = Move 가능 조건 && 이동명령(Move) 진행 중이 아님.
        // IsMoving이 아닌 IsMoveActive로 판정 — 조그 자체가 IsMoving을 띄워
        // dead-man(MouseUp) 정지가 막히는 위험을 피한다.
        public bool CanJog => CanMove && !IsMoveActive;

        // 목표까지 남은 거리(부호 있음 = 목표 − 현재, mm). 이동 방향이 부호로 드러남.
        // 진행 중이 아니면 0.
        public double DistanceToGo => IsMoveActive ? _moveTargetAbs - CurrentPos : 0.0;

        // 이동 진행률 0~100(%).
        public double MoveProgress
        {
            get
            {
                if (!IsMoveActive) return 0.0;
                double total = _moveTargetAbs - _moveStartPos;
                if (Math.Abs(total) < 1e-6) return 100.0;
                double done = (CurrentPos - _moveStartPos) / total;
                return Math.Clamp(done, 0.0, 1.0) * 100.0;
            }
        }
        #endregion

        #region Commands
        public ICommand MoveAbsCommand { get; }
        public ICommand JogForwardCommand { get; }
        public ICommand JogBackwardCommand { get; }
        public ICommand ServoCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand HomeCommand { get; }
        public ICommand ResetCommand { get; }

        #endregion

        public AxisViewModel(IMotionDriver driver, AxisDeviceInfo info, MainViewModel mainViewModel)
        {
            _driver = driver;
            Info = info;
            _mainViewModel = mainViewModel;
            Status = new AxisStatus { AxisNo = info.AxisNo, Name = info.Name, Unit = info.Unit };
            

            // 커맨드 연결 (메서드를 참조하도록 수정)
            MoveAbsCommand = new RelayCommand(async _ => await MoveAsync(), _ => CanMove);
            JogForwardCommand = new RelayCommand(async _ => await JogMoveAsync(true));
            JogBackwardCommand = new RelayCommand(async _ => await JogMoveAsync(false));
            ServoCommand = new RelayCommand(async _ => await ServoOnOffAsync());
            StopCommand = new RelayCommand(async _ => await StopAsync());
            HomeCommand = new RelayCommand(async _ => await HomeAsync());
            ResetCommand = new RelayCommand(async _ => await ResetAlarmAsync());
        }

        public async Task ServoOnOffAsync()
        {
            if (Status == null) return;

            bool currentState = Status.IsServoOn;
            bool targetState = !currentState;
            string action = targetState ? "ON" : "OFF";
            _mainViewModel.AddLog($"[MOTION] {Info.Name} (Axis:{Info.AxisNo}) Servo {action}");

            await _driver.ServoOn(Info.AxisNo, targetState);

            Status.IsServoOn = targetState;
            OnPropertyChanged(nameof(Status));
        }

        public async Task ForceServoOnAsync()
        {
            if (Status == null) return;
            _mainViewModel.AddLog($"[MOTION] {Info.Name} Servo ON");
            await _driver.ServoOn(Info.AxisNo, true);
            Status.IsServoOn = true;
            OnPropertyChanged(nameof(Status));
        }

        public async Task ForceServoOffAsync()
        {
            if (Status == null) return;
            _mainViewModel.AddLog($"[MOTION] {Info.Name} Servo OFF");
            await _driver.ServoOn(Info.AxisNo, false);
            Status.IsServoOn = false;
            OnPropertyChanged(nameof(Status));
        }

        // 2. 절대 위치 이동 메서드
        // profileKind   : 사용할 속도/가감속 프로파일 (기본 Move). 인쇄 시 Printing 지정.
        // profileOverride: 적용된 레시피 snapshot의 프로파일 등을 외부에서 명시적으로 지정. 시퀀스 호출 시 사용.
        //                 null이면 Info.MotionConfig (편집 중인 값) 사용.
        public async Task MoveAsync(MotionProfileKind profileKind = MotionProfileKind.Move,
                                    Profile? profileOverride = null)
        {
            if (string.IsNullOrEmpty(Info.AxisNo))
            {
                Dialogs.Show("숫자를 입력하세요");
                return;
            }
            string modeName = IsAbsMode ? "ABS" : "INC";
            _mainViewModel.AddLog($"[MOTION] {Info.Name} -> Start {modeName} Move ({profileKind}): {TargetPosition:F3}mm");

            // Move 진행률 추적 시작 — 절대/상대 입력을 목표 절대위치로 환산
            _moveStartPos  = CurrentPos;
            _moveTargetAbs = IsAbsMode ? TargetPosition : CurrentPos + TargetPosition;
            _moveSawMotion = false;
            IsMoveActive   = true;
            OnPropertyChanged(nameof(DistanceToGo));
            OnPropertyChanged(nameof(MoveProgress));

            var profile = profileOverride ?? (profileKind switch
            {
                MotionProfileKind.Printing => Info.MotionConfig.Printing,
                MotionProfileKind.Jog      => Info.MotionConfig.Jog,
                _                          => Info.MotionConfig.Move,
            });

            // 안전장치: 속도/가감속이 0이면 드라이브가 모션을 시작하지 않는다. 그러면 명령은 나갔지만
            // 실제로 안 움직이고 InPosition(=!IsMoving)이 즉시 통과해, 시퀀스가 '미이동 상태로 완료'된다
            // (실장 재현: 레시피 모터 속도 미설정 → X 미이동·초기화 완료). MotorConfig.json 에도 Move 프로파일이
            // 없어 fallback 이 0 이 되는 구조라, 여기서 안전 최소 프로파일로 보정하고 경고를 남긴다.
            if (profile.Velocity <= 0 || profile.Acceleration <= 0 || profile.Deceleration <= 0)
            {
                double v = profile.Velocity > 0 ? profile.Velocity
                         : (Info.MotionConfig.Move.Velocity > 0 ? Info.MotionConfig.Move.Velocity : 20.0);
                double a = profile.Acceleration > 0 ? profile.Acceleration : 200.0;
                double d = profile.Deceleration > 0 ? profile.Deceleration : 200.0;
                _mainViewModel.AddLog(
                    $"[MOTION] {Info.Name} 이동 프로파일 미설정(vel={profile.Velocity}) — 안전 최소값(vel={v}, acc={a}, dec={d}) 적용. 레시피 모터 속도를 설정하세요.",
                    LogLevel.Warning);
                profile = new Profile { Velocity = v, Acceleration = a, Deceleration = d };
            }

            try
            {
                if (IsAbsMode)
                {
                    // 절대 좌표 이동 (Absolute)
                    await _driver.MoveAbs(Info.AxisNo, TargetPosition,
                                         profile.Velocity, profile.Acceleration, profile.Deceleration);
                }
                else
                {
                    // 상대 좌표 이동 (Incremental / Relative)
                    await _driver.MoveRel(Info.AxisNo, TargetPosition,
                                         profile.Velocity, profile.Acceleration, profile.Deceleration);
                }
            }
            catch (Exception ex)
            {
                IsMoveActive = false;
                _mainViewModel.AddLog($"[MOTION] Move Failed: {ex.Message}", LogLevel.Error);
            }
        }
        private bool _isStepJogging = false;

        // 조그 프로파일 기본값 — 레시피에 해당 축의 조그 속도가 없을 때만 쓰인다.
        // Move 기본(20)보다 느리게 잡는다. 조그는 사람이 누르고 있는 동안 움직이는 수동 조작이라
        // '안 움직여서 못 쓰는' 것보다 '느리게라도 움직이는' 쪽이 낫고, 빠른 건 위험하다.
        private const double DefaultJogVelocity = 10.0;
        private const double DefaultJogAccel    = 100.0;
        private bool _jogProfileWarned;

        // 조그 속도/가감속 해석.
        // Jog 프로파일은 레시피(RecipeDetails_Motor)에서 채워지므로, 레시피에 그 축 행이 없으면 0 이 된다.
        // 속도 0 으로 명령을 내리면 드라이브가 모션을 시작하지 않아 '명령은 나갔는데 안 움직이는' 상태가 된다
        // — T·DW 축처럼 레시피에 등록되지 않은 축에서 실제로 발생. Move 쪽과 같은 방식으로 보정하고,
        // 조그는 연타 대상이라 경고는 축당 1회만 남긴다.
        private Profile ResolveJogProfile()
        {
            var p = Info.MotionConfig.Jog;
            if (p.Velocity > 0 && p.Acceleration > 0 && p.Deceleration > 0) return p;

            double v = p.Velocity     > 0 ? p.Velocity     : DefaultJogVelocity;
            double a = p.Acceleration > 0 ? p.Acceleration : DefaultJogAccel;
            double d = p.Deceleration > 0 ? p.Deceleration : DefaultJogAccel;

            if (!_jogProfileWarned)
            {
                _jogProfileWarned = true;
                _mainViewModel.AddLog(
                    $"[MOTION] {Info.Name} 조그 프로파일 미설정(vel={p.Velocity}) — 기본값(vel={v}, acc={a}, dec={d}) 적용. " +
                    "레시피 모터 속도를 설정하세요.", LogLevel.Warning);
            }
            return new Profile { Velocity = v, Acceleration = a, Deceleration = d };
        }

        // stepOverride : 화면이 정한 스텝(그 축의 논리단위, 0=연속). null 이면 축 자체의 JogUnit 을 쓴다(기존 화면들).
        //                위치 티칭 화면은 축이 아니라 화면이 스텝 모드를 들고 있어 이 인자로 넘긴다.
        public async Task JogMoveAsync(bool isForward, double speedScale = 1.0, double? stepOverride = null)
        {
            var profile = ResolveJogProfile();
            double vel  = profile.Velocity * Math.Max(0.01, speedScale);
            double step = stepOverride ?? JogUnit;
            string unit = string.IsNullOrWhiteSpace(Info.Unit) ? "mm" : Info.Unit;

            if (step <= 0)
            {
                string direction = isForward ? "Forward(+)" : "Backward(-)";
                _mainViewModel.AddLog($"[MOTION] Jog: {Info.Name} {direction} ×{speedScale:F2}");
                await _driver.MoveJog(Info.AxisNo, isForward, vel, profile.Acceleration, profile.Deceleration);
            }
            else
            {
                if (_isStepJogging) return;  // step 이동 중 중복 방지
                _isStepJogging = true;
                try
                {
                    string direction = isForward ? "+" : "-";
                    _mainViewModel.AddLog($"[MOTION] Jog: {Info.Name} Step {direction}{step:F3}{unit} ×{speedScale:F2}");
                    double distance = isForward ? step : -step;
                    await _driver.MoveRel(Info.AxisNo, distance, vel, profile.Acceleration, profile.Deceleration);
                }
                finally
                {
                    _isStepJogging = false;
                }
            }
        }

        public async Task StopAsync()
        {
            _isStepJogging = false;
            IsMoveActive = false;   // 진행률 표시 종료
            _mainViewModel.AddLog($"[MOTION] {Info.Name} Motor Stop Command.");
            await _driver.Stop(Info.AxisNo);
        }

        // 원점 복귀 메서드
        public async Task HomeAsync()
        {
            _mainViewModel.AddLog($"[MOTION] {Info.Name} Home Searching Start.");
            await _driver.Home(Info.AxisNo);
        }

        // 축 알람(서보 폴트) 리셋 — 드라이버 ResetAlarm 호출. 상태는 폴링으로 갱신됨.
        public async Task ResetAlarmAsync()
        {
            _mainViewModel.AddLog($"[MOTION] {Info.Name} (Axis:{Info.AxisNo}) Alarm Reset Command.");
            bool ok = await _driver.ResetAlarm(Info.AxisNo);
            if (!ok)
                _mainViewModel.AddLog($"[MOTION] {Info.Name} 알람 리셋 실패(무시하고 진행)", LogLevel.Warning);
        }

        // driver 의 실시간 IsInPosition (UpdateMotorStatus 100ms 캐시 우회)
        // 시퀀스 InPosition 폴링이 직전 step의 stale 값에 즉시 break되는 문제 방지용
        public bool IsDriverInPosition()
            => _driver?.GetStatus(Info.AxisNo)?.IsInPosition == true;

        // driver 의 실시간 IsServoOn.
        //
        // ForceServoOnAsync 는 명령을 보낸 뒤 Status.IsServoOn 을 <b>낙관적으로</b> true 로 놓는다
        // (화면이 곧바로 반응해야 하므로). 그래서 "정말 켜졌는가"를 묻는 자리에서는 캐시가 아니라
        // 드라이버에 직접 물어야 한다 — 안 그러면 안 켜진 축도 켜진 것으로 보인다.
        public bool IsDriverServoOn()
            => _driver?.GetStatus(Info.AxisNo)?.IsServoOn == true;

        // driver 의 실시간 위치(캐시 우회). null 이면 드라이버가 없거나 읽기에 실패한 것이다.
        //
        // ★ "도달했다"를 판정한 자리에서 위치를 <b>같은 기준으로</b> 찍기 위한 것이다.
        //   InPosition 은 드라이버에 직접 묻고 위치는 100ms 캐시에서 읽으면, 감속 중 값이 찍혀
        //   멀쩡히 선 축이 크게 어긋난 것처럼 보인다(2026-09-04 11호기 INITIALIZE:
        //   InPos 정상인데 로그에는 X 오차 -0.449mm, T 오차 +1.028deg — 실재하지 않는 오차였다).
        //   없는 오차를 쫓게 만드는 로그는 없느니만 못하다.
        public double? ReadDriverPosition()
            => _driver?.GetStatus(Info.AxisNo)?.CurrentPos;

        public void UpdateMotorStatus()
        {
            try
            {
                if (_driver == null || Status == null) return;

                // 하드웨어로부터 최신 상태 가져오기
                var latest = _driver.GetStatus(Info.AxisNo);

                if (latest != null)
                {
                    // 🚨 [추가] 하드웨어 알람 감지 및 통합 알람 팝업 연동
                    if (latest.IsAlarm)
                    {
                        // 기존에 알람 상태가 아니었다가 새로 발생한 경우에만 팝업을 띄움
                        if (!this.IsAlarm)
                        {
                            // 1. 로그 기록
                            _mainViewModel.AddLog($"[ALARM] Axis {Info.AxisNo} ({Info.Name}) — hardware alarm detected", LogLevel.Error);

                            // 2. 통합 알람 팝업창 호출 — 축 식별자 치환 ({0} → "X" 등)
                            _mainViewModel.AlarmVM.RaiseAlarm("MOT-AXIS-ALM", Info.Name);
                        }
                    }

                    // 상태 동기화
                    this.IsAlarm = latest.IsAlarm;

                    // Status 객체 속성 갱신
                    Status.CurrentPos = latest.CurrentPos;
                    Status.IsServoOn = latest.IsServoOn;
                    Status.IsHomeDone = latest.IsHomeDone;
                    Status.IsInPosition = latest.IsInPosition;
                    Status.IsAlarm = latest.IsAlarm;
                    Status.IsMoving = latest.IsMoving;

                    // 센서 상태 동기화
                    Status.UpperLimit = latest.UpperLimit;
                    Status.LowerLimit = latest.LowerLimit;
                    Status.HomeSensor = latest.HomeSensor;

                    // ViewModel 직속 속성 동기화 (UI 바인딩 가속)
                    this.CurrentPos = latest.CurrentPos;
                    this.IsServoOn = latest.IsServoOn;

                    // UI에 상태 변화 알림
                    OnPropertyChanged(nameof(Status));

                    // Move 진행률/남은거리 갱신 + 완료 판정
                    // 완료 = 정지 상태 && (모션을 한 번이라도 관측 || 목표 근접 20µm 이내)
                    //   → start 시점의 stale In-Position으로 인한 즉시 완료 오판 방지
                    if (IsMoveActive)
                    {
                        if (latest.IsMoving) _moveSawMotion = true;
                        bool reached = !latest.IsMoving &&
                                       (_moveSawMotion || Math.Abs(_moveTargetAbs - latest.CurrentPos) <= 0.02);
                        if (reached) IsMoveActive = false;

                        OnPropertyChanged(nameof(DistanceToGo));
                        OnPropertyChanged(nameof(MoveProgress));
                    }
                }
            }
            catch (Exception ex)
            {
                /* 통신 에러 로그 출력 */
                _mainViewModel.AddLog($"[MOTION] Axis {Info.AxisNo} comm error: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                // Move/Jog 허용 여부가 바뀌면 버튼 활성화 상태와 Move 커맨드를 갱신.
                // 연결 끊김/예외 시에도 반드시 반영되도록 finally에서 처리.
                bool canMoveNow = CanMove;
                if (canMoveNow != _lastCanMove)
                {
                    _lastCanMove = canMoveNow;
                    OnPropertyChanged(nameof(CanMove));
                    OnPropertyChanged(nameof(CanJog));
                    (MoveAbsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }
    }
}