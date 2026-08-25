using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>
    /// 글라스 자동 정렬에서 시퀀스가 바깥에 시키는 일.
    ///
    /// <para><b>왜 인터페이스인가</b>: 정렬은 카메라(패턴 찾기) · 레시피(마크 간격 · 허용 오차) ·
    /// 모터를 한꺼번에 쓴다. 그 셋을 시퀀스 계층이 직접 들고 있으면 장비 없이는 한 줄도 확인할 수
    /// 없다. 여기서는 <b>순서와 멈추는 조건</b>만 다루고, 실제 측정·이동은 바깥이 맡는다.</para>
    ///
    /// <para>각 메서드는 실패하면 예외를 던진다 — 시퀀스 실행기가 그 단계를 실패로 세우고 멈춘다.
    /// 정상이면 사람이 읽을 한 줄을 돌려주고, 그 줄이 로그에 남는다.</para>
    /// </summary>
    public interface IGlassAlignService
    {
        /// <summary>
        /// 정렬을 시작할 수 없는 이유. 시작할 수 있으면 null.
        ///
        /// <para>교정(µm/px) 없음 · 레시피에 마크 간격 없음 · 등록된 패턴 없음 등.
        /// 첫 단계에서 확인해 그 자리에서 멈춘다 — 반쯤 움직인 뒤 멈추면 글라스를 다시 놔야 한다.</para>
        /// </summary>
        string? NotReadyReason { get; }

        /// <summary>보정을 몇 번까지 되풀이할지(레시피). 이 횟수 안에 허용 오차로 못 들어오면 실패다.</summary>
        int MaxPasses { get; }

        /// <summary>마크1 자리(티칭 포인트)로 이동.</summary>
        Task<string> MoveToMark1Async(CancellationToken ct);

        /// <summary>마크2 자리로 이동 — 레시피의 피듀셜 간격만큼 상대 이동한다.</summary>
        Task<string> MoveToMark2Async(CancellationToken ct);

        /// <summary>지금 화면에서 마크를 찾아 기억한다. <paramref name="slot"/> 은 1 또는 2.</summary>
        Task<string> MeasureAsync(int slot, CancellationToken ct);

        /// <summary>
        /// 기억한 두 측정으로 각도를 내고 T 를 보정한다.
        /// 허용 오차 안이면 <b>돌리지 않는다</b> — 스테이지가 못 내는 이동을 명령하지 않기 위해서다.
        /// </summary>
        Task<string> CorrectRotationAsync(CancellationToken ct);

        /// <summary>마크1 을 다시 재고 X·Y 를 보정한다. 허용 오차 안이면 움직이지 않는다.</summary>
        Task<string> CorrectShiftAsync(CancellationToken ct);

        /// <summary>지금 상태가 허용 오차 안인지. 들어왔으면 true.</summary>
        Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct);
    }

    /// <summary>
    /// 시퀀스가 쓸 정렬 서비스를 꽂는 자리.
    ///
    /// <para>시퀀스 목록(<see cref="SequenceRegistry"/>)은 정적이라 생성자로 넘길 자리가 없다.
    /// 화면이 뜰 때 HMI 가 한 번 꽂고, 안 꽂혀 있으면 첫 단계에서 그 사실을 말하고 멈춘다 —
    /// 조용히 아무것도 안 하는 것보다 낫다.</para>
    /// </summary>
    public static class GlassAlignServices
    {
        public static IGlassAlignService? Current { get; set; }
    }
}
