using System;
using System.Collections.Generic;
using IJPSystem.Platform.Domain.Models.Printing;   // IPrintJobCommands

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 헤드 없이 인쇄 명령을 <b>남기기만</b> 하는 구현 — DriverMode.Head=Virtual 용.
    ///
    /// <para>
    /// <b>왜 만드는가</b>: 이것이 없으면 인쇄 경로를 시험할 수 있는 자리가 <b>실기 + 잉크</b>
    /// 뿐이다. 그런데 여기서 제일 틀리기 쉬운 것은 <b>순서</b>다 — 스캔 프린터는 엔코더로
    /// 트리거가 자동 생성되므로 명령이 이동보다 먼저 큐에 들어가야 하고, 순서가 뒤집히면
    /// 이미 지나간 자리에 찍으라는 말이 된다. 하드웨어 없이 확인할 수 있는 것을 확인할 데가
    /// 가장 위험한 곳뿐이어서는 안 된다.
    /// </para>
    /// <para>
    /// <b>명령 배치를 그대로 흉내 낸다.</b> 로그에 실제 DWORD 배열을 적는 이유는, 화면에
    /// "인쇄했다" 는 글자만 남기면 정작 순서·인자를 못 보기 때문이다.
    /// <see cref="MeteorPrintJob"/> 와 같은 값을 적어야 대조가 된다.
    /// </para>
    /// <para>
    /// <b>가상은 가상이라고 말한다.</b> <see cref="Name"/> 에 [가상] 이 들어가고 화면이 이를
    /// 그대로 쓴다 — 실기에서 Head 를 Virtual 로 잘못 둔 채 "찍은 줄 알았는데 안 찍힘" 이
    /// 나는 것을 막는 것은 이 표기뿐이다.
    /// </para>
    /// </summary>
    public sealed class VirtualPrintJob : IPrintJobCommands
    {
        private readonly Action<string>? _log;
        private readonly object _io = new();
        private readonly List<string> _commands = new();

        public VirtualPrintJob(Action<string>? log = null) => _log = log;

        public string Name => "[가상] 인쇄 명령만 기록";

        /// <summary>보낸 척한 명령들 — 시험에서 <b>순서</b>를 확인하는 데 쓴다.</summary>
        public IReadOnlyList<string> Commands
        {
            get { lock (_io) return _commands.ToArray(); }
        }

        public void StartJob(int jobId)
        {
            // 실물은 여기서 PiSetHome 으로 X 기준점을 잡는다. 가상도 같은 자리에 남겨야
            // "기준점을 언제 잡는가" 가 순서로 드러난다.
            Record("PiSetHome", Array.Empty<int>());
            Record("PCMD_STARTJOB", new[] { 4, jobId, /*JT_SCAN*/ 4, /*RES_HIGH*/ 1, 0 });
            _log?.Invoke($"[가상] 인쇄 작업 시작 — Job {jobId}, JT_SCAN");
        }

        public void StartSwath(bool forward)
        {
            Record("PCMD_STARTSCAN", new[] { 1, forward ? 0 : 1 });
            _log?.Invoke($"[가상] 스와스 시작 — {(forward ? "정방향" : "역방향")}");
        }

        public void SendImage(uint bufferId, int plane, int xLeftPx, int yTopPx, int widthPx)
        {
            Record("PCMD_IMAGE_BUFFER",
                   new[] { 5, plane, xLeftPx, yTopPx, widthPx, unchecked((int)bufferId) });
            _log?.Invoke($"[가상] 이미지 지정 — 버퍼 #{bufferId}, plane {plane}, " +
                         $"({xLeftPx},{yTopPx}) 폭 {widthPx}");
        }

        public void EndSwath() => Record("PCMD_ENDDOC", new[] { 0 });

        public void EndJob()
        {
            Record("PCMD_ENDJOB", new[] { 0 });
            _log?.Invoke("[가상] 인쇄 작업 끝");
        }

        private void Record(string name, int[] rest)
        {
            lock (_io) _commands.Add(rest.Length == 0 ? name : $"{name} [{string.Join(", ", rest)}]");
        }
    }

    /// <summary>
    /// 헤드 없이 <b>적재까지만</b> 흉내 내는 전송기 — DriverMode.Head=Virtual 용.
    ///
    /// <para>
    /// <see cref="NullPrintDataDownloader"/> 와 갈라 둔 이유가 있다. 그쪽은 버퍼 번호를 내주지
    /// <b>않아</b> 인쇄 명령이 아예 못 나가고, 그것이 맞다 — 헤드가 없는(None) 구성에서
    /// READY 로 보이면 안 된다. 반면 Virtual 은 <b>일부러 고른</b> 구성이라 인쇄 순서를 끝까지
    /// 돌려 봐야 한다. 그래서 번호를 내주되, 엔진 번호와 <b>절대 헷갈리지 않는 값</b>을 쓴다
    /// (엔진은 1·2… 같은 작은 수를 준다).
    /// </para>
    /// <para>
    /// ★전송기와 명령 상대는 <b>같은 세상</b>이어야 한다. 한쪽만 가상이면 가짜 버퍼 번호가
    /// 실제 엔진 명령에 실린다 — 부르는 쪽에서 둘을 한 번에 고르게 되어 있다.
    /// </para>
    /// </summary>
    public sealed class VirtualPrintDataDownloader : IPrintDataDownloader
    {
        /// <summary>가상 버퍼 번호(첫 장). 로그에 이 값이 보이면 실물이 아니라는 뜻이다.</summary>
        public const uint VirtualBufferId = 0xFA0E_0001;

        private readonly Action<string>? _log;

        public VirtualPrintDataDownloader(Action<string>? log = null) => _log = log;

        public string Name => "[가상] 엔진 없이 적재";
        public bool IsReady => true;
        public string? NotReadyReason => null;

        /// <summary>마지막으로 받은 것 — 화면·검사에서 무엇이 넘어왔는지 확인한다.</summary>
        public PrintJob? Last { get; private set; }

        /// <summary>
        /// 장 수만큼 번호를 내준다 — 실물과 <b>같은 개수</b>여야 인쇄가 같은 순서로 돈다.
        /// 하나만 주면 스와스가 여럿일 때 가상에서만 다른 경로를 타 시험이 헛돈다.
        /// </summary>
        public IReadOnlyList<uint> BufferIds => _bufferIds;
        private uint[] _bufferIds = Array.Empty<uint>();

        public string? LastTransferDetail => Last == null
            ? null
            : $"가상 버퍼 {_bufferIds.Length}장 · {Last.Steps:N0}스텝 × {Last.Nozzles}노즐";

        public void Download(PrintJob job)
        {
            Last = job ?? throw new ArgumentNullException(nameof(job));

            int count = Math.Max(1, job.ImageCount);
            _bufferIds = new uint[count];
            for (int i = 0; i < count; i++) _bufferIds[i] = VirtualBufferId + (uint)i;

            _log?.Invoke($"[가상] 적재 완료 — {count}장(스와스 {job.SwathCount} × 패스 {job.PassCount}), " +
                         $"{job.Steps:N0}스텝 × {job.Nozzles}노즐 " +
                         "(엔진으로 나가지 않았습니다. 순서 확인용입니다)");
        }

        public void Release()
        {
            Last = null;
            _bufferIds = Array.Empty<uint>();
        }
    }
}
