using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>인쇄 준비 상태. (랩뷰 6_WIZ_Print 의 상태머신 대응)</summary>
    public enum PrintReadyState
    {
        /// <summary>아무것도 안 올라가 있다.</summary>
        Idle,

        /// <summary>파일을 읽는 중.</summary>
        Loading,

        /// <summary>PCC 로 올리는 중.</summary>
        Downloading,

        /// <summary>데이터가 PCC 안에 있다 — 이제 트리거만 주면 찍는다.</summary>
        ReadyToPrint,

        /// <summary>발사 중.</summary>
        Printing,

        /// <summary>읽기·전송에서 막혔다. 무엇 때문인지는 <see cref="PrintJobController.Message"/>.</summary>
        Fault,
    }

    /// <summary>
    /// 인쇄 데이터를 PCC 메모리로 올리는 쪽.
    ///
    /// <para>
    /// <b>이 자리가 Meteor 다.</b> PCC 는 PC 의 파일을 못 읽는다 — PC 가 읽어서(<see cref="PrintJobFile"/>)
    /// 여기로 넘기면, 구현체가 <c>PiAllocateImageBufferEx</c> → <c>PiFillImageBuffer</c> →
    /// <c>PCMD_IMAGE_BUFFER</c> 로 올린다.
    /// </para>
    /// <para>
    /// 인터페이스로 갈라 둔 이유: 읽기·검증·상태 전이는 장비 없이 전부 확인할 수 있어야 한다.
    /// 실물이 없을 때는 <see cref="NullPrintDataDownloader"/> 가 들어간다.
    /// </para>
    /// </summary>
    public interface IPrintDataDownloader
    {
        /// <summary>사람이 읽을 이름. 화면에 "무엇으로 보냈는가"를 남기려고 둔다.</summary>
        string Name { get; }

        /// <summary>지금 보낼 수 있는가(헤드 준비·연결).</summary>
        bool IsReady { get; }

        /// <summary>패턴을 PCC 메모리로 올린다. 끝나면 트리거만 주면 찍힌다.</summary>
        void Download(PrintJob job);

        /// <summary>올려 둔 데이터를 버린다. PrintEngine 메모리는 앱이 직접 반납해야 한다.</summary>
        void Release();
    }

    /// <summary>
    /// 보내는 척만 한다. 헤드가 없는 자리(사무실·테스트)에서 화면 흐름을 그대로 밟아 보려고 둔다.
    ///
    /// <para><b>실물처럼 보이면 안 된다</b> — 이름에 [가상] 을 달아 화면에 그대로 뜨게 한다.
    /// 준비됐다는 초록 표시만 보고 실제로 올라간 줄 알면 그게 사고다.</para>
    /// </summary>
    public sealed class NullPrintDataDownloader : IPrintDataDownloader
    {
        public string Name => "[가상] 전송 안 함";
        public bool IsReady => true;

        /// <summary>마지막으로 받은 것 — 화면·검사에서 무엇이 넘어왔는지 확인한다.</summary>
        public PrintJob? Last { get; private set; }

        public void Download(PrintJob job) => Last = job ?? throw new ArgumentNullException(nameof(job));
        public void Release() => Last = null;
    }

    /// <summary>
    /// 저장해 둔 인쇄 데이터를 불러 PCC 로 올리고 READY 까지 가는 흐름.
    /// (랩뷰 <c>6_WIZ_Print</c> 의 "Load Print data" → DownloadIMG → "READY TO PRINT")
    ///
    /// <para>
    /// <b>저장과 인쇄는 갈라져 있다.</b> 파일은 저장할 때 만들어지고, 읽는 것은 한참 뒤
    /// [인쇄 데이터 로드] 를 누를 때다. 그래서 저장해 둔 것을 나중에 불러 여러 번 찍을 수 있다.
    /// </para>
    /// <para>
    /// <b>Print 버튼은 파일을 다시 안 읽는다.</b> READY 가 되면 데이터는 이미 PCC 안에 있고,
    /// 인쇄는 엔코더·PD 트리거로 그 데이터를 쏘는 것뿐이다.
    /// </para>
    /// </summary>
    public sealed class PrintJobController
    {
        private readonly IPrintDataDownloader _downloader;

        public PrintJobController(IPrintDataDownloader downloader)
            => _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));

        public PrintReadyState State { get; private set; } = PrintReadyState.Idle;

        /// <summary>지금 올라가 있는 것. READY 가 아니면 믿을 수 없다.</summary>
        public PrintJob? CurrentJob { get; private set; }

        /// <summary>마지막 결과 설명 — 실패했으면 왜인지가 여기 있다.</summary>
        public string Message { get; private set; } = "";

        /// <summary>검증에서 걸린 것들. 비어 있지 않으면 <see cref="State"/> 는 Fault 다.</summary>
        public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

        public event Action<PrintReadyState>? StateChanged;

        /// <summary>전송기 이름 — 화면에 "무엇으로 보냈는가"를 남긴다.</summary>
        public string DownloaderName => _downloader.Name;

        /// <summary>
        /// ① 파일을 읽어 ② 검증하고 ③ PCC 로 올린다. 끝나면 <see cref="PrintReadyState.ReadyToPrint"/>.
        ///
        /// <para>검증에서 하나라도 걸리면 <b>올리지 않는다</b> — PCC 에 올라간 뒤에 틀린 걸 알면
        /// 잉크가 이미 나가 있다.</para>
        /// </summary>
        public PrintJob? LoadAndDownload(string folder, string? bmpFileName = null)
        {
            Problems = Array.Empty<string>();
            CurrentJob = null;
            SetState(PrintReadyState.Loading);

            PrintJob job;
            try
            {
                job = PrintJobFile.Load(folder, bmpFileName);
            }
            catch (Exception ex)
            {
                Fail("인쇄 데이터를 읽지 못했습니다 — " + ex.Message);
                return null;
            }

            var problems = PrintJobFile.Validate(job);
            if (problems.Count > 0)
            {
                Problems = problems;
                Fail("인쇄 데이터가 앞뒤가 맞지 않습니다 — " + problems[0]);
                return null;
            }

            if (!_downloader.IsReady)
            {
                Fail("헤드가 준비되지 않았습니다 — 전원·연결을 확인하세요.");
                return null;
            }

            SetState(PrintReadyState.Downloading);
            try
            {
                _downloader.Download(job);
            }
            catch (Exception ex)
            {
                Fail("PCC 전송에 실패했습니다 — " + ex.Message);
                return null;
            }

            CurrentJob = job;
            Message = $"READY — {job.Steps}스텝 × {job.Nozzles}노즐, 방울 {job.DropCount:N0}개 " +
                      $"({_downloader.Name})";
            SetState(PrintReadyState.ReadyToPrint);
            return job;
        }

        /// <summary>인쇄를 시작할 수 있는 상태인가. 화면 버튼이 이걸 본다.</summary>
        public bool CanPrint => State == PrintReadyState.ReadyToPrint;

        /// <summary>발사 시작 표시. 데이터는 이미 PCC 안에 있어 파일을 다시 읽지 않는다.</summary>
        public void BeginPrint()
        {
            if (!CanPrint)
                throw new InvalidOperationException(
                    $"인쇄할 수 없는 상태입니다({State}) — [인쇄 데이터 로드] 를 먼저 하세요.");

            SetState(PrintReadyState.Printing);
        }

        /// <summary>발사가 끝났다. 데이터는 그대로 PCC 에 남아 다시 찍을 수 있다.</summary>
        public void EndPrint()
        {
            if (State == PrintReadyState.Printing) SetState(PrintReadyState.ReadyToPrint);
        }

        /// <summary>올려 둔 데이터를 버리고 처음으로. PrintEngine 메모리를 반납한다.</summary>
        public void Unload()
        {
            try { _downloader.Release(); } catch { /* 반납 실패로 화면을 막지는 않는다 */ }
            CurrentJob = null;
            Problems = Array.Empty<string>();
            Message = "";
            SetState(PrintReadyState.Idle);
        }

        private void Fail(string message)
        {
            Message = message;
            SetState(PrintReadyState.Fault);
        }

        private void SetState(PrintReadyState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }
    }
}
