using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Auto Print — 자동 인쇄 시퀀스 (멀티 스와스 지원)</summary>
    /// <remarks>
    /// SequenceStepDef.Name 은 표시용 **번역 키** (Step_AutoPrint_*).
    /// HMI 의 ViewModel 이 Loc.T(key) 로 사용자 언어로 변환해서 화면에 표시.
    /// 키→번역은 Common/Resources/Languages/ko-KR.xaml, en-US.xaml 참조.
    ///
    /// 프린팅수(swathCount) — 기타정보 화면 설정:
    ///   1 : PrintStart→End 1회 프린트 후 Ready (기본/현행)
    ///   2 : Start→End 프린트 → X축 +headLength 스텝오버 → End→Start 프린트 → Ready
    ///   3 : Start→End → 스텝 → End→Start → 스텝 → Start→End → Ready
    ///   N : 패스 N회(방향 교대: 홀수 Start→End / 짝수 End→Start), 패스 사이 X축 +headLength 스텝.
    ///   ※ 헤드(Z)는 첫 패스 전 1회 다운, 마지막 패스 후 1회 업(내린 채 스텝오버).
    ///   ※ 스캔은 Y축(스테이지), 스텝오버는 X축(크로스스캔). 스캔은 단일 축 이동으로 X 유지.
    /// </remarks>
    public static class AutoPrintSequence
    {
        private const string ScanAxisNo = "Y";   // 프린트 스캔 축(스테이지 이송)
        private const string StepAxisNo = "X";   // 스와스 스텝오버 축(크로스스캔)

        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine, IMotionService motion, int swathCount = 1, double headLength = 0)
        {
            if (swathCount < 1) swathCount = 1;

            var steps = new List<SequenceStepDef>();
            int n = 0;

            // IO.json에 글라스 전용 센서가 없으므로 수동 로드 안착 대기. 글라스 클램프 여부는 이후 척 진공 압력으로 확인.
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_WaitGlass",
                ct => Task.Delay(1_500, ct)));

            // 진공 ON — VacuumConfirm(압력스위치 확인) 단계는 제외됨(사용자 요청). 안정화 대기로 이어감.
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumOn",
                ct => { machine.VacuumOn(); return Task.CompletedTask; }));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumStabilize",
                ct => Task.Delay(1_000, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveStart",
                ct => motion.MoveToPointAsync(PointNames.PrintStart, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveStartDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));

            // ── 프린트 헤드 DOWN (글래스 가까이) — 멀티 스와스여도 1회만 ──
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadDown",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadDown, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadDownDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)));

            // ── 스와스 패스 루프 ──
            // 방향 교대: 홀수=Start→End(PrintEnd), 짝수=End→Start(PrintStart).
            // 스캔은 Y축 단일 이동(스텝오버로 옮긴 X 위치 유지). 패스 사이 X축 +headLength 스텝.
            for (int pass = 1; pass <= swathCount; pass++)
            {
                bool forward = (pass % 2 == 1);
                string scanTarget = forward ? PointNames.PrintEnd : PointNames.PrintStart;

                steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_Print",
                    ct => motion.MoveAxisToPointAsync(ScanAxisNo, scanTarget, ct, MotionProfileKind.Printing)));

                steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_PrintDone",
                    ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)));

                // 마지막 패스가 아니면 X축을 헤드길이만큼 +방향 스텝오버(헤드는 내린 채).
                if (pass < swathCount && headLength > 0)
                {
                    steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_SwathStep",
                        ct => motion.MoveAxisRelativeAsync(StepAxisNo, headLength, ct)));

                    steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_SwathStepDone",
                        ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));
                }
            }

            // ── 프린트 헤드 UP (글래스에서 떼기) ──
            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadUp",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadUp, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_HeadUpDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_VacuumOff",
                ct =>
                {
                    machine.VacuumOff();
                    // Virtual 모드에서 압력스위치 OFF 를 시뮬레이션(실장 드라이버는 no-op → 물리 신호가 자연 발생).
                    // 제거하면 Virtual 모드에서 이 단계가 타임아웃 실패한다.
                    machine.IO.ScheduleInput("DI_PRESS_SW_CHUCK_VAC", false, 200);
                    return WaitHelper.ForIOSignal(machine.IO, "DI_PRESS_SW_CHUCK_VAC",
                                                 expected: false, timeoutMs: 10_000, ct);
                }));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveReady",
                ct => motion.MoveToPointAsync(PointNames.Ready, ct)));

            steps.Add(new SequenceStepDef(++n, "Step_AutoPrint_MoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)));

            return steps;
        }
    }
}
