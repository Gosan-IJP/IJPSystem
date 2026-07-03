using IJPSystem.Platform.Domain.Common;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>상태 표시등 1개(VV_IND).</summary>
    public sealed class IndicatorViewModel : ViewModelBase
    {
        public VvIndicator Id { get; }
        public string Label { get; }
        public IndicatorViewModel(VvIndicator id) { Id = id; Label = VvSignalMap.Label(id); }

        private bool _isOn;
        public bool IsOn { get => _isOn; set => SetProperty(ref _isOn, value); }

        /// <summary>알람(MPC AL)이면 켜질 때 빨강, 일반은 초록.</summary>
        public bool IsAlarm => Id == VvIndicator.MpcAlarm;
    }
}
