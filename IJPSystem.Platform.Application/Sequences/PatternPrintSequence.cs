using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Application.Sequences
{
    /// <summary>Pattern Print — Meteor PCC 기반 패턴 인쇄 시퀀스.</summary>
    /// <remarks>
    /// 워크플로우(준비 → 설정 → 이미지 다운로드 → 인쇄 실행 → 모니터링/종료)를 단계화한 것.
    ///
    /// ※ Meteor PCC 제어(OpenPrinter / GetPccStatus / SetParam / ReSendPCCConfiguration /
    ///   SetHeadPower / DownloadIMG_ScanMODE / SetSignal / isBusy / ClosePrinter 등)는
    ///   아직 전용 프린터 드라이버(IPrinterController 등)가 없으므로 **placeholder(Task.Delay)** 로 둔다.
    ///   드라이버 연결 시 각 단계의 주석에 표기된 PCC 함수 호출로 교체할 것.
    ///   모션 이동/복귀와 IO(Set DOUT)는 기존 추상화(IMotionService / machine.IO)를 사용한다.
    ///
    /// 사전조건: Print Image Design(Rasterizer)로 패턴/BMP 생성, Set Print Origin(21)으로 인쇄 원점 설정.
    /// 롤투롤(R2R)이면 9단계 DownloadIMG_ScanMODE 를 DownloadIMG_R2R_seamlessMODE 로 대체.
    /// </remarks>
    public static class PatternPrintSequence
    {
        public static IReadOnlyList<SequenceStepDef> Build(IMachine machine, IMotionService motion) => new[]
        {
            // ── [준비] ──────────────────────────────────────────────
            // 3) OpenPrinter — Meteor PCC 연결
            new SequenceStepDef(1, "Step_PatternPrint_OpenPrinter",
                ct => Task.Delay(500, ct)),

            // 4) GetPccStatus / GetHeadStatus — Ready 확인
            new SequenceStepDef(2, "Step_PatternPrint_CheckStatus",
                ct => Task.Delay(500, ct)),

            // ── [설정] ──────────────────────────────────────────────
            // 5) SetParam / SetParamEx — 해상도·속도·엔코더/PD 설정
            new SequenceStepDef(3, "Step_PatternPrint_SetParam",
                ct => Task.Delay(500, ct)),

            // 6) ReSendPCCConfiguration — 헤드 구성·파형(WF) 전송
            new SequenceStepDef(4, "Step_PatternPrint_SendConfig",
                ct => Task.Delay(800, ct)),

            // 7) SetHeadPower ON — 헤드 전원 ON
            new SequenceStepDef(5, "Step_PatternPrint_HeadPowerOn",
                ct => Task.Delay(500, ct)),

            // 8) SetHome / SetXHeadOffset / UpdateXoffsets — 헤드 정렬·X오프셋
            new SequenceStepDef(6, "Step_PatternPrint_AlignHead",
                ct => Task.Delay(500, ct)),

            // ── [이미지 다운로드] ───────────────────────────────────
            // 9) DownloadIMG_ScanMODE — 래스터 이미지를 PCC 버퍼로 전송
            //    (롤투롤이면 DownloadIMG_R2R_seamlessMODE)
            new SequenceStepDef(7, "Step_PatternPrint_DownloadImage",
                ct => Task.Delay(1_500, ct)),

            // ── [인쇄 실행] ─────────────────────────────────────────
            // 10) Move_ABS — 스테이지를 인쇄 시작 위치로 이동
            new SequenceStepDef(8, "Step_PatternPrint_MoveStart",
                ct => motion.MoveToPointAsync(PointNames.PrintStart, ct)),

            new SequenceStepDef(9, "Step_PatternPrint_MoveStartDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),

            // 11) SetSignal — 인쇄 트리거/PrintGo 신호 ARM
            new SequenceStepDef(10, "Step_PatternPrint_ArmTrigger",
                ct => Task.Delay(300, ct)),

            // 12) (스캔 주행) 엔코더 신호에 맞춰 헤드 토출. 인쇄 구간은 Printing 프로파일.
            //     인쇄 중 밸브/IO 제어가 필요하면 이 구간에서 machine.IO.SetOutput(...) (Set DOUT).
            new SequenceStepDef(11, "Step_PatternPrint_ScanPrint",
                ct => motion.MoveToPointAsync(PointNames.PrintEnd, ct, MotionProfileKind.Printing)),

            new SequenceStepDef(12, "Step_PatternPrint_ScanPrintDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 60_000, ct)),

            // ── [모니터링/종료] ─────────────────────────────────────
            // 13) isBusy / GetPrnStatus 폴링 — 인쇄 완료 대기
            new SequenceStepDef(13, "Step_PatternPrint_WaitPrintDone",
                ct => Task.Delay(1_000, ct)),

            // 14) SetHeadPower OFF
            new SequenceStepDef(14, "Step_PatternPrint_HeadPowerOff",
                ct => Task.Delay(500, ct)),

            // 14) ClosePrinter — Meteor PCC 연결 종료
            new SequenceStepDef(15, "Step_PatternPrint_ClosePrinter",
                ct => Task.Delay(500, ct)),

            // 대기 위치(READY) 복귀
            new SequenceStepDef(16, "Step_PatternPrint_MoveReady",
                ct => motion.MoveToPointAsync(PointNames.Ready, ct)),

            new SequenceStepDef(17, "Step_PatternPrint_MoveReadyDone",
                ct => WaitHelper.ForAllMotionDone(machine.Motion, timeoutMs: 20_000, ct)),
        };
    }
}
