using IJPSystem.Platform.Common.Enums;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>
    /// 시퀀스 스텝 실행을 감싸 <b>시작 / 완료(소요시간) / 취소 / 실패</b>를 남긴다.
    ///
    /// 이게 없으면 시퀀스 전체의 시작·완료만 로그에 남아, 현장에서 "자동운전이 멈췄다"는
    /// 신고가 왔을 때 어느 스텝에서 걸렸는지 재현 없이는 알 수 없다.
    ///   · 시작 로그  → 멈춘 지점 특정(마지막 "시작"만 있고 "완료"가 없는 스텝)
    ///   · 소요시간   → 특정 스텝만 느려지는 이상 감지
    ///   · 실패 메시지 → WaitHelper 가 붙인 원인(미정지 축·IO 현재값)이 그대로 실린다
    ///
    /// 스텝 실행부가 화면(ViewModel)마다 흩어져 있어 각 실행부에서 이 헬퍼를 호출한다.
    /// </summary>
    public static class SequenceStepLogger
    {
        /// <param name="seqName">시퀀스 이름(AutoPrint / Initialize / PatternPrint …)</param>
        /// <param name="log">로그 싱크. HMI 에서는 MainViewModel.AddLog 를 넘긴다.</param>
        public static Task RunAsync(
            SequenceStepDef step,
            string seqName,
            CancellationToken ct,
            Action<string, LogLevel> log)
            => RunAsync(step.Number, step.Name, step.Action, seqName, ct, log);

        /// <summary>
        /// 스텝 실행부가 UI 모델(SequenceStep 등) 을 들고 있는 경우를 위한 오버로드 —
        /// 번호/이름/동작만 있으면 형식에 상관없이 같은 로그를 남긴다.
        /// </summary>
        public static async Task RunAsync(
            int number,
            string stepName,
            Func<CancellationToken, Task> action,
            string seqName,
            CancellationToken ct,
            Action<string, LogLevel> log)
        {
            string head = $"[SEQ] {seqName} step {number} {stepName}";
            log($"{head} — 시작", LogLevel.Info);

            var sw = Stopwatch.StartNew();
            try
            {
                await action(ct);
                log($"{head} — 완료 ({sw.Elapsed.TotalSeconds:F2}s)", LogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                // 일시정지·중단·알람에 의한 취소. 실패가 아니므로 Warning.
                log($"{head} — 취소 ({sw.Elapsed.TotalSeconds:F2}s)", LogLevel.Warning);
                throw;
            }
            catch (Exception ex)
            {
                log($"{head} — 실패 ({sw.Elapsed.TotalSeconds:F2}s): {ex.Message}", LogLevel.Error);
                throw;
            }
        }
    }
}
