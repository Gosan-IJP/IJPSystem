using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    // ── UI 바인딩용 단계 모델 (HMI 전용) ─────────────────────────────────────
    public class SequenceStep : ViewModelBase
    {
        public int Number  { get; init; }
        // 원본 번역 키 — 언어 변경 시 Name 을 다시 계산하기 위해 보존
        public string NameKey { get; init; } = "";

        // 표시명 — 보통 Loc.T(NameKey) 결과. 언어 변경 시 ViewModel 이 재할당.
        private string _name = "";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public Func<CancellationToken, Task> Action { get; init; } = _ => Task.CompletedTask;

        private StepStatus _status = StepStatus.Waiting;
        public StepStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusIcon));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        private string _elapsed = "-";
        public string Elapsed
        {
            get => _elapsed;
            set => SetProperty(ref _elapsed, value);
        }

        public string StatusIcon => Status switch
        {
            StepStatus.Waiting => "⬜",
            StepStatus.Running => "▶",
            StepStatus.Done    => "✅",
            StepStatus.Failed  => "❌",
            StepStatus.Aborted => "⏹",
            _ => ""
        };

        public string StatusText => Status switch
        {
            StepStatus.Waiting => "Wait",
            StepStatus.Running => "Running",
            StepStatus.Done    => "Completed",
            StepStatus.Failed  => "Failed",
            StepStatus.Aborted => "Stopped",
            _ => ""
        };

        public bool IsRunning => Status == StepStatus.Running;
    }

    // ── SequenceViewModel ─────────────────────────────────────────────────────
    public class SequenceViewModel : ViewModelBase
    {
        private readonly MainViewModel      _mainVM;
        private readonly MotionServiceAdapter _motion;
        private CancellationTokenSource?    _cts;
        private readonly ManualResetEventSlim _pauseGate = new(true);

        private readonly RelayCommand _startCmd;
        private readonly RelayCommand _pauseResumeCmd;
        private readonly RelayCommand _abortCmd;

        public ObservableCollection<SequenceDefinition> Sequences   { get; } = new();
        public ObservableCollection<SequenceStep>       ActiveSteps { get; } = new();
        public ObservableCollection<string>             ExecutionLogs { get; } = new();

        private SequenceDefinition? _selectedSequence;
        public SequenceDefinition? SelectedSequence
        {
            get => _selectedSequence;
            set
            {
                if (SetProperty(ref _selectedSequence, value))
                {
                    RebuildActiveSteps();
                    RefreshCommands();
                }
            }
        }

        private SequenceState _state = SequenceState.Idle;
        public SequenceState State
        {
            get => _state;
            set
            {
                if (SetProperty(ref _state, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateBadgeColor));
                    OnPropertyChanged(nameof(PauseResumeLabel));
                    RefreshCommands();
                }
            }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        private string _progressText = "0 / 0";
        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }

        public string StateText => State switch
        {
            SequenceState.Idle      => "READY",
            SequenceState.Running   => "RUNNING",
            SequenceState.Paused    => "PAUSED",
            SequenceState.Completed => "DONE",
            SequenceState.Aborted   => "ABORTED",
            SequenceState.Error     => "ERROR",
            _ => "READY"
        };

        public string StateBadgeColor => State switch
        {
            SequenceState.Running   => "#1D4ED8",
            SequenceState.Paused    => "#92400E",
            SequenceState.Completed => "#166534",
            SequenceState.Aborted   => "#7C2D12",
            SequenceState.Error     => "#7F1D1D",
            _ => "#334155"
        };

        public string PauseResumeLabel => State == SequenceState.Paused ? "▶  RESUME" : "⏸  PAUSE";

        public ICommand StartCommand       => _startCmd;
        public ICommand PauseResumeCommand => _pauseResumeCmd;
        public ICommand AbortCommand       => _abortCmd;

        public SequenceViewModel(MainViewModel mainViewModel)
        {
            _mainVM = mainViewModel;
            _motion = new MotionServiceAdapter(mainViewModel);

            _startCmd = new RelayCommand(
                async _ => await RunSequenceAsync(),
                _ => SelectedSequence != null &&
                     State is SequenceState.Idle or SequenceState.Completed
                          or SequenceState.Aborted or SequenceState.Error);

            _pauseResumeCmd = new RelayCommand(
                _ => TogglePause(),
                _ => State is SequenceState.Running or SequenceState.Paused);

            _abortCmd = new RelayCommand(
                _ => Abort(),
                _ => State is SequenceState.Running or SequenceState.Paused);

            // Registry 로부터 SequenceDefinition 받아오면서 현재 언어로 Name/Description 번역
            foreach (var def in SequenceRegistry.GetAll())
            {
                def.Name        = IJPSystem.Platform.HMI.Common.Loc.T(def.NameKey);
                def.Description = IJPSystem.Platform.HMI.Common.Loc.T(def.DescriptionKey);
                Sequences.Add(def);
            }

            SelectedSequence = Sequences.FirstOrDefault();
        }

        // ── 실행 엔진 ─────────────────────────────────────────────────────────
        private async Task RunSequenceAsync()
        {
            if (SelectedSequence == null) return;

            // 시퀀스 동작은 활성 레시피의 티칭 포인트를 참조하므로 적용된 레시피가 없으면 거부
            if (string.IsNullOrEmpty(_mainVM.RecipeVM.ActiveRecipeName))
            {
                _mainVM.AddLog($"[SEQ] {SelectedSequence.Name} 중단 — 적용된 레시피 없음", LogLevel.Warning);
                _mainVM.AlarmVM.RaiseAlarm("SEQ-NO-ACTIVE-RECIPE");
                return;
            }

            // 미해제 알람이 남아 있으면 시퀀스 시작 거부 (알람 클리어 후 재시도)
            if (_mainVM.HasActiveAlarm)
            {
                _mainVM.AddLog($"[SEQ] {SelectedSequence.Name} 중단 — 미해제 알람 존재", LogLevel.Warning);
                Dialogs.Show(
                    "미해제 알람이 있습니다.\n알람 화면에서 알람을 모두 해제(Clear)한 뒤 다시 시도하세요.",
                    "시퀀스 시작 불가",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            _cts = new CancellationTokenSource();
            _pauseGate.Set();

            // 시퀀스 실제 실행 시작 — 화면 전환 차단 ON (try/finally 로 OFF 보장)
            _mainVM.SetSequenceRunning(true);
            try
            {

            RebuildActiveSteps();
            State    = SequenceState.Running;
            Progress = 0; //진행율 초기화
            ExecutionLogs.Clear(); //로그 초기화

            int total = ActiveSteps.Count;
            AddExecLog($"[{SelectedSequence.Name}] 시퀀스 시작 ({total} 단계)");
            _mainVM.AddLog($"[SEQ] {SelectedSequence.Name} 시작", LogLevel.Info);

            for (int i = 0; i < ActiveSteps.Count; i++)
            {
                try { await Task.Run(() => _pauseGate.Wait(_cts.Token), _cts.Token); }
                catch (OperationCanceledException) { break; }

                if (_cts.Token.IsCancellationRequested) break;

                var step = ActiveSteps[i];
                step.Status  = StepStatus.Running;
                step.Elapsed = "-";
                UpdateProgress(i, total);
                AddExecLog($"Step {step.Number}  {step.Name}");

                var sw = Stopwatch.StartNew();
                try
                {
                    await SequenceStepLogger.RunAsync(
                        step.Number, step.NameKey, step.Action,
                        SelectedSequence?.Name ?? "Sequence", _cts.Token, _mainVM.AddLog);
                    sw.Stop();
                    step.Elapsed = $"{sw.Elapsed.TotalSeconds:F1}s";
                    step.Status  = StepStatus.Done;
                    UpdateProgress(i + 1, total);
                    AddExecLog($"  → 완료 ({step.Elapsed})");
                }
                catch (OperationCanceledException)
                {
                    step.Status = StepStatus.Aborted;
                    break;
                }
                catch (TimeoutException ex)
                {
                    step.Status = StepStatus.Failed;
                    AddExecLog($"  → 타임아웃: {ex.Message}");
                    _mainVM.AddLog($"[SEQ] Step {step.Number} 타임아웃: {ex.Message}", LogLevel.Error);
                    State = SequenceState.Error;
                    _mainVM.AlarmVM.RaiseAlarm("SEQ-STEP-TIMEOUT");
                    return;
                }
                catch (Exception ex)
                {
                    step.Status = StepStatus.Failed;
                    AddExecLog($"  → 실패: {ex.Message}");
                    _mainVM.AddLog($"[SEQ] Step {step.Number} 실패: {ex.Message}", LogLevel.Error);
                    State = SequenceState.Error;
                    _mainVM.AlarmVM.RaiseAlarm("SEQ-STEP-FAIL");
                    return;
                }
            }

            if (_cts.IsCancellationRequested)
            {
                State = SequenceState.Aborted;
                AddExecLog("⏹ 시퀀스 중단됨");
                _mainVM.AddLog($"[SEQ] {SelectedSequence.Name} 중단", LogLevel.Warning);
            }
            else
            {
                State    = SequenceState.Completed;
                Progress = 100;
                AddExecLog($"✅ [{SelectedSequence.Name}] 완료");
                _mainVM.AddLog($"[SEQ] {SelectedSequence.Name} 완료", LogLevel.Success);
            }

            }
            finally
            {
                _mainVM.SetSequenceRunning(false);
            }
        }

        private void TogglePause()
        {
            if (State == SequenceState.Running)
            {
                _pauseGate.Reset();
                State = SequenceState.Paused;
                _mainVM.SetSequenceRunning(false);   // 일시정지 → 화면 전환 허용
                AddExecLog("⏸ 일시정지 — 현재 단계 완료 후 정지됩니다");
            }
            else if (State == SequenceState.Paused)
            {
                _pauseGate.Set();
                State = SequenceState.Running;
                _mainVM.SetSequenceRunning(true);    // 재개 → 화면 전환 차단
                AddExecLog("▶ 재개됨");
            }
        }

        private void Abort()
        {
            _pauseGate.Set();
            _cts?.Cancel();
        }

        private void RebuildActiveSteps()
        {
            ActiveSteps.Clear();
            if (SelectedSequence == null) return;
            var machine = _mainVM.GetController().GetMachine();
            foreach (var def in SelectedSequence.BuildSteps(machine, _motion))
                ActiveSteps.Add(new SequenceStep
                {
                    Number  = def.Number,
                    NameKey = def.Name,
                    Name    = IJPSystem.Platform.HMI.Common.Loc.T(def.Name),
                    Action  = def.Action,
                });
        }

        /// <summary>언어 변경 시 호출 — 진행 중이어도 표시명만 갱신</summary>
        public void RefreshStepNames()
        {
            foreach (var s in ActiveSteps)
                s.Name = IJPSystem.Platform.HMI.Common.Loc.T(s.NameKey);

            // Sequences (선택 리스트)의 Name/Description 도 키 기반이면 재번역
            foreach (var def in Sequences)
            {
                def.Name        = IJPSystem.Platform.HMI.Common.Loc.T(def.NameKey);
                def.Description = IJPSystem.Platform.HMI.Common.Loc.T(def.DescriptionKey);
            }
        }

        private void UpdateProgress(int done, int total)
        {
            Progress     = total == 0 ? 0 : (double)done / total * 100;
            ProgressText = $"{done} / {total}  ({(int)Progress}%)";
        }

        private void AddExecLog(string message)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}]  {message}";
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ExecutionLogs.Insert(0, entry);
                if (ExecutionLogs.Count > 50) ExecutionLogs.RemoveAt(ExecutionLogs.Count - 1);
            });
        }

        private void RefreshCommands()
        {
            _startCmd.RaiseCanExecuteChanged();
            _pauseResumeCmd.RaiseCanExecuteChanged();
            _abortCmd.RaiseCanExecuteChanged();
        }
    }
}
