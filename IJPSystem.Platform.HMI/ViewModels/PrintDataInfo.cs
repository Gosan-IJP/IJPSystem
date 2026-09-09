namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>
    /// 메인화면이 START 옆에 띄우는 인쇄 정보 한 묶음.
    ///
    /// <para>
    /// <b>왜 여기까지 끌고 오는가</b>: START 를 누르기 직전이 "무엇을, 어디서 어디까지 찍는가" 를
    /// 확인할 마지막 자리다. 예전에는 이 화면에서 알 길이 없어 패턴 인쇄 화면까지 가야 했고,
    /// Print Run 인데 데이터가 안 올라가 있으면 모션만 돈 것을 인쇄한 줄 알았다.
    /// </para>
    /// <para>
    /// <b>종료는 계산값이다.</b> 시작(티칭 PRINT ORIGIN) + 패턴 길이 — 티칭된 PRINT END 가
    /// 아니다. 끝점을 티칭으로 두면 진실이 둘이 되어, 패턴을 바꾼 뒤 티칭이 낡아도 아무도
    /// 모르는 채 뒷부분이 안 찍힌다.
    /// </para>
    /// </summary>
    /// <param name="Title">인쇄 데이터 이름(가상이면 [가상] 이 붙는다).</param>
    /// <param name="Detail">스텝 × 노즐 · 버퍼 번호 · 적재 시각.</param>
    /// <param name="Blocked">지금 시작하면 안 되는 상태인가 — 화면이 주황으로 바꾼다.</param>
    /// <param name="Start">인쇄 시작 위치(티칭).</param>
    /// <param name="End">인쇄 종료 위치(계산) + 길이.</param>
    /// <param name="RangeWarn">종료가 티칭 주행을 넘거나 티칭이 없는가.</param>
    /// <param name="Width">원본 폭과 헤드가 한 번에 덮는 폭.</param>
    /// <param name="Swath">필요한 스와스 수.</param>
    /// <param name="SwathWarn">스와스가 2 이상인가 — 지금은 첫 폭만 나간다.</param>
    public sealed record PrintDataInfo(
        string Title,
        string Detail,
        bool   Blocked   = false,
        string Start     = "-",
        string End       = "-",
        bool   RangeWarn = false,
        string Width     = "-",
        string Swath     = "-",
        bool   SwathWarn = false);
}
