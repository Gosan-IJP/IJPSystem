using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using IJPSystem.Platform.Domain.Common;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// LabVIEW "20_Screen_Nozzle Select.vi" 화면 로직 변환 (MVVM ViewModel).
    /// "Set Use Nozzle" 버튼 → 입력 문자열 파싱(ADD/DEL DSL) → control global 의 Using Nozzle 갱신.
    /// </summary>
    public sealed class NozzleSelectViewModel : INotifyPropertyChanged
    {
        // 헤드별 노즐 범위. 실제 헤드 사양 확정 시 교체(이미지 기준 1~999).
        private const int MinNozzle = 1;
        private const int MaxNozzle = 999;

        public NozzleSelectViewModel()
        {
            SetUseNozzleCommand = new RelayCommand(_ => SetUseNozzle());

            // 현재 전역 Using Nozzle 로 초기 표시
            var cur = NozzleControlGlobal.Instance.UsingNozzle;
            _usingNozzleText = string.Join(",", cur.UsingNozzles);
            if (cur.Count > 0) _statusText = $"현재 사용 노즐 {cur.Count}개";
        }

        private string _inputText = "";
        /// <summary>노즐 지정 입력창 (예: "ADD(1~100); DEL(40~45)").</summary>
        public string InputText
        {
            get => _inputText;
            set { _inputText = value; OnPropertyChanged(); }
        }

        private string _statusText = "노즐을 입력하고 [Set Use Nozzle]을 누르세요.";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private string _usingNozzleText = "";
        /// <summary>현재 사용 노즐 표시(상단 박스, 콤마 구분 목록).</summary>
        public string UsingNozzleText
        {
            get => _usingNozzleText;
            set { _usingNozzleText = value; OnPropertyChanged(); }
        }

        /// <summary>"Set Use Nozzle" 버튼 커맨드.</summary>
        public ICommand SetUseNozzleCommand { get; }

        /// <summary>버튼 클릭 시 실제 동작 (원본 이벤트 케이스에 대응).</summary>
        private void SetUseNozzle()
        {
            // 1) 입력 문자열 파싱 (ADD/DEL DSL)
            List<int> nozzles = NozzleParser.Parse(InputText, MinNozzle, MaxNozzle, out var invalid);

            // 2) Selected Nozzle info 생성
            var info = new SelectedNozzleInfo(nozzles, InputText);

            // 3) control global 의 Using Nozzle 갱신 → (Rasterizer/RIP 가 이 값을 사용)
            NozzleControlGlobal.Instance.UsingNozzle = info;

            // 4) UI 피드백
            UsingNozzleText = string.Join(",", nozzles);
            StatusText = invalid.Count == 0
                ? $"적용 완료: {info.Count}개 노즐 (범위 {MinNozzle}~{MaxNozzle})"
                : $"적용({info.Count}개). 무시된 토큰: {string.Join(", ", invalid)}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
