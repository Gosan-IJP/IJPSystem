using IJPSystem.Platform.Domain.Common;

namespace IJPSystem.Platform.HMI.Common.Models
{
    public enum InitStepStatus
    {
        Pending,
        Running,
        Done,
        Failed,
        /// <summary>동작은 계속하지만 정상 상태는 아님(예: Meteor 헤드 미부착). 기동을 막지 않는다.</summary>
        Warning,
    }

    // 초기 로딩 화면의 한 단계 — 이름/설명/상태/에러
    public class InitStep : ViewModelBase
    {
        public string Name { get; set; } = "";

        // 조회 결과를 단계 완료 후 설명에 반영하는 경우가 있어(헤드 PCC 상태 등) 알림형 속성이어야 한다.
        private string _description = "";
        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        private InitStepStatus _status = InitStepStatus.Pending;
        public InitStepStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(IconColor));
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError    => !string.IsNullOrEmpty(_errorMessage);
        public bool IsRunning   => _status == InitStepStatus.Running;

        public string Icon => _status switch
        {
            InitStepStatus.Pending => "○",
            InitStepStatus.Running => "●",
            InitStepStatus.Done    => "✓",
            InitStepStatus.Failed  => "✗",
            InitStepStatus.Warning => "!",
            _ => "?",
        };

        public string IconColor => _status switch
        {
            InitStepStatus.Pending => "#475569",
            InitStepStatus.Running => "#3B82F6",
            InitStepStatus.Done    => "#22C55E",
            InitStepStatus.Failed  => "#EF4444",
            InitStepStatus.Warning => "#F59E0B",
            _ => "#94A3B8",
        };
    }
}
