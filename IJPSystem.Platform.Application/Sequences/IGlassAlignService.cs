using System;
using System.Threading;
using System.Threading.Tasks;
using IJPSystem.Platform.Domain.Models.Vision;   // VisionImage — 잰 사진을 화면으로 흘려 줄 때

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

        /// <summary>
        /// 레시피가 자동 정렬을 쓰기로 돼 있는가(기본 설정 → 자동 정렬).
        ///
        /// <para>피듀셜 마크가 없는 품종도 있어 장비가 아니라 <b>품종</b>이 정한다.
        /// 인쇄 시퀀스는 이 값이 false 면 정렬 단계를 아예 만들지 않는다.</para>
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>마크1 자리(티칭 포인트)로 이동. T 를 포함한 전 축이 티칭 값으로 간다 — 시작 기준을 잡는 자리다.</summary>
        /// <summary>
        /// 새 판을 시작한다 — 앞 판의 오차 기록을 지운다.
        ///
        /// <para>정렬은 "보정 뒤 오차가 줄었는가"로 교정 부호가 맞는지 판정한다. 앞 판의
        /// 오차가 남아 있으면 새 글라스의 첫 측정을 남의 값과 견주게 된다. 예전에는
        /// <see cref="MoveToMark1Async"/> 가 이 일을 겸했는데, 그 이동을 내지 않는 경로가
        /// 생기면서(글라스 화면 [Auto Align]) 지우는 자리를 따로 뒀다.</para>
        /// </summary>
        void BeginRun();

        Task<string> MoveToMark1Async(CancellationToken ct);

        /// <summary>
        /// 회전 보정을 끝낸 뒤 마크1 자리로 <b>돌아온다 — T 는 건드리지 않는다.</b>
        ///
        /// <para>GLASS ALIGN 티칭 포인트에는 T 도 들어 있어서, 그냥 절대 이동하면 <b>방금 준
        /// 회전 보정이 그대로 지워진다</b>. 실장 로그(2026-08-27)에서 T 를 -0.031° 돌린 65ms 뒤에
        /// 절대 이동이 T 를 티칭 값으로 되돌렸고, 그래서 몇 판을 돌려도 측정 각도가 -0.031° 에서
        /// 꿈쩍하지 않았다.</para>
        ///
        /// <para>그래서 X·Y 만 티칭 값으로 되돌린다. Z 는 정렬 중 움직이지 않으므로 그대로 둔다.</para>
        /// </summary>
        Task<string> ReturnToMark1Async(CancellationToken ct);

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

        /// <summary>
        /// 회전이 실제로 펴졌는지 — 마크2 를 다시 재서 각도를 다시 낸다.
        ///
        /// <para>X·Y 는 기울어져 있어도 맞출 수 있다. 그래서 마크1 만 보고 끝내면 T 방향을
        /// 반대로 잡아 기울기가 두 배가 된 글라스도 "정렬 완료"로 나간다.</para>
        /// </summary>
        Task<(bool Ok, string Message)> VerifyAngleAsync(CancellationToken ct);
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

        // ── 정렬이 도는 중인가 ────────────────────────────────────────────
        //
        // 정렬 단계를 도는 자리가 <b>셋</b>이다: 자동 인쇄(MainDashboardViewModel), 글라스 화면의
        // [Auto Align], 시퀀스 화면의 GLASS ALIGN. 그런데 대시보드 애니메이션은 자기가 돌린
        // 경우만 알고 있어서, 나머지 둘로 돌리면 글라스가 파킹 자리에 붙어 있었다
        // (2026-08-28 실장 — "얼라인 동작시 스테이지가 Y방향으로 움직이지 않아요").
        //
        // 그래서 "지금 정렬 중"이라는 사실 하나를 여기 한 곳에 둔다. 돌리는 쪽이 <see cref="BeginRun"/>
        // 로 감싸기만 하면, 보는 쪽(대시보드)은 누가 돌렸는지 몰라도 된다.

        private static int _running;

        /// <summary>정렬 단계가 돌고 있는가. 어느 화면에서 시작했는지와 무관하다.</summary>
        public static bool IsRunning => System.Threading.Volatile.Read(ref _running) > 0;

        /// <summary><see cref="IsRunning"/> 이 바뀌었다. 구독자는 UI 스레드가 아닐 수 있음에 주의.</summary>
        public static event Action<bool>? RunningChanged;

        /// <summary>
        /// 정렬 한 판을 감싼다 — <c>using var _ = GlassAlignServices.BeginRun();</c>
        ///
        /// <para>세는 방식인 이유: 자동 인쇄 안에서 돌던 중에 누가 또 시작해도 먼저 끝난 쪽이
        /// 깃발을 내려 버리지 않는다. 예외로 빠져나가도 <c>using</c> 이 반드시 내린다.</para>
        /// </summary>
        public static IDisposable BeginRun() => new RunScope();

        private sealed class RunScope : IDisposable
        {
            private int _done;

            public RunScope()
            {
                if (System.Threading.Interlocked.Increment(ref _running) == 1) Raise(true);
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _done, 1) != 0) return;   // 두 번 불려도 한 번만
                if (System.Threading.Interlocked.Decrement(ref _running) == 0) Raise(false);
            }
        }

        /// <summary>구독자가 던져도 정렬을 세우지 않는다 — 화면 갱신 실패가 장비를 멈출 이유는 없다.</summary>
        private static void Raise(bool running)
        {
            try { RunningChanged?.Invoke(running); } catch { }
        }

        // ── 지금 재고 있는가 ──────────────────────────────────────────────
        //
        // 정렬이 마크를 <b>재는 순간</b>에는 그 카메라를 보는 다른 화면이 비켜야 한다.
        // 소비자가 하나뿐이어야 "그 사진이 라이브와 겹친 것 아니냐"를 나중에 따질 일이 없다.
        //
        // 글라스 화면은 자기 안에서 잠금(HoldLiveForCapture)을 걸어 해결했지만, 같은 카메라를
        // 보는 창이 <b>화면 밖에도</b> 생겼다(대시보드 GVC 팝업). 그쪽은 GlassViewModel 을
        // 들고 있지 않으므로, 정렬이 재는 중이라는 사실을 여기 한 곳에서 알려야 한다.
        //
        // <see cref="IsRunning"/> 과 나누는 이유: 한 판은 20초가 넘는데 그동안 라이브를 세우면
        // 마크가 시야로 들어오는지 볼 수가 없다. 비켜 주는 것은 <b>찍는 순간</b>뿐이다.

        private static int _capturing;

        /// <summary>정렬이 지금 사진을 찍고 있는가. 같은 카메라를 보는 라이브는 이 동안 건너뛴다.</summary>
        public static bool Capturing => System.Threading.Volatile.Read(ref _capturing) > 0;

        /// <summary>찍는 동안을 감싼다 — <c>using var _ = GlassAlignServices.BeginCapture();</c></summary>
        public static IDisposable BeginCapture() => new CaptureScope();

        private sealed class CaptureScope : IDisposable
        {
            private int _done;

            public CaptureScope() => System.Threading.Interlocked.Increment(ref _capturing);

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _done, 1) == 0)
                    System.Threading.Interlocked.Decrement(ref _capturing);
            }
        }

        // ── 잰 사진을 화면으로 ────────────────────────────────────────────
        //
        // 정렬이 재는 동안 라이브는 비켜 서 있다. 그 사이 화면에 남는 것은 <b>직전에 받은</b>
        // 프레임인데, 이동 직후라면 그것이 이동 중에 찍힌 얼룩진 그림이다(노출 15ms 에
        // 순항속도면 한 장이 화면 높이 전체를 훑는다). 정지했는데 화면은 아직 흐른다.
        //
        // 그래서 정렬이 <b>실제로 쓴 그 사진</b>을 그대로 흘려 준다. 화면은 잠금이 풀리기 전에
        // 정지 후의 그림으로 바뀌고, 덤으로 "매칭이 무엇을 보고 그 점수를 냈는지"가 남는다.
        //
        // 구독자는 UI 스레드가 아닐 수 있다. 그리고 <b>즉시</b> 자기 것으로 복사해야 한다.

        /// <summary>정렬이 마크를 잰 사진. 8비트 그레이.</summary>
        public static event Action<VisionImage>? FrameMeasured;

        /// <summary>구독자가 던져도 정렬을 세우지 않는다 — 화면 갱신 실패가 장비를 멈출 이유는 없다.</summary>
        public static void PublishMeasuredFrame(VisionImage img)
        {
            try { FrameMeasured?.Invoke(img); } catch { }
        }

        // ── 한 번 재고 난 결과 ────────────────────────────────────────────
        //
        // 대시보드 시각화가 정렬 카메라를 <b>실제 사건</b>에 맞춰 그리기 위한 신호다.
        // 타이머로 깜빡이면 그림이 거짓말을 한다 — 몇 번 찍었는지, 그 판이 잘 잡혔는지를
        // 화면이 말해 줘야 로그를 안 열고도 상태를 안다.

        /// <summary>한 번 잰 결과. 색으로 구분할 만큼만 나눈다.</summary>
        public enum MarkVerdict
        {
            /// <summary>찾았고 점수도 무난하다.</summary>
            Good,
            /// <summary>찾긴 했는데 마크1 보다 점수가 크게 낮다 — 이 판의 각도는 그만큼만 믿는다.</summary>
            Weak,
            /// <summary>못 찾았거나 위치를 믿을 수 없다.</summary>
            NotFound,
        }

        /// <summary>마크를 한 번 쟀다. 구독자는 UI 스레드가 아닐 수 있다.</summary>
        public static event Action<MarkVerdict>? MarkMeasured;

        /// <summary>구독자가 던져도 정렬을 세우지 않는다.</summary>
        public static void PublishMarkMeasured(MarkVerdict verdict)
        {
            try { MarkMeasured?.Invoke(verdict); } catch { }
        }
    }
}
