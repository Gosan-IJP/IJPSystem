using IJPSystem.Machines.Pulse;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Common.Enums;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Services;
using static IJPSystem.Platform.HMI.Common.Loc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    // P&ID 화면 ViewModel — Head pack 인렛/리턴 밸브 (DO_PACK1_H1_IN ~ DO_PACK1_H8_OUT) 토글 제어
    // 프로퍼티 명은 IO.json Index 규약(Pack1HnIn/Pack1HnOut)을 따름
    public class PnidViewModel : ViewModelBase, IDisposable
    {
        private readonly MainViewModel _mainVM;
        private readonly PulseMachine? _machine;
        private DispatcherTimer? _refreshTimer;
        private int _suppressWrite;  // refresh 중에는 SetOutput 재호출 억제

        public PnidViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;
            _machine = mainVM.GetController()?.GetMachine() as PulseMachine;

            OpenAllHeadValvesCommand      = new RelayCommand(_ => SetAllHeadValves(true));
            CloseAllHeadValvesCommand     = new RelayCommand(_ => SetAllHeadValves(false));
            SetPressureCommand            = new RelayCommand(_ => ApplyPressureSV());
            SetVacuumCommand              = new RelayCommand(_ => ApplyVacuumSV());
            // 퍼지/스피팅 상호 배제 — 한쪽이 활성일 동안 다른쪽 버튼 비활성화
            TogglePurgeCommand            = new RelayCommand(_ => TogglePurge(),    _ => !IsSpitting);
            ToggleSpittingCommand         = new RelayCommand(_ => ToggleSpitting(), _ => !IsPurging);
            TogglePositivePressureCommand = new RelayCommand(_ => TogglePositivePressure());
            ToggleVacuumCommand           = new RelayCommand(_ => ToggleVacuum());
            AutoPurgeCommand              = new RelayCommand(async _ => await RunAutoPurgeAsync(),
                                                              _ => !_isAutoRunning);
            AutoBlottingCommand           = new RelayCommand(async _ => await RunAutoBlottingAsync(),
                                                              _ => !_isAutoRunning);
            StopAutoSequenceCommand       = new RelayCommand(_ => CancelAutoSequence(),
                                                              _ => _isAutoRunning);

            RefreshFromMachine();

            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshTimer.Tick += (_, __) => RefreshFromMachine();
            _refreshTimer.Start();
        }

        // ── 우측 패널 일괄 동작 커맨드 ──
        public ICommand OpenAllHeadValvesCommand      { get; }
        public ICommand CloseAllHeadValvesCommand     { get; }
        public ICommand TogglePurgeCommand            { get; }
        public ICommand ToggleSpittingCommand         { get; }
        public ICommand TogglePositivePressureCommand { get; }
        public ICommand ToggleVacuumCommand           { get; }
        public ICommand AutoPurgeCommand              { get; }
        public ICommand AutoBlottingCommand           { get; }
        public ICommand StopAutoSequenceCommand       { get; }

        // ── AUTO 시퀀스 실행 (Auto 퍼지 / Auto Blotting) ──
        private bool _isAutoRunning;
        // STOP 버튼이 외부에서 cancel 할 수 있도록 클래스 필드로 보유
        private CancellationTokenSource? _autoCts;

        public bool IsAutoRunning
        {
            get => _isAutoRunning;
            private set
            {
                if (SetProperty(ref _isAutoRunning, value))
                    Status.IsRunning = value;
            }
        }

        // SEQUENCE STATUS 패널 바인딩의 단일 소스 — XAML 은 {Binding Status.*} 로 접근
        public SequenceStatusViewModel Status { get; } = new();

        private void CancelAutoSequence()
        {
            if (!_isAutoRunning || _autoCts == null) return;
            _mainVM.AddLog("[SEQ] AUTO — 사용자 STOP 요청", LogLevel.Warning);
            try { _autoCts.Cancel(); } catch { /* 이미 dispose된 경우 무시 */ }

            // SetSequenceRunning(false) 는 RunAutoSequenceAsync 의 finally 에서만 해제.
            // → 시퀀스가 실제로 멈출 때(다음 await 지점에서 OperationCanceledException)까지
            //   화면 전환 차단을 유지하여 race condition 방지.
        }

        private Task RunAutoPurgeAsync() =>
            // 양압/음압 SV 는 PNID 화면 PMC 패널의 SET 버튼으로 입력한 값을 사용.
            // SET 으로 IO 에 적용된 값과 ViewModel SV 는 동기화되어 있으므로, 시퀀스가
            // step 7/9 에서 동일 값을 다시 인가해도 무방하다.
            RunAutoSequenceAsync("AUTO PURGE",
                (m, mo) => PurgeSequence.Build(m, mo, PressureSV, VacuumSV),
                "SEQ-PURGE-FAIL");

        private Task RunAutoBlottingAsync() =>
            RunAutoSequenceAsync("AUTO BLOTTING", BlottingSequence.Build, "SEQ-STEP-FAIL");

        // 공통 런너 — 5단계 로그 패턴(시작/단계/완료 또는 취소/타임아웃/실패) + Stopwatch + 알람
        private async Task RunAutoSequenceAsync(
            string name,
            Func<IMachine, IMotionService, IReadOnlyList<SequenceStepDef>> buildSteps,
            string failAlarmCode)
        {
            if (_machine == null)
            {
                _mainVM.AddLog($"[SEQ] {name} — 중단 (머신 미초기화)", LogLevel.Warning);
                return;
            }
            // 시퀀스 동작은 활성 레시피의 티칭 포인트를 참조하므로 적용된 레시피가 없으면 거부
            if (string.IsNullOrEmpty(_mainVM.RecipeVM.ActiveRecipeName))
            {
                _mainVM.AddLog($"[SEQ] {name} — 중단 (적용된 레시피 없음)", LogLevel.Warning);
                _mainVM.AlarmVM.RaiseAlarm("SEQ-NO-ACTIVE-RECIPE");
                return;
            }
            // 미해제 알람이 남아 있으면 시퀀스 시작 거부
            if (_mainVM.HasActiveAlarm)
            {
                _mainVM.AddLog($"[SEQ] {name} — 중단 (미해제 알람 존재)", LogLevel.Warning);
                Dialogs.Show(
                    "미해제 알람이 있습니다.\n알람 화면에서 알람을 모두 해제(Clear)한 뒤 다시 시도하세요.",
                    "시퀀스 시작 불가",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            // INITIALIZE 시퀀스(전체 축 원점복귀) 수행 여부 체크 — MainDashboardVM 의 사전 조건과 동일
            var allAxes = _machine.Motion?.GetAllStatus();
            if (allAxes == null || allAxes.Count == 0)
            {
                _mainVM.AddLog($"[SEQ] {name} — 중단 (축 정보 없음 — 모션 드라이버 확인)", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("SEQ-NO-AXIS");
                return;
            }
            var notHomed = allAxes.Where(ax => !ax.IsHomeDone).Select(ax => ax.AxisNo).ToList();
            if (notHomed.Count > 0)
            {
                _mainVM.AddLog($"[SEQ] {name} — 중단 (INITIALIZE 미수행, 미원점 축: {string.Join(", ", notHomed)})", LogLevel.Warning);
                Dialogs.Show(
                    $"INITIALIZE 시퀀스를 먼저 수행하세요.\n\n미원점 축: {string.Join(", ", notHomed)}",
                    "시퀀스 시작 불가",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (_isAutoRunning) return;

            IsAutoRunning = true;
            _mainVM.SetSequenceRunning(true);   // 실행 시작 → 화면 전환 차단
            CommandManager.InvalidateRequerySuggested();

            var sw     = Stopwatch.StartNew();
            var motion = new MotionServiceAdapter(_mainVM);
            var steps  = buildSteps(_machine, motion);
            _autoCts = new CancellationTokenSource();
            var token = _autoCts.Token;

            Status.Begin(name, steps.Count);

            _mainVM.AddLog($"[SEQ] {name} — 시작 ({steps.Count} 단계)", LogLevel.Info);

            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var step = steps[i];

                    Status.Advance(i + 1, T(step.Name));

                    // 퍼지 동작 애니메이션 동기화 — 양압 인가 후 토출 완료 직전까지 IsPurging=true
                    UpdatePurgingAnimation(step.Name);

                    await SequenceStepLogger.RunAsync(step, name, token, _mainVM.AddLog)
                                            .ConfigureAwait(false);
                }

                sw.Stop();
                _mainVM.AddLog($"[SEQ] {name} — 완료 ({sw.Elapsed.TotalSeconds:F1}s)", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _mainVM.AddLog($"[SEQ] {name} — 사용자 STOP 으로 중단됨 ({sw.Elapsed.TotalSeconds:F1}s)", LogLevel.Warning);
            }
            catch (TimeoutException ex)
            {
                _mainVM.AddLog($"[SEQ] {name} — 타임아웃: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("SEQ-MOTION-TIMEOUT");
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[SEQ] {name} — 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm(failAlarmCode);
            }
            finally
            {
                _autoCts?.Dispose();
                _autoCts = null;
                IsAutoRunning = false;
                _mainVM.SetSequenceRunning(false);   // 종료 → 화면 전환 허용

                Status.Reset();
                IsPurging = false;

                CommandManager.InvalidateRequerySuggested();
            }
        }

        // 시퀀스 step 진입 시 IsPurging(노즐 분사 애니메이션) 토글.
        // - Step_Purge_PressurizeOn 이후 (양압 인가) → 토출 시작
        // - Step_Purge_PressurizeOff 이후 (양압 해제) → 토출 종료
        // BlottingSequence 등 다른 시퀀스에서는 ValveOpen/Close 또는 본 메서드의 다른 분기로 자연스럽게 확장 가능.
        private void UpdatePurgingAnimation(string stepKey)
        {
            switch (stepKey)
            {
                case "Step_Purge_PressurizeOn":
                    IsPurging = true;
                    break;
                case "Step_Purge_PressurizeOff":
                    IsPurging = false;
                    break;
            }
        }

        // ── 양압(P+) / 음압(P-) ENABLE 상태 (PMC 배관 flow 애니메이션 트리거) ──
        private bool _isPositivePressureEnabled;
        public bool IsPositivePressureEnabled
        {
            get => _isPositivePressureEnabled;
            private set => SetProperty(ref _isPositivePressureEnabled, value);
        }

        private bool _isVacuumEnabled;
        public bool IsVacuumEnabled
        {
            get => _isVacuumEnabled;
            private set => SetProperty(ref _isVacuumEnabled, value);
        }

        // ── SV-101 (Canister → Reservoir 공급 밸브) 상태 — 배관 flow 트리거 ──
        private bool _sv101Open;
        public bool Sv101Open
        {
            get => _sv101Open;
            set => SetProperty(ref _sv101Open, value);
        }

        private void TogglePositivePressure()
        {
            IsPositivePressureEnabled = !IsPositivePressureEnabled;
            _mainVM.AddLog(
                $"[PNID] PMC: P+ ENABLE → {(IsPositivePressureEnabled ? "ON" : "OFF")}",
                LogLevel.Warning);
        }

        private void ToggleVacuum()
        {
            IsVacuumEnabled = !IsVacuumEnabled;
            _mainVM.AddLog(
                $"[PNID] PMC: P- ENABLE → {(IsVacuumEnabled ? "ON" : "OFF")}",
                LogLevel.Warning);
        }

        // ── 퍼지 동작 상태 (P&ID 노즐 애니메이션 트리거) ──
        // 헤드별 토출은 IsPurging && (해당 head의 In 또는 Out 밸브가 열림)일 때만 활성화
        private bool _isPurging;
        public bool IsPurging
        {
            get => _isPurging;
            private set
            {
                if (SetProperty(ref _isPurging, value))
                {
                    NotifyAllHeadPurging();
                    CommandManager.InvalidateRequerySuggested();  // ToggleSpittingCommand CanExecute 재평가
                }
            }
        }

        // ── Spit 동작 상태 (P&ID 노즐 애니메이션 트리거) ──
        // 헤드별 spit은 IsSpitting && HeadEnabled 이면 활성화 (밸브 무관)
        private bool _isSpitting;
        public bool IsSpitting
        {
            get => _isSpitting;
            private set
            {
                if (SetProperty(ref _isSpitting, value))
                {
                    NotifyAllHeadActive();
                    CommandManager.InvalidateRequerySuggested();  // TogglePurgeCommand CanExecute 재평가
                }
            }
        }

        // 헤드별 퍼지 활성 조건: IsPurging && HeadEnabled(사용 중) && (In 또는 Out 밸브 1개 이상 열림)
        public bool Head1Purging => IsPurging && _head1Enabled && (Pack1H1In || Pack1H1Out);
        public bool Head2Purging => IsPurging && _head2Enabled && (Pack1H2In || Pack1H2Out);
        public bool Head3Purging => IsPurging && _head3Enabled && (Pack1H3In || Pack1H3Out);
        public bool Head4Purging => IsPurging && _head4Enabled && (Pack1H4In || Pack1H4Out);
        public bool Head5Purging => IsPurging && _head5Enabled && (Pack1H5In || Pack1H5Out);
        public bool Head6Purging => IsPurging && _head6Enabled && (Pack1H6In || Pack1H6Out);
        public bool Head7Purging => IsPurging && _head7Enabled && (Pack1H7In || Pack1H7Out);
        public bool Head8Purging => IsPurging && _head8Enabled && (Pack1H8In || Pack1H8Out);

        // 헤드별 spit 활성 조건: 퍼지와 동일 (IsSpitting && HeadEnabled && (In || Out))
        public bool Head1Spitting => IsSpitting && _head1Enabled && (Pack1H1In || Pack1H1Out);
        public bool Head2Spitting => IsSpitting && _head2Enabled && (Pack1H2In || Pack1H2Out);
        public bool Head3Spitting => IsSpitting && _head3Enabled && (Pack1H3In || Pack1H3Out);
        public bool Head4Spitting => IsSpitting && _head4Enabled && (Pack1H4In || Pack1H4Out);
        public bool Head5Spitting => IsSpitting && _head5Enabled && (Pack1H5In || Pack1H5Out);
        public bool Head6Spitting => IsSpitting && _head6Enabled && (Pack1H6In || Pack1H6Out);
        public bool Head7Spitting => IsSpitting && _head7Enabled && (Pack1H7In || Pack1H7Out);
        public bool Head8Spitting => IsSpitting && _head8Enabled && (Pack1H8In || Pack1H8Out);

        // 노즐 애니메이션 통합 트리거 (XAML이 한 바인딩으로 퍼지/spit 둘 다 반응)
        public bool Head1Active => Head1Purging || Head1Spitting;
        public bool Head2Active => Head2Purging || Head2Spitting;
        public bool Head3Active => Head3Purging || Head3Spitting;
        public bool Head4Active => Head4Purging || Head4Spitting;
        public bool Head5Active => Head5Purging || Head5Spitting;
        public bool Head6Active => Head6Purging || Head6Spitting;
        public bool Head7Active => Head7Purging || Head7Spitting;
        public bool Head8Active => Head8Purging || Head8Spitting;

        // 8개 헤드 중 하나라도 퍼지 중 → bath → bottle3 드레인 flow 애니메이션 트리거
        // spit은 잉크가 드레인 bath로 가지 않으므로 포함하지 않음
        public bool AnyHeadPurging =>
            Head1Purging || Head2Purging || Head3Purging || Head4Purging ||
            Head5Purging || Head6Purging || Head7Purging || Head8Purging;

        private void NotifyAllHeadPurging()
        {
            for (int n = 1; n <= 8; n++)
            {
                OnPropertyChanged($"Head{n}Purging");
                OnPropertyChanged($"Head{n}Active");
            }
            OnPropertyChanged(nameof(AnyHeadPurging));
        }

        // MainVM.IsSpitting 변경 또는 HeadEnabled 토글 시 → 모든 헤드 spit/active 갱신
        private void NotifyAllHeadActive()
        {
            for (int n = 1; n <= 8; n++)
            {
                OnPropertyChanged($"Head{n}Spitting");
                OnPropertyChanged($"Head{n}Active");
            }
        }

        private void TogglePurge()
        {
            IsPurging = !IsPurging;
            _mainVM.AddLog(
                $"[PNID] PURGE → {(IsPurging ? "ON (잉크 토출 중)" : "OFF")}",
                LogLevel.Warning);
        }

        private void ToggleSpitting()
        {
            IsSpitting = !IsSpitting;
            _mainVM.AddLog(
                $"[PNID] SPITTING → {(IsSpitting ? "ON (노즐 분사 중)" : "OFF")}",
                LogLevel.Warning);
        }

        // ── PMC PV / SV / Set 커맨드 ──
        // 양압(P+, Purge)  : PV ← AI X6002, SV ← AO Y7001
        // 음압(P-, Meniscus): PV ← AI X6001, SV ← AO Y7000
        private const string AI_PURGE_PRESSURE    = "AI_CH_1_PURGE_PRESSURE";
        private const string AI_MENISCUS_PRESSURE = "AI_CH_1_MENISCUS_PRESSURE";
        private const string AO_TARGET_PURGE      = "AO_CH_1_TARGET_PURGE";
        private const string AO_TARGET_MENISCUS   = "AO_CH_1_TARGET_MENISCUS";

        private double _pressurePV, _pressureSV;
        private double _vacuumPV,   _vacuumSV;
        public double PressurePV { get => _pressurePV; private set => SetProperty(ref _pressurePV, value); }
        public double PressureSV { get => _pressureSV; set => SetProperty(ref _pressureSV, value); }
        public double VacuumPV   { get => _vacuumPV;   private set => SetProperty(ref _vacuumPV,   value); }
        public double VacuumSV   { get => _vacuumSV;   set => SetProperty(ref _vacuumSV,   value); }
        public ICommand SetPressureCommand { get; }
        public ICommand SetVacuumCommand   { get; }

        private void ApplyPressureSV()
        {
            if (_machine?.IO == null) return;
            _machine.IO.SetAnalogOutput(AO_TARGET_PURGE, PressureSV);
            _mainVM.AddLog($"[PNID] PMC: 양압 SV 적용 → {PressureSV:F2} kPa", LogLevel.Warning);
        }

        private void ApplyVacuumSV()
        {
            if (_machine?.IO == null) return;
            _machine.IO.SetAnalogOutput(AO_TARGET_MENISCUS, VacuumSV);
            _mainVM.AddLog($"[PNID] PMC: 음압 SV 적용 → {VacuumSV:F2} kPa", LogLevel.Warning);
        }

        // 16개 head 밸브(Y700~Y715) 일괄 OPEN/CLOSE
        // OPEN: HeadXEnabled=true 인 head 만 열고, 비활성 head 는 닫음
        // CLOSE: 16개 일괄 닫기
        private void SetAllHeadValves(bool open)
        {
            if (_machine == null) return;

            if (open)
            {
                for (int i = 1; i <= 8; i++)
                {
                    bool enabled = IsHeadEnabled(i);
                    _machine.HeadInletIn(i, enabled);
                    _machine.HeadInletOut(i, enabled);
                }
            }
            else
            {
                _machine.AllHeadInletsIn(false);
                _machine.AllHeadInletsOut(false);
            }

            RefreshFromMachine();  // 500ms tick 기다리지 않고 즉시 UI 동기화
            _mainVM.AddLog(
                $"[PNID] 전체 head valve → {(open ? "OPEN (사용 중인 head 만)" : "CLOSE (16개)")} (Y700~Y715)",
                LogLevel.Warning);
        }

        // ── Head pack 밸브 16개 (TwoWay 바인딩 대상) ──
        // Pack1HnIn  : DO_PACK1_H{n}_IN  (Y{698+2n})
        // Pack1HnOut : DO_PACK1_H{n}_OUT (Y{699+2n})
        private bool _pack1H1In,  _pack1H1Out, _pack1H2In, _pack1H2Out;
        private bool _pack1H3In,  _pack1H3Out, _pack1H4In, _pack1H4Out;
        private bool _pack1H5In,  _pack1H5Out, _pack1H6In, _pack1H6Out;
        private bool _pack1H7In,  _pack1H7Out, _pack1H8In, _pack1H8Out;

        public bool Pack1H1In  { get => _pack1H1In;  set => SetValve(ref _pack1H1In,  value, 1, true,  nameof(Pack1H1In));  }
        public bool Pack1H1Out { get => _pack1H1Out; set => SetValve(ref _pack1H1Out, value, 1, false, nameof(Pack1H1Out)); }
        public bool Pack1H2In  { get => _pack1H2In;  set => SetValve(ref _pack1H2In,  value, 2, true,  nameof(Pack1H2In));  }
        public bool Pack1H2Out { get => _pack1H2Out; set => SetValve(ref _pack1H2Out, value, 2, false, nameof(Pack1H2Out)); }
        public bool Pack1H3In  { get => _pack1H3In;  set => SetValve(ref _pack1H3In,  value, 3, true,  nameof(Pack1H3In));  }
        public bool Pack1H3Out { get => _pack1H3Out; set => SetValve(ref _pack1H3Out, value, 3, false, nameof(Pack1H3Out)); }
        public bool Pack1H4In  { get => _pack1H4In;  set => SetValve(ref _pack1H4In,  value, 4, true,  nameof(Pack1H4In));  }
        public bool Pack1H4Out { get => _pack1H4Out; set => SetValve(ref _pack1H4Out, value, 4, false, nameof(Pack1H4Out)); }
        public bool Pack1H5In  { get => _pack1H5In;  set => SetValve(ref _pack1H5In,  value, 5, true,  nameof(Pack1H5In));  }
        public bool Pack1H5Out { get => _pack1H5Out; set => SetValve(ref _pack1H5Out, value, 5, false, nameof(Pack1H5Out)); }
        public bool Pack1H6In  { get => _pack1H6In;  set => SetValve(ref _pack1H6In,  value, 6, true,  nameof(Pack1H6In));  }
        public bool Pack1H6Out { get => _pack1H6Out; set => SetValve(ref _pack1H6Out, value, 6, false, nameof(Pack1H6Out)); }
        public bool Pack1H7In  { get => _pack1H7In;  set => SetValve(ref _pack1H7In,  value, 7, true,  nameof(Pack1H7In));  }
        public bool Pack1H7Out { get => _pack1H7Out; set => SetValve(ref _pack1H7Out, value, 7, false, nameof(Pack1H7Out)); }
        public bool Pack1H8In  { get => _pack1H8In;  set => SetValve(ref _pack1H8In,  value, 8, true,  nameof(Pack1H8In));  }
        public bool Pack1H8Out { get => _pack1H8Out; set => SetValve(ref _pack1H8Out, value, 8, false, nameof(Pack1H8Out)); }

        // ── 8 Print Head 사용/미사용 (소프트웨어 레벨 ENABLE — 시퀀스 시 미사용 head 스킵) ──
        // 기본값 true (모두 사용). P&ID에서 head body 클릭으로 토글
        private bool _head1Enabled = true, _head2Enabled = true, _head3Enabled = true, _head4Enabled = true;
        private bool _head5Enabled = true, _head6Enabled = true, _head7Enabled = true, _head8Enabled = true;

        public bool Head1Enabled { get => _head1Enabled; set => SetHeadEnabled(ref _head1Enabled, value, 1, nameof(Head1Enabled)); }
        public bool Head2Enabled { get => _head2Enabled; set => SetHeadEnabled(ref _head2Enabled, value, 2, nameof(Head2Enabled)); }
        public bool Head3Enabled { get => _head3Enabled; set => SetHeadEnabled(ref _head3Enabled, value, 3, nameof(Head3Enabled)); }
        public bool Head4Enabled { get => _head4Enabled; set => SetHeadEnabled(ref _head4Enabled, value, 4, nameof(Head4Enabled)); }
        public bool Head5Enabled { get => _head5Enabled; set => SetHeadEnabled(ref _head5Enabled, value, 5, nameof(Head5Enabled)); }
        public bool Head6Enabled { get => _head6Enabled; set => SetHeadEnabled(ref _head6Enabled, value, 6, nameof(Head6Enabled)); }
        public bool Head7Enabled { get => _head7Enabled; set => SetHeadEnabled(ref _head7Enabled, value, 7, nameof(Head7Enabled)); }
        public bool Head8Enabled { get => _head8Enabled; set => SetHeadEnabled(ref _head8Enabled, value, 8, nameof(Head8Enabled)); }

        private void SetHeadEnabled(ref bool field, bool value, int headNo, string propName)
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(propName);
            // 사용 토글 시 해당 head 퍼지/spit 애니메이션 즉시 반영
            OnPropertyChanged($"Head{headNo}Purging");
            OnPropertyChanged($"Head{headNo}Spitting");
            OnPropertyChanged($"Head{headNo}Active");
            OnPropertyChanged(nameof(AnyHeadPurging));
            _mainVM.AddLog(
                $"[PNID] H{headNo} → {(value ? "ENABLED (사용)" : "DISABLED (미사용)")}",
                LogLevel.Info);
        }

        private bool IsHeadEnabled(int n) => n switch
        {
            1 => _head1Enabled, 2 => _head2Enabled, 3 => _head3Enabled, 4 => _head4Enabled,
            5 => _head5Enabled, 6 => _head6Enabled, 7 => _head7Enabled, 8 => _head8Enabled,
            _ => false,
        };

        // ── Reservoir 수위 센서 (DI_RESERVOIR_*) ──
        private bool _reservoirOverflow;
        private bool _reservoirHigh;
        private bool _reservoirSet;
        private bool _reservoirEmpty;
        private LevelStatus _reservoirLevel = LevelStatus.Unknown;

        public bool ReservoirOverflow { get => _reservoirOverflow; private set => SetProperty(ref _reservoirOverflow, value); }
        public bool ReservoirHigh     { get => _reservoirHigh;     private set => SetProperty(ref _reservoirHigh,     value); }
        public bool ReservoirSet      { get => _reservoirSet;      private set => SetProperty(ref _reservoirSet,      value); }
        public bool ReservoirEmpty    { get => _reservoirEmpty;    private set => SetProperty(ref _reservoirEmpty,    value); }

        // ── Bottle 센서 매핑 ──
        //   Bottle 1 = Canister (CN-101, 잉크 공급)   ← BOTTLE_1_*
        //   Bottle 2 = (추후 진행)                     ← BOTTLE_2_MT_* (현재 미사용)
        //   Bottle 3 = DB-101   (BATH/Purge 드레인)   ← BOTTLE_3_MT_*
        //   Bottle 4 = DB-102   (Slot Die 드레인)     ← BOTTLE_4_MT_* (HIGH는 IO 미존재 → false)
        // 각 보틀 3 센서: Detect (보틀 감지), Leak (누액), High (액위 상한)
        private bool _bottle1Detect, _bottle1Leak, _bottle1High;
        private bool _bottle3Detect, _bottle3Leak, _bottle3High;
        private bool _bottle4Detect, _bottle4Leak, _bottle4High;

        public bool Bottle1Detect { get => _bottle1Detect; private set => SetProperty(ref _bottle1Detect, value); }
        public bool Bottle1Leak   { get => _bottle1Leak;   private set => SetProperty(ref _bottle1Leak,   value); }
        public bool Bottle1High   { get => _bottle1High;   private set => SetProperty(ref _bottle1High,   value); }
        public bool Bottle3Detect { get => _bottle3Detect; private set => SetProperty(ref _bottle3Detect, value); }
        public bool Bottle3Leak   { get => _bottle3Leak;   private set => SetProperty(ref _bottle3Leak,   value); }
        public bool Bottle3High   { get => _bottle3High;   private set => SetProperty(ref _bottle3High,   value); }
        public bool Bottle4Detect { get => _bottle4Detect; private set => SetProperty(ref _bottle4Detect, value); }
        public bool Bottle4Leak   { get => _bottle4Leak;   private set => SetProperty(ref _bottle4Leak,   value); }
        public bool Bottle4High   { get => _bottle4High;   private set => SetProperty(ref _bottle4High,   value); }

        // 4점 센서 → liquid 레벨 표시 (Reservoir 컨트롤의 LevelStatus 바인딩)
        // 센서 규약: 액위가 해당 센서 위치에 도달하면 true
        //   Overflow=true                     → HH    (알람)
        //   !Overflow & High=true             → High
        //   !High & Set=true                  → Set
        //   !Set & Empty=true                 → Low   (Empty 센서 위, Set 미달)
        //   !Empty                            → Empty (Empty 센서 아래)
        public LevelStatus ReservoirLevel { get => _reservoirLevel; private set => SetProperty(ref _reservoirLevel, value); }

        private void SetValve(ref bool field, bool value, int headNo, bool isInlet, string propName)
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(propName);
            OnPropertyChanged($"Head{headNo}Purging");
            OnPropertyChanged($"Head{headNo}Spitting");
            OnPropertyChanged($"Head{headNo}Active");
            OnPropertyChanged(nameof(AnyHeadPurging));

            if (_suppressWrite > 0 || _machine == null) return;

            if (isInlet) _machine.HeadInletIn(headNo, value);
            else         _machine.HeadInletOut(headNo, value);

            string port = isInlet ? "In" : "Out";
            _mainVM.AddLog(
                $"[PNID] H{headNo} {port} → {(value ? "OPEN" : "CLOSE")}",
                LogLevel.Warning);
        }

        // 외부에서 IO가 바뀐 경우 UI 동기화 (시퀀스/포스 출력 등)
        private void RefreshFromMachine()
        {
            if (_machine == null) return;

            _suppressWrite++;
            try
            {
                Pack1H1In  = _machine.IsHeadInletInOn(1);
                Pack1H1Out = _machine.IsHeadInletOutOn(1);
                Pack1H2In  = _machine.IsHeadInletInOn(2);
                Pack1H2Out = _machine.IsHeadInletOutOn(2);
                Pack1H3In  = _machine.IsHeadInletInOn(3);
                Pack1H3Out = _machine.IsHeadInletOutOn(3);
                Pack1H4In  = _machine.IsHeadInletInOn(4);
                Pack1H4Out = _machine.IsHeadInletOutOn(4);
                Pack1H5In  = _machine.IsHeadInletInOn(5);
                Pack1H5Out = _machine.IsHeadInletOutOn(5);
                Pack1H6In  = _machine.IsHeadInletInOn(6);
                Pack1H6Out = _machine.IsHeadInletOutOn(6);
                Pack1H7In  = _machine.IsHeadInletInOn(7);
                Pack1H7Out = _machine.IsHeadInletOutOn(7);
                Pack1H8In  = _machine.IsHeadInletInOn(8);
                Pack1H8Out = _machine.IsHeadInletOutOn(8);

                // Reservoir 수위 센서
                ReservoirOverflow = _machine.IsReservoirOverflow();
                ReservoirHigh     = _machine.IsReservoirHigh();
                ReservoirSet      = _machine.IsReservoirSet();
                ReservoirEmpty    = _machine.IsReservoirEmpty();

                // Bottle 센서 (Canister=B1, DB-101=B3, DB-102=B4. Bottle2는 추후 진행)
                Bottle1Detect = _machine.IsInkBottleDetected();
                Bottle1Leak   = _machine.IsInkBottleLeak();
                Bottle1High   = _machine.IsInkBottleLevelHigh();

                Bottle3Detect = _machine.IsMtBottleDetected(3);
                Bottle3Leak   = _machine.IsMtBottleLeak(3);
                Bottle3High   = _machine.IsMtBottleLevelHigh(3);

                Bottle4Detect = _machine.IsMtBottleDetected(4);
                Bottle4Leak   = _machine.IsMtBottleLeak(4);
                Bottle4High   = _machine.IsMtBottleLevelHigh(4); // 4번 HIGH 미정의 → false

                // PMC PV (AI에서 읽기) — SV는 사용자 입력값 유지, 외부 변경 시 동기화는 별도 처리
                if (_machine.IO != null)
                {
                    PressurePV = _machine.IO.GetAnalogInput(AI_PURGE_PRESSURE);
                    VacuumPV   = _machine.IO.GetAnalogInput(AI_MENISCUS_PRESSURE);
                    // 최초 진입 시 SV를 현재 AO 값으로 초기화 (사용자가 아직 편집하지 않은 경우)
                    if (_pressureSV == 0.0) _pressureSV = _machine.IO.GetAnalogOutput(AO_TARGET_PURGE);
                    if (_vacuumSV   == 0.0) _vacuumSV   = _machine.IO.GetAnalogOutput(AO_TARGET_MENISCUS);
                }
                ReservoirLevel = ReservoirOverflow ? LevelStatus.HH
                               : ReservoirHigh     ? LevelStatus.High
                               : ReservoirSet      ? LevelStatus.Set
                               : ReservoirEmpty    ? LevelStatus.Low
                                                   : LevelStatus.Empty;
            }
            finally
            {
                _suppressWrite--;
            }
        }

        public void Dispose()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer = null;
            }
            // 화면 닫힐 때 진행 중인 시퀀스가 있으면 취소
            try { _autoCts?.Cancel(); } catch { }
            _autoCts?.Dispose();
            _autoCts = null;
        }
    }
}
