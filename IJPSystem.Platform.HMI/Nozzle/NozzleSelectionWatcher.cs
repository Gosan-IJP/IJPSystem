using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Infrastructure.Print;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// 선택된 노즐을 <b>화면에 보이게</b> 하는 한 곳.
    ///
    /// <para><b>왜 필요한가</b>: Nozzle Select 창을 닫고 나면 아무 흔적이 없었다. 골랐는지,
    /// 취소했는지, 몇 개인지 화면이 구분해 주지 않아서 <b>Spit 을 눌러 실패해야</b> 알 수 있었다.</para>
    ///
    /// <para><b>왜 싱글톤인가</b>: <see cref="NozzleControlGlobal"/> 이 장비 전역이라 선택값도 하나다.
    /// 패턴 인쇄에서 고른 노즐이 드랍와처·P&amp;ID·웨이브폼에도 그대로 적용된다. 화면마다 요약을
    /// 따로 들면 같은 값을 네 벌 계산하게 되고, 그러다 한 곳이 갱신에서 빠진다
    /// (스핏 버튼이 정확히 그래서 <c>SpitService</c> 로 모았다).</para>
    ///
    /// <para>부수적으로 <b>구독 누수도 막는다</b>. VM 마다 전역 이벤트를 구독하면 화면이 다시
    /// 만들어질 때마다 옛 VM 이 이벤트에 매달려 남는데, 네 VM 중 둘은 Dispose 가 없어 풀 자리도
    /// 없다. 여기 하나만 앱 수명 동안 구독한다.</para>
    /// </summary>
    public sealed class NozzleSelectionWatcher : ViewModelBase
    {
        public static NozzleSelectionWatcher Instance { get; } = new();

        private NozzleSelectionWatcher()
        {
            NozzleControlGlobal.Instance.UsingNozzleChanged += (_, _) => Refresh();
            Refresh();
        }

        /// <summary>버튼 라벨 옆에 붙는 짧은 요약. 번호는 넣지 않는다 — 3200개까지 갈 수 있다.</summary>
        public string Summary { get; private set; } = "";

        /// <summary>툴팁용 상세. 범위로 압축한다(예: "1~40, 100~120").</summary>
        public string Detail { get; private set; } = "";

        /// <summary>하나도 안 골랐는가. 버튼 색을 바꿔 Spit 전에 알아채게 한다.</summary>
        public bool IsEmpty { get; private set; } = true;

        public int Count { get; private set; }

        private void Refresh()
        {
            var sel = NozzleControlGlobal.Instance.UsingNozzle;
            Count   = sel.Count;
            IsEmpty = Count == 0;
            Summary = IsEmpty ? "미선택" : $"{Count}개 선택";

            Detail = IsEmpty
                // 장비 전역이라는 사실을 여기서 알린다 — "이 화면 설정"으로 오해하면
                // 다른 화면에서 바꾼 값이 들어와 있는 것을 설명할 수 없다.
                ? "선택된 노즐이 없습니다. Nozzle Select 로 지정하세요.\n" +
                  "이 선택은 장비 전체가 공유합니다(패턴 인쇄·드랍와처·P&ID·웨이브폼)."
                // 구간으로 접는다 — 전 노즐 선택이면 "1~3200" 한 줄이지만, 번호를 늘어놓으면
                // 15KB 가 넘어 툴팁으로 못 쓴다. 구간이 많을 때는 앞쪽만 보이고 나머지는 개수로.
                : $"{NozzleRangeText.Summarize(sel.UsingNozzles, maxRanges: 8)}  (총 {Count}개)\n" +
                  "이 선택은 장비 전체가 공유합니다(패턴 인쇄·드랍와처·P&ID·웨이브폼).";

            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Count));
        }
    }
}
