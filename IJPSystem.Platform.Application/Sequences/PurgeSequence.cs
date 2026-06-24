using IJPSystem.Platform.Domain.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    public static class PurgeSequence
    {
        private const int PressureStabilizeMs = 1_500;  // 압력 안정화 대기(타이밍 마커)

        // purgePressureKpa/meniscusPressureKpa: IO.json(ETS-D08MN)에 아날로그 압력 출력이 없어 현재 미사용(시그니처 보존 — 추후 아날로그 채널 추가 시 재사용).
        public static IReadOnlyList<SequenceStepDef> Build(
            IMachine machine,
            IMotionService motion,
            double purgePressureKpa    = 50.0,
            double meniscusPressureKpa = -3.5)
            => new[]
        {
            new SequenceStepDef(1, "Step_Purge_VacuumOff",
                ct =>
                {
                    machine.VacuumOff();
                    machine.IO.ScheduleInput("DI_PRESS_SW_CHUCK_VAC", false, 500);
                    return WaitHelper.ForIOSignal(machine.IO, "DI_PRESS_SW_CHUCK_VAC",
                                                 expected: false, timeoutMs: 10_000, ct);
                }),

            new SequenceStepDef(2, "Step_Purge_MovePurge",
                ct => motion.MoveToPointAsync(PointNames.Purge, ct)),

            new SequenceStepDef(3, "Step_Purge_MovePurgeDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),

            // ── 신규: 프린트 헤드 DOWN (퍼지 위치 가까이) ──
            new SequenceStepDef(4, "Step_Purge_HeadDown",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadDown, ct)),

            new SequenceStepDef(5, "Step_Purge_HeadDownDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)),

            // ── 헤드 밸브 OPEN (IO.json: DO_HEAD1_LEFT/RIGHT_VALVE) ──
            new SequenceStepDef(6, "Step_Purge_ValveOpen",
                ct =>
                {
                    machine.IO.SetOutput("DO_HEAD1_LEFT_VALVE",  true);
                    machine.IO.SetOutput("DO_HEAD1_RIGHT_VALVE", true);
                    return Task.Delay(300, ct);   // 밸브 응답 시간
                }),

            // ── 양압 인가 단계 — IO.json에 아날로그 압력 출력이 없어 SV 인가 생략(토출 시작 타이밍 마커 유지) ──
            new SequenceStepDef(7, "Step_Purge_PressurizeOn",
                ct => Task.Delay(PressureStabilizeMs, ct)),

            new SequenceStepDef(8, "Step_Purge_WaitDispense",
                ct => Task.Delay(3_000, ct)),   // 토출 시간(전용 토출 감지 센서 없음)

            // ── 양압 OFF + 음압(Meniscus) 단계 — 아날로그 출력 없음, 타이밍 마커 유지 ──
            new SequenceStepDef(9, "Step_Purge_PressurizeOff",
                ct => Task.Delay(PressureStabilizeMs, ct)),

            // ── 헤드 밸브 CLOSE ──
            new SequenceStepDef(10, "Step_Purge_ValveClose",
                ct =>
                {
                    machine.IO.SetOutput("DO_HEAD1_LEFT_VALVE",  false);
                    machine.IO.SetOutput("DO_HEAD1_RIGHT_VALVE", false);
                    return Task.Delay(300, ct);
                }),

            // ── 신규: 프린트 헤드 UP (퍼지 완료 후 떼기) ──
            new SequenceStepDef(11, "Step_Purge_HeadUp",
                ct => motion.MoveToPointAsync(PointNames.PrintHeadUp, ct)),

            new SequenceStepDef(12, "Step_Purge_HeadUpDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 10_000, ct)),

            new SequenceStepDef(13, "Step_Purge_MoveReady",
                ct => motion.MoveToPointAsync(PointNames.Ready, ct)),

            new SequenceStepDef(14, "Step_Purge_MoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),
        };
    }
}
