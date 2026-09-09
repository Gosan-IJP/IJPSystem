using System;
using IJPSystem.Platform.Domain.Models.Printing;   // IPrintJobCommands
using Ttp.Meteor;   // PrinterInterfaceCLS, CtrlCmdIds, eJOBTYPE, eRES, eSCANDIR, eRET

namespace IJPSystem.Platform.Infrastructure.Print.Meteor
{
    /// <summary>
    /// Meteor 인쇄 명령 — <c>PiSendCommand</c> 로 DWORD 배열을 보낸다.
    ///
    /// <para><b>스캔 프린터 시퀀스</b> (SDK 매뉴얼 §10.11.6):
    /// <code>
    ///   PiSetHome()                              ; X 절대 카운터 0 — 이후 좌표의 기준
    ///   PCMD_STARTJOB, JT_SCAN
    ///   패스마다:  PCMD_STARTSCAN(방향) → PCMD_IMAGE_BUFFER → PCMD_ENDDOC
    ///   PCMD_ENDJOB
    /// </code>
    /// 스캔 프린터는 <c>PCMD_STARTFDOC</c>/<c>STARTPDOC</c> 을 쓰지 않는다 —
    /// <c>PCMD_STARTSCAN</c> 이 그 자리이고, <b>스와스 하나가 문서 하나</b>다.</para>
    ///
    /// <para><b>트리거는 자동이다.</b> 매뉴얼: "The print trigger can also be generated
    /// automatically at the start of a print swathe based on the encoder position and direction.
    /// This mode is used in scanning printers." 그래서 PD 센서가 없어도 되고, 명령을 큐에 넣어
    /// 둔 뒤 스캔축을 움직이면 엔코더가 도는 순간 발사된다 — <b>명령이 이동보다 먼저</b>다.</para>
    ///
    /// <para><b>프린터를 열지 않는다.</b> 세션은 MeteorStatusMonitor 가 소유한다(한 프로세스만
    /// 소유 가능). 여기서 열고 닫으면 서로 뺏는다.</para>
    /// </summary>
    public sealed class MeteorPrintJob : IPrintJobCommands
    {
        private readonly Action<string>? _log;
        private readonly eRES _resolution;
        private readonly object _io = new();

        /// <summary>
        /// 기본 해상도(RES_HIGH)로 만든다.
        ///
        /// <para>★Meteor 타입을 <b>생성자에 노출하지 않는다</b> — 노출하면 부르는 쪽(HMI)이
        /// MeteorCLS 를 참조해야 한다. 헤드 SDK 는 Infrastructure 안에만 있어야 하고,
        /// 화면은 <c>IPrintJobCommands</c> 만 알면 된다.</para>
        /// </summary>
        public MeteorPrintJob(Action<string>? log = null) : this(log, eRES.RES_HIGH) { }

        /// <param name="resolution">
        /// cfg 의 <c>[Encoder] Res1/Res2/Res3</c> 이 각각 RES_HIGH/MED/LOW 의 추가 분주다.
        /// S800 cfg 는 Res1=1 이므로 RES_HIGH 가 600dpi 원해상도다.
        /// </param>
        public MeteorPrintJob(Action<string>? log, eRES resolution)
        {
            _log = log;
            _resolution = resolution;
        }

        public string Name => "Meteor 스캔 인쇄";

        public void StartJob(int jobId)
        {
            lock (_io)
            {
                // 캐리지가 인쇄 원점에 있는 상태에서 부른다 — 여기가 X 좌표 0 이 된다.
                Check(PrinterInterfaceCLS.PiSetHome(), "PiSetHome");

                // Cmd[1]=4 는 뒤따르는 DWORD 수(Job ID·Type·Res·문서폭).
                // 문서폭은 스캔 인쇄에서 무시되므로 0.
                Send("PCMD_STARTJOB", new[]
                {
                    (int)CtrlCmdIds.PCMD_STARTJOB, 4,
                    jobId, (int)eJOBTYPE.JT_SCAN, (int)_resolution, 0,
                });
                _log?.Invoke($"인쇄 작업 시작 — Job {jobId}, JT_SCAN, {_resolution}");
            }
        }

        public void StartSwath(bool forward)
        {
            lock (_io)
            {
                // Cmd[2] Bit0 = 방향. Bits31:16 은 스캔 오프셋(1/100 print clock)이라 0.
                var dir = forward ? eSCANDIR.SD_FWD : eSCANDIR.SD_REV;
                Send("PCMD_STARTSCAN", new[] { (int)CtrlCmdIds.PCMD_STARTSCAN, 1, (int)dir });
                _log?.Invoke($"스와스 시작 — {(forward ? "정방향" : "역방향")}");
            }
        }

        public void SendImage(uint bufferId, int plane, int xLeftPx, int yTopPx, int widthPx)
        {
            lock (_io)
            {
                // Cmd[1]=5 는 뒤따르는 DWORD 수(Plane·XLeft·YTop·Width·버퍼ID).
                // ※ Plane 최상위 비트를 세우면 인쇄 후 버퍼가 해제된다. 여기서는 세우지 않는다 —
                //   같은 데이터로 여러 스와스를 찍고, 반납은 화면이 [언로드] 할 때 한다.
                Send("PCMD_IMAGE_BUFFER", new[]
                {
                    (int)CtrlCmdIds.PCMD_IMAGE_BUFFER, 5,
                    plane, xLeftPx, yTopPx, widthPx, unchecked((int)bufferId),
                });
                _log?.Invoke($"이미지 지정 — 버퍼 #{bufferId}, plane {plane}, ({xLeftPx},{yTopPx}) 폭 {widthPx}");
            }
        }

        public void EndSwath()
        {
            lock (_io) Send("PCMD_ENDDOC", new[] { (int)CtrlCmdIds.PCMD_ENDDOC, 0 });
        }

        public void EndJob()
        {
            lock (_io)
            {
                Send("PCMD_ENDJOB", new[] { (int)CtrlCmdIds.PCMD_ENDJOB, 0 });
                _log?.Invoke("인쇄 작업 끝");
            }
        }

        private static void Send(string name, int[] cmd)
        {
            var r = PrinterInterfaceCLS.PiSendCommand(cmd);
            if (r != eRET.RVAL_OK)
                throw new InvalidOperationException(
                    $"Meteor {name} 실패: {r}" +
                    (r == eRET.RVAL_FULL ? " — 명령 큐가 찼습니다. 앞 작업이 끝나기를 기다려야 합니다." : ""));
        }

        private static void Check(eRET r, string call)
        {
            if (r != eRET.RVAL_OK) throw new InvalidOperationException($"Meteor {call} 실패: {r}");
        }
    }
}
