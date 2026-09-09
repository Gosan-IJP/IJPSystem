using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Pattern Print — 패턴 인쇄 화면의 인쇄 시퀀스.</summary>
    /// <remarks>
    /// <para><b>알맹이는 <see cref="PrintCycleSequence"/> 에 있다</b> — 오토프린트와 같은 것을 쓴다.
    /// 진공 온오프 · 헤드 업다운 · Meteor 준비 · 스와스 루프는 두 화면 모두에 필요하고,
    /// 실제로 다른 것은 <b>얼라인을 하느냐</b> 하나뿐이다(2026-09-09 확인).</para>
    ///
    /// <para><b>패턴 인쇄는 정렬하지 않는다.</b> 티칭해 둔 인쇄 원점에 그대로 찍어 보는 시험
    /// 인쇄라, 피듀셜 마크를 찾을 이유가 없다. 마크가 있는 제품 글라스에 맞춰 찍는 것은
    /// 오토프린트의 일이다.</para>
    ///
    /// <para>사전조건: Print Image Design 으로 패턴 생성 → [인쇄 데이터 로드] 로 엔진 버퍼에 적재
    /// (그 버퍼 번호가 <see cref="PrintRunOptions.BufferId"/> 로 들어온다) →
    /// Set Print Origin 으로 인쇄 원점 티칭.</para>
    /// </remarks>
    public static class PatternPrintSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion)
            => Build(machine, motion, new PrintRunOptions());

        /// <param name="opts">인쇄 명령 상대까지 담은 전체 설정. <c>Job</c> 이 null 이면 Dry Run.</param>
        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, PrintRunOptions opts)
            => PrintCycleSequence.Build(machine, motion, opts, useGlassAlign: false);
    }
}
