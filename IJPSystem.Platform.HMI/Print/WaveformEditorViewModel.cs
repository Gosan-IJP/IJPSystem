using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Infrastructure.Print.Waveform;

namespace IJPSystem.Platform.HMI.Print
{
    /// <summary>
    /// 구동 파형 편집 — MetWaveEpson 의 편집 표면을 화면에 옮긴 것.
    ///
    /// <para><b>왜 별도 객체인가</b>: 웨이브폼 화면은 원래 파일을 읽어 그림만 그리는 뷰어였다.
    /// 편집은 성격이 다른 일이라(값 → 재계산 → 그래프 → 검증) 화면 VM 에 섞으면 로드·스핏·히터
    /// 코드와 뒤엉킨다. 노즐 요약을 <c>NozzleSelectionWatcher</c> 로 뺀 것과 같은 이유다.</para>
    ///
    /// <para>모든 편집은 <see cref="EpsonWaveformCalculator.ResolveDocument"/> 를 거친다 —
    /// 화면 그래프와 헤드로 내려갈 값이 같은 계산에서 나와야 한다.</para>
    /// </summary>
    public sealed class WaveformEditorViewModel : ViewModelBase
    {
        /// <summary>편집값이 바뀌어 그래프를 다시 그려야 한다.</summary>
        public event Action? Changed;

        private EpsonWaveformDocument _doc = new();
        private bool _suspend;

        public WaveformEditorViewModel()
        {
            GreyLevelRows = Enumerable.Range(0, GreyLevelMatrix.Levels)
                                      .Select(g => new GreyLevelRowVm(this, g))
                                      .ToList();
            ToggleGreyLevelCommand = new RelayCommand(p => ToggleGreyLevel(p as GreyLevelCellVm));
            LoadEmpty();
        }

        public EpsonWaveformDocument Document => _doc;

        /// <summary>문서를 갈아 끼운다(파일 로드 등).</summary>
        public void Load(EpsonWaveformDocument doc)
        {
            _doc = doc ?? new EpsonWaveformDocument();
            EpsonWaveformCalculator.ResolveDocument(_doc);
            RefreshAll();
        }

        /// <summary>파일이 없을 때의 빈 상태 — 화면이 값 없이 열려도 형태는 유지한다.</summary>
        public void LoadEmpty() => Load(new EpsonWaveformDocument());

        // ── 전역 파라미터 ─────────────────────────────────────────────────

        /// <summary>대기 전압. 바꾸면 모든 펄스의 마지막 세그먼트가 따라 움직인다.</summary>
        public double Vst
        {
            get => _doc.Vst;
            set { if (Math.Abs(_doc.Vst - value) < 1e-9) return; _doc.Vst = value; Recalculate(); }
        }

        /// <summary>
        /// 기울기 고정(true) / 천이시간 고정(false).
        /// 어느 쪽이든 <b>한 칸은 계산값</b>이라 읽기 전용이 된다 — 둘 다 입력이면 ΔV 와 모순된다.
        /// </summary>
        public bool IsConstantSlew
        {
            get => _doc.VoltageAdjustMode == VoltageAdjustMode.ConstantSlew;
            set
            {
                var mode = value ? VoltageAdjustMode.ConstantSlew : VoltageAdjustMode.ConstantDuration;
                if (_doc.VoltageAdjustMode == mode) return;
                _doc.VoltageAdjustMode = mode;
                Recalculate();
                OnPropertyChanged(nameof(IsConstantDuration));
                OnPropertyChanged(nameof(IsSlewEditable));
                OnPropertyChanged(nameof(IsSlewTimeEditable));
            }
        }
        public bool IsConstantDuration { get => !IsConstantSlew; set => IsConstantSlew = !value; }

        /// <summary>Slew 칸을 입력받을 수 있는가(= 기울기 고정 모드).</summary>
        public bool IsSlewEditable     => IsConstantSlew;
        /// <summary>Slew Time 칸을 입력받을 수 있는가.</summary>
        public bool IsSlewTimeEditable => IsConstantDuration;

        /// <summary>ComB 를 ComA 복제로 둘지. 켜면 ComB 편집이 의미를 잃는다.</summary>
        public bool IsSynchronous
        {
            get => _doc.ComAbMode == ComAbMode.Synchronous;
            set
            {
                var m = value ? ComAbMode.Synchronous : ComAbMode.Independent;
                if (_doc.ComAbMode == m) return;
                _doc.ComAbMode = m;
                Recalculate();
                OnPropertyChanged(nameof(IsIndependent));
                OnPropertyChanged(nameof(IsComBEditable));
            }
        }
        public bool IsIndependent  { get => !IsSynchronous; set => IsSynchronous = !value; }
        public bool IsComBEditable => IsIndependent;

        /// <summary>노즐 행 A/B 를 다른 잉크로 볼지.</summary>
        public bool IsDualColour
        {
            get => _doc.NozzleRowMode == NozzleRowMode.DualColour;
            set
            {
                var m = value ? NozzleRowMode.DualColour : NozzleRowMode.SingleColour;
                if (_doc.NozzleRowMode == m) return;
                _doc.NozzleRowMode = m;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSingleColour));
            }
        }
        public bool IsSingleColour { get => !IsDualColour; set => IsDualColour = !value; }

        /// <summary>이 파형이 낼 수 있는 최대 반복 주파수. 읽기 전용 계산값.</summary>
        public string MaxFrequencyText
        {
            get
            {
                double k = EpsonWaveformCalculator.MaxFrequencyKHz(_doc);
                return k <= 0 ? "-" : $"{k:F2} kHz";
            }
        }

        /// <summary>ComA / ComB 한 주기 길이 — 어느 채널이 주기를 정하는지 보이게 한다.</summary>
        public string ChannelTimeText =>
            $"ComA {_doc.ComA.TotalTimeUs:F2} µs · ComB {_doc.ComB.TotalTimeUs:F2} µs";

        // ── GL × Pulse 배정표 ─────────────────────────────────────────────

        public IReadOnlyList<GreyLevelRowVm> GreyLevelRows { get; }
        public ICommand ToggleGreyLevelCommand { get; }

        private void ToggleGreyLevel(GreyLevelCellVm? cell)
        {
            if (cell == null) return;
            _doc.GreyLevels.Toggle(cell.GreyLevel, cell.PulseIndex, cell.Assign);
            foreach (var row in GreyLevelRows) row.Refresh();
            OnPropertyChanged(nameof(UnassignedGreyLevelsText));
            OnPropertyChanged(nameof(HasUnassignedGreyLevel));
        }

        /// <summary>
        /// 펄스가 하나도 배정되지 않은 그레이 레벨. 그 레벨은 <b>토출 자체가 일어나지 않는다</b> —
        /// 화면에는 아무 이상이 없어 보이므로 미리 알려야 한다.
        /// </summary>
        public string UnassignedGreyLevelsText
        {
            get
            {
                var none = Enumerable.Range(0, GreyLevelMatrix.Levels)
                                     .Where(g => !_doc.GreyLevels.HasAnyPulse(g))
                                     .Select(g => $"GL{g}")
                                     .ToList();
                return none.Count == 0 ? "" : $"{string.Join(", ", none)} 에 배정된 펄스가 없어 토출되지 않습니다.";
            }
        }
        public bool HasUnassignedGreyLevel => UnassignedGreyLevelsText.Length > 0;

        // ── 세그먼트 그리드 ───────────────────────────────────────────────

        public ObservableCollection<PulseTabVm> ComAPulses { get; } = new();
        public ObservableCollection<PulseTabVm> ComBPulses { get; } = new();

        private PulseTabVm? _selectedComA, _selectedComB;
        public PulseTabVm? SelectedComAPulse
        {
            get => _selectedComA;
            set { _selectedComA = value; OnPropertyChanged(); }
        }
        public PulseTabVm? SelectedComBPulse
        {
            get => _selectedComB;
            set { _selectedComB = value; OnPropertyChanged(); }
        }

        // ── 재계산 ────────────────────────────────────────────────────────

        /// <summary>편집 후 확정 계산 + 화면 갱신. 세그먼트 행이 값을 바꾸면 이걸 부른다.</summary>
        public void Recalculate()
        {
            if (_suspend) return;
            EpsonWaveformCalculator.ResolveDocument(_doc);
            RefreshAll();
        }

        private void RefreshAll()
        {
            _suspend = true;
            try
            {
                RebuildPulseTabs(_doc.ComA, ComAPulses, ref _selectedComA);
                RebuildPulseTabs(_doc.ComB, ComBPulses, ref _selectedComB);
                foreach (var row in GreyLevelRows) row.Refresh();
            }
            finally { _suspend = false; }

            OnPropertyChanged(nameof(Vst));
            OnPropertyChanged(nameof(MaxFrequencyText));
            OnPropertyChanged(nameof(ChannelTimeText));
            OnPropertyChanged(nameof(SelectedComAPulse));
            OnPropertyChanged(nameof(SelectedComBPulse));
            OnPropertyChanged(nameof(UnassignedGreyLevelsText));
            OnPropertyChanged(nameof(HasUnassignedGreyLevel));
            Changed?.Invoke();
        }

        private void RebuildPulseTabs(EpsonComChannel ch, ObservableCollection<PulseTabVm> tabs,
                                      ref PulseTabVm? selected)
        {
            // 탭을 통째로 다시 만들면 선택이 풀려 편집 중 화면이 튄다 — 인덱스를 기억해 되돌린다.
            int keep = selected == null ? 0 : Math.Max(0, tabs.IndexOf(selected));
            tabs.Clear();
            for (int i = 0; i < ch.Pulses.Count; i++)
                tabs.Add(new PulseTabVm(this, ch.Channel, i, ch.Pulses[i]));

            selected = tabs.Count == 0 ? null : tabs[Math.Min(keep, tabs.Count - 1)];
        }

        internal bool IsLoading => _suspend;
    }

    /// <summary>펄스 탭 하나 — 그 안에 세그먼트 행이 들어간다.</summary>
    public sealed class PulseTabVm
    {
        public PulseTabVm(WaveformEditorViewModel owner, ComChannelId ch, int index, EpsonWaveformPulse pulse)
        {
            Channel = ch;
            Index   = index;
            Header  = $"Pulse {index + 1}";
            Segments = new ObservableCollection<SegmentRowVm>(
                pulse.Segments.Select((s, i) => new SegmentRowVm(owner, s, i, i == pulse.Segments.Count - 1)));
            SegmentCountText = $"{pulse.Segments.Count}";
            TotalTimeText    = $"{pulse.TotalTimeUs:F2} µs";
        }

        public ComChannelId Channel { get; }
        public int    Index  { get; }
        public string Header { get; }
        public ObservableCollection<SegmentRowVm> Segments { get; }
        public string SegmentCountText { get; }

        /// <summary>이 펄스가 차지하는 시간 — 최대 주파수가 왜 그 값인지 여기서 읽힌다.</summary>
        public string TotalTimeText { get; }
    }

    /// <summary>세그먼트 한 줄. 값이 바뀌면 문서 전체를 다시 계산시킨다.</summary>
    public sealed class SegmentRowVm : ViewModelBase
    {
        private readonly WaveformEditorViewModel _owner;
        private readonly EpsonWaveformSegment _seg;

        public SegmentRowVm(WaveformEditorViewModel owner, EpsonWaveformSegment seg, int index, bool isLast)
        {
            _owner = owner;
            _seg   = seg;
            Header = $"Seg{index + 1}";
            IsLast = isLast;
        }

        public string Header { get; }

        /// <summary>마지막 세그먼트인가 — 도달 전압이 Vst 로 강제되므로 입력을 막는다.</summary>
        public bool IsLast { get; }
        public bool IsHoldVoltageEditable => !IsLast;

        public double Slew
        {
            get => _seg.Slew;
            set { _seg.Slew = value; Push(); }
        }
        public double SlewTimeUs
        {
            get => _seg.SlewTimeUs;
            set { _seg.SlewTimeUs = value; Push(); }
        }
        public double HoldVoltage
        {
            get => _seg.HoldVoltage;
            set { if (IsLast) return; _seg.HoldVoltage = value; Push(); }
        }
        public double HoldTimeUs
        {
            get => _seg.HoldTimeUs;
            set { _seg.HoldTimeUs = value; Push(); }
        }

        private void Push()
        {
            if (_owner.IsLoading) return;
            _owner.Recalculate();
        }
    }

    /// <summary>GL 한 줄(GL0~GL3) — 펄스마다 ComA/ComB 두 칸.</summary>
    public sealed class GreyLevelRowVm
    {
        public GreyLevelRowVm(WaveformEditorViewModel owner, int greyLevel)
        {
            GreyLevel = greyLevel;
            Header    = $"GL{greyLevel}";
            Cells = Enumerable.Range(0, EpsonComChannel.MaxPulses)
                .SelectMany(p => new[]
                {
                    new GreyLevelCellVm(owner, greyLevel, p, GreyLevelAssign.ComA),
                    new GreyLevelCellVm(owner, greyLevel, p, GreyLevelAssign.ComB),
                })
                .ToList();
        }

        public int    GreyLevel { get; }
        public string Header    { get; }
        public IReadOnlyList<GreyLevelCellVm> Cells { get; }

        public void Refresh() { foreach (var c in Cells) c.Refresh(); }
    }

    /// <summary>배정표 한 칸.</summary>
    public sealed class GreyLevelCellVm : ViewModelBase
    {
        private readonly WaveformEditorViewModel _owner;

        public GreyLevelCellVm(WaveformEditorViewModel owner, int greyLevel, int pulseIndex, GreyLevelAssign assign)
        {
            _owner     = owner;
            GreyLevel  = greyLevel;
            PulseIndex = pulseIndex;
            Assign     = assign;
            Label      = assign == GreyLevelAssign.ComA ? "ComA" : "ComB";
        }

        public int             GreyLevel  { get; }
        public int             PulseIndex { get; }
        public GreyLevelAssign Assign     { get; }
        public string          Label      { get; }

        /// <summary>이 칸이 선택된 상태인가.</summary>
        public bool IsOn => _owner.Document.GreyLevels[GreyLevel, PulseIndex] == Assign;

        public void Refresh() => OnPropertyChanged(nameof(IsOn));
    }
}
