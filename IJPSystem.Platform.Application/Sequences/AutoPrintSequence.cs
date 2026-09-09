using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Auto Print — 자동 인쇄 시퀀스 (멀티 스와스 지원).</summary>
    /// <remarks>
    /// SequenceStepDef.Name 은 표시용 <b>번역 키</b>. HMI 가 Loc.T(key) 로 화면에 표시한다.
    /// 키→번역은 Common/Resources/Languages/ko-KR.xaml, en-US.xaml 참조.
    ///
    /// <para><b>알맹이는 <see cref="PrintCycleSequence"/> 에 있다.</b> 패턴 인쇄와 다른 것은
    /// 얼라인 사용 여부 하나뿐이라, 두 벌로 두면 한쪽만 고치는 일이 반드시 생긴다.
    /// 여기는 "오토프린트는 정렬을 한다" 만 말한다.</para>
    ///
    /// 프린팅수(swathCount) — 레시피 기본 설정:
    ///   1 : PrintStart→End 1회 프린트 후 Ready (기본/현행)
    ///   N : 패스 N회, 패스 사이 X축 +swathPitchMm 스텝.
    ///   ※ 헤드(Z)는 첫 패스 전 1회 다운, 마지막 패스 후 1회 업(내린 채 스텝오버).
    ///   ※ 스캔은 Y축(스테이지), 스텝오버는 X축(크로스스캔).
    ///
    /// 프린팅 방향(bidirectional):
    ///   양방향(true) : 패스마다 방향 교대. 복귀 없이 왕복하며 인쇄.
    ///   단방향(false): 매 패스 Start→End 인쇄 후 End→Start 복귀(비인쇄).
    /// </remarks>
    public static class AutoPrintSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, int swathCount = 1, double swathPitchMm = 0,
            bool bidirectional = true)
            => Build(machine, motion, new PrintRunOptions
            {
                SwathCount    = swathCount,
                SwathPitchMm  = swathPitchMm,
                Bidirectional = bidirectional,
            });

        /// <param name="opts">인쇄 명령 상대까지 담은 전체 설정. <c>Job</c> 이 null 이면 Dry Run.</param>
        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, PrintRunOptions opts)
            => PrintCycleSequence.Build(machine, motion, opts, useGlassAlign: true);
    }
}
