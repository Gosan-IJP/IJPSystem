using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.HMI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class MotorControlViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;

        public ObservableCollection<AxisViewModel> AxisList => _mainVM.SharedAxisList;
        private AxisViewModel? _selectedAxis;
        public AxisViewModel? SelectedAxis
        {
            get => _selectedAxis;
            set => SetProperty(ref _selectedAxis, value);
        }

        // D-패드 조그 버튼(X/Y/Z/T)의 IsEnabled 바인딩용 — 코드비하인드 ResolveAxis와 동일하게
        // 이름에 해당 축 문자가 포함된 축을 찾는다. (각 축의 CanMove로 활성화 제어)
        public AxisViewModel? AxisX => ResolveByTag("X");
        public AxisViewModel? AxisY => ResolveByTag("Y");
        public AxisViewModel? AxisZ => ResolveByTag("Z");
        public AxisViewModel? AxisT => ResolveByTag("T");
        private AxisViewModel? ResolveByTag(string tag) =>
            AxisList.FirstOrDefault(a => a.Info?.Name != null &&
                a.Info.Name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);
       
        public ICommand AllServoOnCommand  { get; }
        public ICommand AllServoOffCommand { get; }
        public ICommand AllStopCommand     { get; }

        private double _jogSpeedScale = 1.0;
        public double JogSpeedScale
        {
            get => _jogSpeedScale;
            set
            {
                if (SetProperty(ref _jogSpeedScale, value))
                {
                    OnPropertyChanged(nameof(IsJogSpeedSlow));
                    OnPropertyChanged(nameof(IsJogSpeedNormal));
                    OnPropertyChanged(nameof(IsJogSpeedFast));
                }
            }
        }
        public bool IsJogSpeedSlow   { get => JogSpeedScale == 0.25; set { if (value) JogSpeedScale = 0.25; } }
        public bool IsJogSpeedNormal { get => JogSpeedScale == 1.0;  set { if (value) JogSpeedScale = 1.0; } }
        public bool IsJogSpeedFast   { get => JogSpeedScale == 2.0;  set { if (value) JogSpeedScale = 2.0; } }

        public MotorControlViewModel(MainViewModel mainViewModel)
        {
            _mainVM = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

            SelectedAxis = AxisList.FirstOrDefault();

            AllServoOnCommand  = new RelayCommand(async _ => await ExecuteAllServoOn());
            AllServoOffCommand = new RelayCommand(async _ => await ExecuteAllServoOff());
            AllStopCommand     = new RelayCommand(async _ => await ExecuteAllStop());
        }

        private async Task ExecuteAllServoOn()
        {
            _mainVM.AddLog("[MOTION] All Axes Servo ON Command.");
            try
            {
                await Task.WhenAll(AxisList.Select(a => a.ForceServoOnAsync()));
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] All Servo ON failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task ExecuteAllServoOff()
        {
            _mainVM.AddLog("[MOTION] All Axes Servo OFF Command.");
            try
            {
                await Task.WhenAll(AxisList.Select(a => a.ForceServoOffAsync()));
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] All Servo OFF failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task ExecuteAllStop()
        {
            _mainVM.AddLog("[MOTION] Stop all axes!");
            try
            {
                await Task.WhenAll(AxisList.Select(a => a.StopAsync()));
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] All Stop failed: {ex.Message}", LogLevel.Error);
            }
        }
        
    }
}