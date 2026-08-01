using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace IJPSystem.Platform.HMI.ViewModels
{
    // SplashWindow 의 DataContext — 초기화 단계 진행 표시
    public class SplashViewModel : ViewModelBase
    {
        public ObservableCollection<InitStep> InitSteps { get; } = new();

        private string _machineName = "INKJET HMI";
        public string MachineName
        {
            get => _machineName;
            set => SetProperty(ref _machineName, value);
        }

        // 단계 실행: 작업을 수행하면서 step 상태를 갱신. minMs 미만으로 끝나면 잠시 대기 (시각 피드백 보장)
        public Task<T> RunStepAsync<T>(
            string name,
            string description,
            Func<T> action,
            bool background = true,
            int minMs = 200) =>
            RunStepAsync(name, description, action, report: null, background, minMs);

        /// <summary>
        /// 결과를 화면에 되돌려 표시하는 단계 — 조회형 작업(예: Meteor 헤드 PCC 상태)에 사용.
        /// <paramref name="report"/> 가 완료 상태와 표시 문구를 결정하므로, "연결 안 됐지만 기동은 정상"
        /// 같은 경우를 실패(✗)가 아니라 경고(!)로 남길 수 있다.
        /// </summary>
        public async Task<T> RunStepAsync<T>(
            string name,
            string description,
            Func<T> action,
            Func<T, (InitStepStatus Status, string Description)>? report,
            bool background = true,
            int minMs = 200)
        {
            var step = new InitStep
            {
                Name        = name,
                Description = description,
                Status      = InitStepStatus.Running,
            };
            System.Windows.Application.Current.Dispatcher.Invoke(() => InitSteps.Add(step));

            var sw = Stopwatch.StartNew();
            try
            {
                T result;
                if (background)
                {
                    // 드라이버 등 IO 바운드 작업은 백그라운드 스레드에서 실행
                    result = await Task.Run(action).ConfigureAwait(true);
                }
                else
                {
                    // UI 스레드에 있어야 하는 작업 (DispatcherTimer 등)
                    await Task.Yield();
                    result = action();
                }

                int elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed < minMs) await Task.Delay(minMs - elapsed);

                if (report != null)
                {
                    var (status, desc) = report(result);
                    step.Description = desc;
                    step.Status      = status;
                }
                else
                {
                    step.Status = InitStepStatus.Done;
                }
                return result;
            }
            catch (Exception ex)
            {
                step.Status       = InitStepStatus.Failed;
                step.ErrorMessage = ex.Message;
                throw;
            }
        }

        public Task RunStepAsync(
            string name,
            string description,
            Action action,
            bool background = true,
            int minMs = 200) =>
            RunStepAsync<object?>(name, description, () => { action(); return null; }, background, minMs);
    }
}
