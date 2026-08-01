using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>
    /// INITIALIZE 전용 화면 — 서보ON/원점복귀/READY 위치 이동을 단계별 실행
    /// </summary>
    public class InitializeViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;
        private readonly IMachine _machine;
        private readonly IMotionService _motion;
        private CancellationTokenSource? _cts;

        public ObservableCollection<SequenceStep> Steps { get; } = new();
        public ObservableCollection<string> ExecutionLogs { get; } = new();

        // 12축 motor status / position 표시용 (MainViewModel의 SharedAxisList 노출)
        public ObservableCollection<AxisViewModel> Axes => _mainVM.SharedAxisList;

        // 드라이브 준비상태(🔴/🟡/🟢) — 상시 구동되는 대시보드 VM 것을 그대로 노출.
        // INIT 버튼 옆 표시등이 이 값을 바인딩한다(별도 폴링 불필요).
        public MainDashboardViewModel Dashboard => _mainVM.DashboardVM;

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateColor));
                    _startCmd.RaiseCanExecuteChanged();
                    _stopCmd.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set
            {
                if (SetProperty(ref _isError, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateColor));
                }
            }
        }

        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (SetProperty(ref _isCompleted, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateColor));
                }
            }
        }

        private string _currentStepName = "IDLE";
        public string CurrentStepName
        {
            get => _currentStepName;
            set => SetProperty(ref _currentStepName, value);
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set
            {
                if (SetProperty(ref _progress, value))
                    OnPropertyChanged(nameof(ProgressText));
            }
        }

        public string ProgressText => $"{(int)Progress}%";

        public string StateText =>
            IsError     ? "ERROR"
          : IsRunning   ? "RUNNING"
          : IsCompleted ? "DONE"
                        : "READY";

        public string StateColor =>
            IsError     ? "#7F1D1D"
          : IsRunning   ? "#1D4ED8"
          : IsCompleted ? "#166534"
                        : "#334155";

        private readonly RelayCommand _startCmd;
        private readonly RelayCommand _stopCmd;
        public ICommand StartCommand => _startCmd;
        public ICommand StopCommand  => _stopCmd;

        public InitializeViewModel(MainViewModel mainVM)
        {
            _mainVM  = mainVM;
            _machine = mainVM.GetController().GetMachine();
            _motion  = new MotionServiceAdapter(mainVM);

            _startCmd = new RelayCommand(async _ => await RunAsync(), _ => !IsRunning);
            _stopCmd  = new RelayCommand(_ => _cts?.Cancel(),         _ =>  IsRunning);

            BuildSteps();
        }

        private void BuildSteps()
        {
            // def.Name 은 번역 키 (Step_Init_N) → 현재 언어로 변환해서 표시.
            // NameKey 도 보존해 언어 변경 시 RefreshStepNames() 로 재번역.
            Steps.Clear();
            foreach (var def in InitializeSequence.Build(_machine, _motion))
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

        private async Task RunAsync()
        {
            // 미해제 알람이 남아 있으면 시퀀스 시작 거부 — Sequence/Pnid/MainDashboard 와 동일 가드
            if (_mainVM.HasActiveAlarm)
            {
                _mainVM.AddLog("[SEQ] Initialize — 중단 (미해제 알람 존재)", LogLevel.Warning);
                Dialogs.Show(
                    "미해제 알람이 있습니다.\n알람 화면에서 알람을 모두 해제(Clear)한 뒤 다시 시도하세요.",
                    "Initialize 시작 불가",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // 초기화를 수행하면 진행/일시정지 중이던 오토런도 함께 초기화(백그라운드 런·화면 상태 정리).
            _mainVM.ResetAutoRunForInitialize();

            // 시퀀스가 참조하는 활성 스냅샷을 DB 최신값으로 갱신 — 티칭 저장 후 READY 좌표가
            // 반영되도록(스냅샷 미갱신으로 READY 미이동하던 문제 방지). ActiveRecipeName 기준.
            _mainVM.RecipeVM.RefreshActivePointsSnapshot();

            BuildSteps();
            ExecutionLogs.Clear();
            IsError = false;
            IsCompleted = false;
            IsRunning = true;
            Progress = 0;
            CurrentStepName = "STARTING";
            _machine.SetSystemStatus(MachineState.Running);
            AddExecLog($"[INITIALIZE] 시작 ({Steps.Count} 단계)");
            _mainVM.AddLog("[SEQ] Initialize — 시작", LogLevel.Info);

            _cts = new CancellationTokenSource();
            int total = Steps.Count;

            try
            {
                for (int i = 0; i < Steps.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var step = Steps[i];
                    step.Status = StepStatus.Running;
                    step.Elapsed = "-";
                    CurrentStepName = $"[{step.Number}/{total}] {step.Name}";
                    Progress = (double)i / total * 100;
                    AddExecLog($"Step {step.Number}  {step.Name}");

                    var sw = Stopwatch.StartNew();
                    await step.Action(_cts.Token);
                    sw.Stop();
                    step.Elapsed = $"{sw.Elapsed.TotalSeconds:F1}s";
                    step.Status = StepStatus.Done;
                    Progress = (double)(i + 1) / total * 100;
                    AddExecLog($"  → 완료 ({step.Elapsed})");
                }

                Progress = 100;
                CurrentStepName = "COMPLETED";
                IsCompleted = true;
                _machine.SetSystemStatus(MachineState.Standby);
                AddExecLog("✅ 초기화 완료");
                _mainVM.AddLog("[SEQ] Initialize — 완료", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                MarkRunningStepAs(StepStatus.Aborted);
                CurrentStepName = "ABORTED";
                _machine.SetSystemStatus(MachineState.Standby);
                AddExecLog("⏹ 중단됨");
                _mainVM.AddLog("[SEQ] Initialize — 중단", LogLevel.Warning);
            }
            catch (TimeoutException ex)
            {
                MarkRunningStepAs(StepStatus.Failed);
                IsError = true;
                CurrentStepName = "TIMEOUT";
                _machine.SetSystemStatus(MachineState.Alarm);
                AddExecLog($"⏱ 타임아웃: {ex.Message}");
                _mainVM.AddLog($"[SEQ] Initialize — 타임아웃: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("SEQ-INIT-TIMEOUT");
            }
            catch (Exception ex)
            {
                MarkRunningStepAs(StepStatus.Failed);
                IsError = true;
                CurrentStepName = "ERROR";
                _machine.SetSystemStatus(MachineState.Alarm);
                AddExecLog($"❌ 실패: {ex.Message}");
                _mainVM.AddLog($"[SEQ] Initialize — 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("SEQ-INIT-FAIL");
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void MarkRunningStepAs(StepStatus status)
        {
            var running = Steps.FirstOrDefault(s => s.Status == StepStatus.Running);
            if (running != null) running.Status = status;
        }

        /// <summary>
        /// 진행 중인 초기화 시퀀스를 중단한다. 초기화 화면을 벗어나거나 알람이 발생하면 호출.
        /// 미호출 시 시퀀스가 백그라운드에서 대기하다, 사용자가 Motor Control 등에서 축을 수동
        /// 정상화하면 대기 조건이 충족돼 남은 단계(READY 이동 등)가 자동 실행되는 위험이 있다.
        /// </summary>
        public void Abort()
        {
            if (IsRunning) _cts?.Cancel();
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
    }
}
