using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Infrastructure.Config;
using IJPSystem.Platform.Infrastructure.Print;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// 노즐 선택 화면 로직. (LabVIEW "20_Screen_Nozzle Select.vi" 대응)
    ///
    /// <para>
    /// 선택 상태는 <b>여기 있는 집합 하나</b>가 기준이고, 막대 드래그·빠른 버튼·명령어 입력
    /// 셋 다 같은 집합을 고친다. 무엇으로 바꿨든 결과가 즉시 전역(Using Nozzle)에 반영된다 —
    /// "적용을 눌렀는지" 를 기억해야 하는 화면은 실수를 부른다.
    /// </para>
    /// <para>
    /// 명령어 입력만 [적용]이 필요하다. 타이핑 도중의 미완성 문자열이 그대로 적용되면 안 되기 때문.
    /// 대신 치는 동안 막대에 결과를 미리 칠해 준다.
    /// </para>
    /// </summary>
    public sealed class NozzleSelectViewModel : INotifyPropertyChanged
    {
        // 헤드별 노즐 범위 — HeadSpec 하나만 본다. 화면마다 다른 숫자를 들고 있으면
        // 여기서 "적용 완료" 라고 표시된 뒤 토출 단계에서 조용히 빠진다.
        // const 가 아닌 이유: 헤드가 바뀌면 장비 설정에서 값이 바뀐다.
        public static int MinNozzle => HeadSpec.FirstNozzle;
        public static int MaxNozzle => HeadSpec.LastNozzle;

        private readonly SortedSet<int> _selected = new();

        public NozzleSelectViewModel()
        {
            ApplyCommand   = new RelayCommand(_ => ApplyInput());
            SelectAllCommand = new RelayCommand(_ => Replace(AllNozzles(), "전체"));
            SelectOddCommand  = new RelayCommand(_ => Replace(AllNozzles().Where(n => n % 2 == 1), "홀수"));
            SelectEvenCommand = new RelayCommand(_ => Replace(AllNozzles().Where(n => n % 2 == 0), "짝수"));
            InvertCommand  = new RelayCommand(_ => Replace(AllNozzles().Where(n => !_selected.Contains(n)), "반전"));
            ClearCommand   = new RelayCommand(_ => Replace(Array.Empty<int>(), "해제"));

            // 전역에 범위 밖 번호가 들어 있을 수 있다(헤드 사양이 바뀌었거나 옛 레시피).
            // 그대로 받으면 "사용 152 / 128" 같은 표시가 나오고, 토출 단계에서 조용히 빠진다.
            var stored = NozzleControlGlobal.Instance.UsingNozzle.UsingNozzles;
            int dropped = 0;
            foreach (int n in stored)
            {
                if (n >= MinNozzle && n <= MaxNozzle) _selected.Add(n);
                else dropped++;
            }

            RefreshSelection(
                dropped > 0 ? $"저장된 선택 중 범위({MinNozzle}~{MaxNozzle}) 밖 {dropped}개를 뺐습니다 — 사용 {_selected.Count}개"
                : _selected.Count > 0 ? $"현재 사용 노즐 {_selected.Count}개"
                : null);
        }

        public int FirstNozzle  => MinNozzle;
        public int TotalNozzles => MaxNozzle - MinNozzle + 1;

        /// <summary>막대를 몇 줄로 나눌지 — 헤드 열 수와 맞춘다(S800 = 2열 × 400).</summary>
        public int Rows => HeadSpec.Rows;

        // ── 선택 상태 ─────────────────────────────────────────────────────
        private IReadOnlyCollection<int> _selectedView = Array.Empty<int>();
        /// <summary>막대가 그릴 현재 선택.</summary>
        public IReadOnlyCollection<int> Selected
        {
            get => _selectedView;
            private set { _selectedView = value; OnPropertyChanged(); }
        }

        private IReadOnlyCollection<int>? _preview;
        /// <summary>입력 중인 명령의 결과 미리보기. null 이면 미리보기 없음.</summary>
        public IReadOnlyCollection<int>? PreviewSelection
        {
            get => _preview;
            private set { _preview = value; OnPropertyChanged(); }
        }

        /// <summary>선택을 구간으로 접은 표기 — <c>1~100, 150, 200~250</c>.</summary>
        public string UsingNozzleText => _selected.Count == 0
            ? "(선택 없음)"
            : NozzleRangeText.Summarize(_selected);

        public string CountText => $"사용 {_selected.Count} / {TotalNozzles}";

        // ── 입력 ──────────────────────────────────────────────────────────
        private string _inputText = "";
        /// <summary>노즐 지정 입력창 (예: "ADD(1~100); DEL(40~45)").</summary>
        public string InputText
        {
            get => _inputText;
            set
            {
                if (_inputText == value) return;
                _inputText = value;
                OnPropertyChanged();
                UpdatePreview();
            }
        }

        private string _statusText = "막대를 드래그하거나 명령을 입력하세요.";
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        private string _hoverText = "";
        /// <summary>마우스가 가리키는 노즐 — 800개 막대에서 몇 번인지 알려면 필요하다.</summary>
        public string HoverText
        {
            get => _hoverText;
            private set { _hoverText = value; OnPropertyChanged(); }
        }

        public ICommand ApplyCommand      { get; }
        public ICommand SelectAllCommand  { get; }
        public ICommand SelectOddCommand  { get; }
        public ICommand SelectEvenCommand { get; }
        public ICommand InvertCommand     { get; }
        public ICommand ClearCommand      { get; }

        /// <summary>막대 드래그 결과 반영.</summary>
        public void ToggleRange(int from, int to, bool add)
        {
            from = Math.Max(MinNozzle, from);
            to   = Math.Min(MaxNozzle, to);
            if (to < from) return;

            for (int n = from; n <= to; n++)
                if (add) _selected.Add(n); else _selected.Remove(n);

            string what = from == to ? $"{from}번" : $"{from}~{to}";
            RefreshSelection($"{what} {(add ? "선택" : "해제")} — 사용 {_selected.Count}개");
        }

        public void SetHover(int? nozzle) => HoverText = nozzle == null ? "" : $"노즐 {nozzle}";

        private IEnumerable<int> AllNozzles() => Enumerable.Range(MinNozzle, TotalNozzles);

        private void Replace(IEnumerable<int> nozzles, string what)
        {
            var next = nozzles.ToList();       // _selected 를 읽는 중에 지우면 안 된다(반전)
            _selected.Clear();
            foreach (int n in next) _selected.Add(n);
            RefreshSelection($"{what} — 사용 {_selected.Count}개");
        }

        /// <summary>입력창 명령을 적용. 파싱 결과가 곧 새 선택이다(누적 아님).</summary>
        private void ApplyInput()
        {
            if (string.IsNullOrWhiteSpace(InputText))
            {
                StatusText = "입력이 비어 있습니다.";
                return;
            }

            var nozzles = NozzleParser.Parse(InputText, MinNozzle, MaxNozzle, out var invalid);
            _selected.Clear();
            foreach (int n in nozzles) _selected.Add(n);

            PreviewSelection = null;
            RefreshSelection(invalid.Count == 0
                ? $"적용 완료 — 사용 {_selected.Count}개 (범위 {MinNozzle}~{MaxNozzle})"
                : $"적용({_selected.Count}개). 무시된 토큰: {string.Join(", ", invalid)}");
        }

        /// <summary>타이핑 중 결과를 막대에 미리 칠한다 — [적용] 전에 틀린 걸 알 수 있게.</summary>
        private void UpdatePreview()
        {
            if (string.IsNullOrWhiteSpace(InputText)) { PreviewSelection = null; return; }
            try
            {
                var parsed = NozzleParser.Parse(InputText, MinNozzle, MaxNozzle, out _);
                PreviewSelection = parsed.Count > 0 ? parsed : null;
            }
            catch
            {
                // 치는 도중에는 문법이 깨져 있는 것이 정상이다 — 미리보기만 끄고 조용히 넘어간다.
                PreviewSelection = null;
            }
        }

        /// <summary>
        /// 선택이 바뀌었을 때 화면을 맞춘다.
        ///
        /// <para>
        /// <b>전역은 건드리지 않는다.</b> 예전에는 여기서 바로 전역에 썼는데, 그러면 창을 닫는
        /// 순간이 아니라 막대를 한 번 끄는 순간 이미 반영돼 되돌릴 방법이 없었다.
        /// 전역에 옮기는 것은 [확인]을 눌렀을 때뿐이다(<see cref="Commit"/>).
        /// </para>
        /// </summary>
        private void RefreshSelection(string? status)
        {
            Selected = _selected.ToList();

            OnPropertyChanged(nameof(UsingNozzleText));
            OnPropertyChanged(nameof(CountText));
            if (status != null) StatusText = status;
        }

        /// <summary>지금 선택을 전역에 반영한다 — [확인] 전용.</summary>
        public void Commit()
            => NozzleControlGlobal.Instance.UsingNozzle = new SelectedNozzleInfo(_selected, InputText);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
