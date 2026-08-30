using IJPSystem.Platform.Domain.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>
    /// 글라스 자동 정렬 — 마크 두 개로 각도를 재고 T·X·Y 를 맞추는 연속 동작.
    ///
    /// <para><b>왜 이 순서인가</b></para>
    /// <para>
    /// 마크2 는 글라스에서 마크1 보다 +Y 쪽에 있다. 카메라는 고정이므로 마크2 를 렌즈 밑으로
    /// 데려오려면 글라스를 <b>-Y 로</b> 민다(레시피의 피듀셜 간격만큼). 두 자리에서 잰 픽셀 위치와
    /// 명령한 이동량을 합치면 두 마크의 실제 간격 벡터가 나오고, 그것이 설계 간격에서 몇 도
    /// 돌아 있는지가 곧 글라스 회전이다. 기선이 160mm 로 길어 각도가 아주 곱게 나온다.
    /// </para>
    /// <para>
    /// 회전을 고친 뒤 <b>마크1 을 다시 재는</b> 이유: T 는 척 회전중심을 기준으로 돌기 때문에
    /// 돌리면 글라스가 딸려 움직인다. 그 이동량을 계산하려면 회전중심을 교정해야 하는데,
    /// 돌린 다음 다시 재면 그 이동까지 함께 잡힌다 — 사진 한 장 값으로 교정 하나를 없앤 셈이다.
    /// </para>
    /// <para>
    /// X·Y 검증에서 허용 오차 안으로 못 들어오면 <b>보정을 되풀이한다</b>(레시피의 반복 상한).
    /// 그래도 못 들어오면 실패로 세운다 — 못 맞춘 글라스를 맞춘 것으로 알고 인쇄하면 안 된다.
    /// </para>
    /// <para>
    /// 마지막에 <b>마크2 로 한 번 더 가서 각도를 다시 잰다</b>. X·Y 는 기울어진 채로도 맞출 수
    /// 있어서, 마크1 만 보고 끝내면 T 를 반대로 돌려 기울기가 두 배가 된 글라스도 "완료"로
    /// 나간다. 이동 한 번·사진 한 장을 더 쓰고 그 경우를 없앤다.
    /// </para>
    /// </summary>
    public static class GlassAlignSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion)
            => Build(machine, motion, GlassAlignServices.Current);

        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, IGlassAlignService? align)
        {
            var steps = new List<SequenceStepDef>();
            int n = 0;
            foreach (var (name, action) in Definitions(machine, align))
                steps.Add(new SequenceStepDef(++n, name, action));
            return steps;
        }

        /// <summary>
        /// 인쇄 시퀀스 안에 끼워 넣을 정렬 단계 — 번호는 부르는 쪽이 이어서 매긴다.
        ///
        /// <para><b>레시피가 [미사용]이면 아무 단계도 내지 않는다.</b> 단계를 내 놓고 안에서
        /// 건너뛰면, 화면에는 정렬하는 것처럼 보이는데 실제로는 아무 일도 없는 목록이 남는다 —
        /// 목록은 실제로 도는 것만 보여야 한다.</para>
        ///
        /// <para>정렬 서비스가 안 꽂혀 있어도 비운다. 그 상태에서는 레시피 설정을 읽을 길도
        /// 없어서, 켜져 있는지조차 말할 수 없기 때문이다(HMI 는 시작할 때 반드시 꽂는다).</para>
        /// </summary>
        public static IReadOnlyList<SequenceStepDef> Embedded(
            IMachine machine, IGlassAlignService? align, int startNumber)
        {
            if (align == null || !align.IsEnabled) return System.Array.Empty<SequenceStepDef>();

            var steps = new List<SequenceStepDef>();
            int n = startNumber - 1;
            foreach (var (name, action) in Definitions(machine, align))
                steps.Add(new SequenceStepDef(++n, name, action));
            return steps;
        }

        /// <summary>단계의 알맹이 — 번호만 빼고 여기 한 벌만 둔다.</summary>
        private static IEnumerable<(string Name, Func<CancellationToken, Task> Action)> Definitions(
            IMachine machine, IGlassAlignService? align)
        {
            // 시작 전에 갖춰졌는지 먼저 본다. 반쯤 움직인 뒤 멈추면 글라스를 다시 놔야 한다.
            yield return ("Step_GlassAlign_Ready",
                ct => Task.Run(() =>
                {
                    var a = Require(align);
                    string? why = a.NotReadyReason;
                    if (why != null) throw new InvalidOperationException(why);
                }, ct));

            yield return ("Step_GlassAlign_Mark1Move",
                ct => Require(align).MoveToMark1Async(ct));

            yield return ("Step_GlassAlign_Mark1MoveDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct));

            yield return ("Step_GlassAlign_Mark1Find",
                ct => Require(align).MeasureAsync(1, ct));

            yield return ("Step_GlassAlign_Mark2Move",
                ct => Require(align).MoveToMark2Async(ct));

            yield return ("Step_GlassAlign_Mark2MoveDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 30_000, ct));

            yield return ("Step_GlassAlign_Mark2Find",
                ct => Require(align).MeasureAsync(2, ct));

            yield return ("Step_GlassAlign_Rotate",
                ct => Require(align).CorrectRotationAsync(ct));

            yield return ("Step_GlassAlign_RotateDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct));

            // 회전으로 딸려 나간 이동까지 여기서 함께 잡는다.
            // ※ X·Y 만 되돌린다 — 티칭 포인트에 T 가 들어 있어 절대 이동을 하면 방금 준
            //   회전 보정이 지워진다(실장 2026-08-27, ReturnToMark1Async 참고).
            yield return ("Step_GlassAlign_Mark1Return",
                ct => Require(align).ReturnToMark1Async(ct));

            yield return ("Step_GlassAlign_Mark1ReturnDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 30_000, ct));

            yield return ("Step_GlassAlign_Shift",
                ct => CorrectUntilInToleranceAsync(machine, align, ct));

            yield return ("Step_GlassAlign_Verify",
                ct => VerifyAsync(align, ct));

            // 마크1 만 보고 끝내지 않는다 — X·Y 는 기울어진 채로도 맞출 수 있어서,
            // T 를 반대로 돌려 기울기가 두 배가 된 글라스가 "완료"로 나갈 수 있다.
            yield return ("Step_GlassAlign_Mark2Recheck",
                ct => Require(align).MoveToMark2Async(ct));

            yield return ("Step_GlassAlign_Mark2RecheckDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 30_000, ct));

            yield return ("Step_GlassAlign_VerifyAngle",
                ct => VerifyAngleAsync(align, ct));
        }

        /// <summary>
        /// 허용 오차 안으로 들어올 때까지 X·Y 보정을 되풀이한다.
        ///
        /// <para>한 번에 들어오지 않는 이유는 여러 가지다 — 이동 오차, 백래시, 측정 잡음.
        /// 되풀이하면 대개 두 번째에 들어온다. 상한을 두는 이유는 못 들어오는 경우
        /// (교정이 틀렸거나 글라스가 잘못 놓였다) 끝없이 도는 것을 막기 위해서다.</para>
        /// </summary>
        private static async Task CorrectUntilInToleranceAsync(
            IMachine machine, IGlassAlignService? align, CancellationToken ct)
        {
            var a = Require(align);
            int passes = Math.Max(1, a.MaxPasses);

            for (int i = 1; i <= passes; i++)
            {
                await a.CorrectShiftAsync(ct);
                await WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct);

                var (ok, _) = await a.VerifyAsync(ct);
                if (ok) return;
            }
            // 여기서 던지지 않는다 — 마지막 검증 단계가 판정을 맡는다.
            // 이 단계까지 실패로 세우면 같은 사실이 두 번 보고된다.
        }

        private static async Task VerifyAsync(IGlassAlignService? align, CancellationToken ct)
        {
            var (ok, message) = await Require(align).VerifyAsync(ct);
            if (!ok) throw new InvalidOperationException(message);
        }

        private static async Task VerifyAngleAsync(IGlassAlignService? align, CancellationToken ct)
        {
            var (ok, message) = await Require(align).VerifyAngleAsync(ct);
            if (!ok) throw new InvalidOperationException(message);
        }

        /// <summary>서비스가 안 꽂혀 있으면 조용히 넘어가지 않고 그 사실을 말한다.</summary>
        private static IGlassAlignService Require(IGlassAlignService? align)
            => align ?? throw new InvalidOperationException(
                "정렬 서비스가 연결되지 않았습니다 — 글라스 화면을 한 번 연 뒤 다시 실행하세요.");
    }
}
