using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Models.IO;
using IJPSystem.Platform.HMI.Common;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Data;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class IOMonitorViewModel : ViewModelBase, IDisposable
    {
        private readonly MainViewModel _mainVM;

        public ObservableCollection<IOViewModel> InputList => _mainVM.dgInputList;
        public ObservableCollection<IOViewModel> OutputList => _mainVM.dgOutputList;
        public ObservableCollection<IOViewModel> AnalogInputList => _mainVM.agInputList;
        public ObservableCollection<IOViewModel> AnalogOutputList => _mainVM.agOutputList;

        // 아날로그 채널이 없는 장비(Pulse 등)에서는 AI/AO 탭 자체를 숨긴다 —
        // 빈 표를 눌러보게 만들면 "설정이 안 된 건지 장비에 없는 건지" 구분이 안 된다.
        // IO.json 로드가 VM 생성 이후일 수 있어 컬렉션 변경도 구독한다.
        public bool HasAnalogInput  => AnalogInputList.Count  > 0;
        public bool HasAnalogOutput => AnalogOutputList.Count > 0;

        private string _filterText = "";
        public string FilterText
        {
            get => _filterText;
            set
            {
                SetProperty(ref _filterText, value);
                ApplyFilter();
            }
        }

        public IOMonitorViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM;

            // 디지털 출력 이벤트 연결
            foreach (var vm in OutputList)
                vm.RequestControl += OnDigitalControlRequested;

            // 아날로그 출력 이벤트 연결
            foreach (var vm in AnalogOutputList)
                vm.RequestAnalogControl += OnAnalogControlRequested;

            AnalogInputList.CollectionChanged  += OnAnalogInputChanged;
            AnalogOutputList.CollectionChanged += OnAnalogOutputChanged;
        }

        private void OnAnalogInputChanged(object? s, NotifyCollectionChangedEventArgs e)
            => OnPropertyChanged(nameof(HasAnalogInput));

        private void OnAnalogOutputChanged(object? s, NotifyCollectionChangedEventArgs e)
            => OnPropertyChanged(nameof(HasAnalogOutput));

        // 디지털 출력 제어
        private void OnDigitalControlRequested(object? sender, bool isOn)
        {
            if (sender is not IOViewModel ioItem) return;

            var ioDriver = _mainVM.GetController()?.GetMachine()?.IO;
            if (ioDriver != null && !string.IsNullOrEmpty(ioItem.Index))
            {
                ioDriver.SetOutput(ioItem.Index, isOn);
                _mainVM.AddLog($"[IO] DO {ioItem.Index} → {(isOn ? "ON" : "OFF")}",
                               LogLevel.Warning);
            }
        }

        // 아날로그 출력 제어
        private void OnAnalogControlRequested(object? sender, double value)
        {
            if (sender is not IOViewModel ioItem) return;

            var ioDriver = _mainVM.GetController()?.GetMachine()?.IO;
            if (ioDriver != null && !string.IsNullOrEmpty(ioItem.Index))
            {
                ioDriver.SetAnalogOutput(ioItem.Index, value);
                _mainVM.AddLog($"[IO] AO {ioItem.Index} → {value:F2}",
                               LogLevel.Warning);
            }
        }

        private void ApplyFilter()
        {
            foreach (var list in new[]
            {
                InputList,
                OutputList,
                AnalogInputList,
                AnalogOutputList
            })
            {
                var view = CollectionViewSource.GetDefaultView(list);
                view.Filter = obj => obj is IOViewModel vm && MatchesFilter(vm);
            }
        }

        private bool MatchesFilter(IOViewModel vm)
        {
            if (string.IsNullOrWhiteSpace(_filterText)) return true;
            string f = _filterText.ToLower();
            return (vm.Address?.ToLower().Contains(f) ?? false) ||
                   (vm.Description?.ToLower().Contains(f) ?? false) ||
                   (vm.Index?.ToLower().Contains(f) ?? false);
        }

        public void Dispose()
        {
            foreach (var vm in OutputList)
                vm.RequestControl -= OnDigitalControlRequested;

            foreach (var vm in AnalogOutputList)
                vm.RequestAnalogControl -= OnAnalogControlRequested;

            AnalogInputList.CollectionChanged  -= OnAnalogInputChanged;
            AnalogOutputList.CollectionChanged -= OnAnalogOutputChanged;
        }
    }
}