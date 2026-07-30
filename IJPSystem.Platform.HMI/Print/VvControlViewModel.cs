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
    /// - 제어 버튼(Final VV / Switching Pressure / Pump Control) → EtherCAT DO 토글(ecdoPutOne).
    /// - 상태 LED(Meni/Purge/Final VV/Over Flow/Purge/Meniscus/Pump/MPC AL) → 출처별 폴링(200ms).
    ///   · 대부분 LED = DO 코일 되읽기(GetOutput, ecdoGetOne) = LabVIEW "Get DOUT State".
    ///   · Over Flow = 실제 DI 센서(IO.json DI_OVER_FLOW_SND).
    ///   · MPC AL   = DMD 메니스커스 컨트롤러(Modbus) 알람 — mpcAlarmProvider 로 주입.
    /// IO 는 프로젝트 공용 IIODriver(정수 채널)를 쓰며, 미연결/미매핑이면 안전하게 무시된다.
    /// </summary>
    public sealed class VvControlViewModel : ViewModelBase, IDisposable
    {
        private readonly Func<IIODriver?> _io;
        private readonly Action<string>? _log;
        private readonly Func<bool>? _mpcAlarm;    // DMD MPC AL 알람 (null=미연동 → 항상 off)
        private readonly Func<double>? _pressure;  // 현재 메니스커스 압력 (표시용, 선택)
        private readonly DispatcherTimer _poll;

        public VvControlViewModel(
            Func<IIODriver?> ioProvider,
            Action<string>? log = null,
            Func<bool>? mpcAlarmProvider = null,
            Func<double>? pressureProvider = null)
        {
            _io = ioProvider ?? throw new ArgumentNullException(nameof(ioProvider));
            _log = log;
            _mpcAlarm = mpcAlarmProvider;
            _pressure = pressureProvider;

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

        // ---- 버튼 상태(로컬 래치 — 실장 연결 시 코일 리드백으로 보정) ----
        private bool _finalVvOn;
        public bool FinalVvOn { get => _finalVvOn; private set => SetProperty(ref _finalVvOn, value); }
        private bool _switchingOn;
        public bool SwitchingPressureOn { get => _switchingOn; private set => SetProperty(ref _switchingOn, value); }
        private bool _pumpOn;
        public bool PumpControlOn { get => _pumpOn; private set => SetProperty(ref _pumpOn, value); }

        /// <summary>Pump Control 활성 조건(이미지에서 비활성 표시). 실제 규칙으로 교체.</summary>
        private bool _pumpEnabled;
        public bool PumpControlEnabled { get => _pumpEnabled; set => SetProperty(ref _pumpEnabled, value); }

        /// <summary>현재 메니스커스 압력(표시용). pressureProvider 미주입 시 갱신 안 함.</summary>
        private double _meniscusPressure;
        public double MeniscusPressure { get => _meniscusPressure; private set => SetProperty(ref _meniscusPressure, value); }

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

            int ch = VvSignalMap.OutputChannel(o);
            try { if (ch >= 0) _io()?.SetOutput(ch, next); } catch { /* IO 미연결 무시 */ }

            switch (o)
            {
                case VvControlOutput.FinalVv: FinalVvOn = next; break;
                case VvControlOutput.SwitchingPressure: SwitchingPressureOn = next; break;
                default: PumpControlOn = next; break;
            }

            _log?.Invoke($"[VV] {OutputLabel(o)} (DO ch{ch}) {(next ? "ON" : "OFF")}");
        }

        private static string OutputLabel(VvControlOutput o) => o switch
        {
            VvControlOutput.FinalVv => "Final VV",
            VvControlOutput.SwitchingPressure => "Switching Pressure",
            VvControlOutput.PumpControl => "Pump Control",
            _ => o.ToString()
        };

        /// <summary>주기 갱신: LED 출처별 읽기 + (실장 시) 버튼 코일 리드백.</summary>
        private void Refresh()
        {
            var io = _io();
            if (io == null) return;

            // 상태 LED
            foreach (var ind in Indicators)
            {
                try
                {
                    var src = VvSignalMap.IndicatorSource(ind.Id);
                    ind.IsOn = src.Source switch
                    {
                        VvSource.DoCoil   => src.DoChannel >= 0 && io.GetOutput(src.DoChannel),
                        VvSource.DiSensor => io.GetInput(src.DiIndex),
                        VvSource.Dmd      => _mpcAlarm?.Invoke() ?? false,
                        _ => false
                    };
                }
                catch { /* 미매핑/미연결 무시 */ }
            }

            // 버튼 상태: 실장(IsConnected)일 때만 코일 리드백으로 보정.
            // echo(개발/미연결) 모드에선 GetOutput 이 항상 false 라 로컬 래치를 유지한다.
            if (io.IsConnected)
            {
                try
                {
                    FinalVvOn           = io.GetOutput(VvSignalMap.OutputChannel(VvControlOutput.FinalVv));
                    SwitchingPressureOn = io.GetOutput(VvSignalMap.OutputChannel(VvControlOutput.SwitchingPressure));
                    PumpControlOn       = io.GetOutput(VvSignalMap.OutputChannel(VvControlOutput.PumpControl));
                }
                catch { /* 무시 */ }
            }

            if (_pressure != null)
            {
                try { MeniscusPressure = _pressure(); } catch { /* 무시 */ }
            }
        }

        public void Dispose() => _poll?.Stop();
    }
}
