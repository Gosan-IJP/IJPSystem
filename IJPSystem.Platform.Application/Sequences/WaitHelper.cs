using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.Domain.Models.Vision;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>
    /// 시퀀스 스텝에서 조건 대기를 처리하는 유틸리티.
    ///
    /// [Virtual 모드 동작 원리]
    /// - ForMotionDone : VirtualMotionDriver 타이머가 IsMoving=false, IsInPosition=true를 자동 세팅
    /// - ForIOSignal   : 시퀀스에서 machine.IO.ScheduleInput()으로 가상 신호를 예약한 뒤 호출
    ///                   실제 하드웨어에서는 ScheduleInput이 no-op이고 물리 신호가 자연 발생
    /// - ForVisionResult: CaptureAndInspectAsync() 실행 후 LastResult를 폴링
    /// </summary>
    public static class WaitHelper
    {
        // ────────────────────────────────────────────────
        // 1. 기본 조건 폴링 (모든 Wait의 핵심)
        // ────────────────────────────────────────────────

        /// <summary>
        /// condition이 true가 될 때까지 pollMs 간격으로 폴링합니다.
        /// timeoutMs 이내에 만족되지 않으면 TimeoutException을 던집니다.
        /// </summary>
        /// <param name="describeFailure">
        /// 타임아웃 순간의 실제 상태를 한 줄로 만들어 예외 메시지에 붙인다.
        /// "타임아웃" 한 줄만 남으면 어느 축/신호 때문인지 알 수 없어 현장 분석이 막힌다.
        /// </param>
        public static async Task ForCondition(
            Func<bool> condition,
            int timeoutMs,
            CancellationToken ct,
            int pollMs = 20,
            Func<string>? describeFailure = null)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    string detail = SafeDescribe(describeFailure);
                    throw new TimeoutException(
                        $"조건 미충족 — 제한 시간 {timeoutMs}ms 초과" +
                        (string.IsNullOrEmpty(detail) ? "" : $" · {detail}"));
                }
                await Task.Delay(pollMs, ct);
            }
        }

        // 진단 문자열을 만들다 실패해도 원래 타임아웃 예외를 잃지 않게 감싼다.
        private static string SafeDescribe(Func<string>? describe)
        {
            if (describe == null) return "";
            try { return describe(); }
            catch (Exception ex) { return $"(상태 수집 실패: {ex.GetType().Name})"; }
        }

        // ────────────────────────────────────────────────
        // 2. IO 신호 대기
        // ────────────────────────────────────────────────

        /// <summary>
        /// IO 입력 신호가 expected 값이 될 때까지 대기합니다.
        /// Virtual 모드: 호출 전에 machine.IO.ScheduleInput()으로 신호를 예약하세요.
        /// </summary>
        public static Task ForIOSignal(
            IIODriver io,
            string indexName,
            bool expected,
            int timeoutMs,
            CancellationToken ct,
            int pollMs = 20)
            => ForCondition(
                () => io.GetInput(indexName) == expected,
                timeoutMs, ct, pollMs,
                () => $"IO '{indexName}' 기대={OnOff(expected)} 현재={OnOff(io.GetInput(indexName))}");

        // ────────────────────────────────────────────────
        // 3. 모션 완료 대기
        // ────────────────────────────────────────────────

        /// <summary>단일 축의 IsMoving == false 대기</summary>
        public static Task ForMotionDone(
            IMotionDriver motion,
            string axisNo,
            int timeoutMs,
            CancellationToken ct,
            int pollMs = 50)
            => ForCondition(
                () => !motion.GetStatus(axisNo).IsMoving,
                timeoutMs, ct, pollMs,
                () => "정지하지 않은 축: " + Describe(motion.GetStatus(axisNo)));

        /// <summary>모든 축의 IsMoving == false 대기</summary>
        public static Task ForAllMotionDone(
            IMotionDriver motion,
            int timeoutMs,
            CancellationToken ct,
            int pollMs = 50)
            => ForCondition(
                () => motion.GetAllStatus().All(s => !s.IsMoving),
                timeoutMs, ct, pollMs,
                () =>
                {
                    var busy = motion.GetAllStatus().Where(s => s.IsMoving).ToList();
                    return busy.Count == 0
                        ? "정지하지 않은 축 없음(조건 재평가 시 해소 — 폴링 경합 의심)"
                        : "정지하지 않은 축: " + string.Join(", ", busy.Select(Describe));
                });

        /// <summary>단일 축의 IsInPosition == true 대기</summary>
        public static Task ForInPosition(
            IMotionDriver motion,
            string axisNo,
            int timeoutMs,
            CancellationToken ct,
            int pollMs = 50)
            => ForCondition(
                () => motion.GetStatus(axisNo).IsInPosition,
                timeoutMs, ct, pollMs,
                () => "InPosition 미도달: " + Describe(motion.GetStatus(axisNo)));

        // 타임아웃 진단용 축 상태 한 줄. 원인 판별에 필요한 최소 항목만 담는다.
        private static string Describe(AxisStatus s) =>
            $"{s.AxisNo}(pos={s.CurrentPos:F3} target={s.TargetPos:F3} moving={s.IsMoving} " +
            $"inPos={s.IsInPosition} servo={s.IsServoOn} err={s.FollowingError:F3}" +
            (s.IsAlarm ? $" ALARM={s.AlarmCode}:{s.AlarmMessage}" : "") + ")";

        private static string OnOff(bool v) => v ? "ON" : "OFF";

        // ────────────────────────────────────────────────
        // 4. 비전 검사 완료 대기
        // ────────────────────────────────────────────────

        /// <summary>
        /// 카메라의 LastResult가 null이 아닐 때까지 대기하고 결과를 반환합니다.
        /// 호출 전 CaptureAndInspectAsync()를 Fire-and-forget으로 실행하거나,
        /// 이 메서드가 직접 검사를 실행하는 오버로드를 사용하세요.
        /// </summary>
        public static async Task<InspectionResult?> ForVisionResult(
            IVisionDriver vision,
            string cameraId,
            int timeoutMs,
            CancellationToken ct,
            int pollMs = 50)
        {
            // LastResult를 null로 초기화한 뒤 새 결과가 들어올 때까지 대기
            var status = vision.GetStatus(cameraId);
            status.LastResult = null;

            await ForCondition(() => status.LastResult != null, timeoutMs, ct, pollMs);
            return status.LastResult;
        }

        /// <summary>촬영+검사를 실행하고 결과가 나올 때까지 대기합니다.</summary>
        public static async Task<InspectionResult?> CaptureAndWait(
            IVisionDriver vision,
            string cameraId,
            int timeoutMs,
            CancellationToken ct)
        {
            var status = vision.GetStatus(cameraId);
            status.LastResult = null;

            // 검사를 비동기로 시작
            _ = Task.Run(async () =>
            {
                var result = await vision.CaptureAndInspectAsync(cameraId);
                status.LastResult = result;
            }, ct);

            await ForCondition(() => status.LastResult != null, timeoutMs, ct, pollMs: 50);
            return status.LastResult;
        }
    }
}
