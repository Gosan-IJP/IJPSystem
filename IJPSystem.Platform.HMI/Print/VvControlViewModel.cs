using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Interfaces;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 메니스커스 화면 VV Control 패널 로직 (MVVM).
    /// - 제어 버튼(Final VV / Switching Pressure / Pump Control) → DOUT 토글
    /// - 상태 LED(Meni/Purge/Final VV/Over Flow/Purge/Meniscus/Pump/MPC AL) → DIN 폴링
    /// IO 는 프로젝트 공용 IIODriver(문자열 인덱스)를 쓰며, 미연결/미매핑이면 안전하게 무시된다.
    /// 출력 버튼 상태는 기존 Valve 토글과 동일하게 로컬로 래치한다(리드백이 아닌 명령 기준).
    /// </summary>
    public sealed class VvControlViewModel : ViewModelBase, IDisposable
    {
        private readonly Func<IIODriver?> _io;
        private readonly Action<string>? _log;
        private readonly DispatcherTimer _poll;

        public VvControlViewModel(Func<IIODriver?> ioProvider, Action<string>? log = null)
        {
            _io = ioProvider ?? throw new ArgumentNullException(nameof(ioProvider));
            _log = log;

            Indicators = new ObservableCollection<IndicatorViewModel>();
            foreach (VvIndicator ind in Enum.GetValues(typeof(VvIndicator)))
                Indicators.Add(new IndicatorViewModel(ind));

            FinalVvCommand           = new RelayCommand(_ => Toggle(VvControlOutput.FinalVv));
            SwitchingPressureCommand = new RelayCommand(_ => Toggle(VvControlOutput.SwitchingPressure));
            PumpControlCommand       = new RelayCommand(_ => Toggle(VvControlOutput.PumpControl), _ => PumpControlEnabled);

            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _poll.Tick += (s, e) => Refresh();
            _poll.Start();
        }

        // ---- 버튼 상태(로컬 래치) ----
        private bool _finalVvOn;
        public bool FinalVvOn { get => _finalVvOn; private set => SetProperty(ref _finalVvOn, value); }
        private bool _switchingOn;
        public bool SwitchingPressureOn { get => _switchingOn; private set => SetProperty(ref _switchingOn, value); }
        private bool _pumpOn;
        public bool PumpControlOn { get => _pumpOn; private set => SetProperty(ref _pumpOn, value); }

        /// <summary>Pump Control 활성 조건(이미지에서 비활성 표시). 실제 규칙으로 교체.</summary>
        private bool _pumpEnabled;
        public bool PumpControlEnabled { get => _pumpEnabled; set => SetProperty(ref _pumpEnabled, value); }

        public ObservableCollection<IndicatorViewModel> Indicators { get; }

        // ---- 커맨드 ----
        public ICommand FinalVvCommand { get; }
        public ICommand SwitchingPressureCommand { get; }
        public ICommand PumpControlCommand { get; }

        private void Toggle(VvControlOutput o)
        {
            bool next = o switch
            {
                VvControlOutput.FinalVv => !FinalVvOn,
                VvControlOutput.SwitchingPressure => !SwitchingPressureOn,
                _ => !PumpControlOn
            };

            string index = VvSignalMap.OutputIndex[o];
            try { _io()?.SetOutput(index, next); } catch { /* IO 미연결/미매핑 무시 */ }

            switch (o)
            {
                case VvControlOutput.FinalVv: FinalVvOn = next; break;
                case VvControlOutput.SwitchingPressure: SwitchingPressureOn = next; break;
                default: PumpControlOn = next; break;
            }

            _log?.Invoke($"[VV] {OutputLabel(o)} ({index}) {(next ? "ON" : "OFF")}");
        }

        private static string OutputLabel(VvControlOutput o) => o switch
        {
            VvControlOutput.FinalVv => "Final VV",
            VvControlOutput.SwitchingPressure => "Switching Pressure",
            VvControlOutput.PumpControl => "Pump Control",
            _ => o.ToString()
        };

        /// <summary>주기 갱신: 상태 LED(DIN) 읽기.</summary>
        private void Refresh()
        {
            var io = _io();
            if (io == null) return;
            foreach (var ind in Indicators)
            {
                try { ind.IsOn = io.GetInput(VvSignalMap.IndicatorIndex[ind.Id]); }
                catch { /* 미매핑/미연결 무시 */ }
            }
        }

        public void Dispose() => _poll?.Stop();
    }
}
