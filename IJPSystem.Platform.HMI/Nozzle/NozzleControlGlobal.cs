using System;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// LabVIEW "control global.vi"(Rasterizer 전역)의 "Using Nozzle" 저장소 대응.
    /// "Set Use Nozzle" 버튼이 여기에 결과를 기록하고, Rasterizer/RIP 가 이 값을 읽는다.
    /// 실장비 코드에서는 보통 싱글톤(또는 DI 등록)으로 공유한다.
    /// </summary>
    public sealed class NozzleControlGlobal
    {
        private static readonly Lazy<NozzleControlGlobal> _instance =
            new Lazy<NozzleControlGlobal>(() => new NozzleControlGlobal());

        public static NozzleControlGlobal Instance => _instance.Value;

        private NozzleControlGlobal() { }

        private SelectedNozzleInfo _usingNozzle = new SelectedNozzleInfo(Array.Empty<int>());

        /// <summary>현재 활성 "Using Nozzle". 값이 갱신되면 UsingNozzleChanged 발생.</summary>
        public SelectedNozzleInfo UsingNozzle
        {
            get => _usingNozzle;
            set
            {
                _usingNozzle = value ?? new SelectedNozzleInfo(Array.Empty<int>());
                UsingNozzleChanged?.Invoke(this, _usingNozzle);
            }
        }

        /// <summary>Using Nozzle 변경 알림(Rasterizer 등 구독).</summary>
        public event EventHandler<SelectedNozzleInfo>? UsingNozzleChanged;
    }
}
