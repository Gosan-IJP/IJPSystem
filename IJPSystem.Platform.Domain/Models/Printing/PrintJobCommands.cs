namespace IJPSystem.Platform.Domain.Models.Printing
{
    /// <summary>
    /// 인쇄 명령을 내는 쪽. <b>스와스 한 번이 문서 하나</b>다(스캔 프린터 규약).
    ///
    /// <para><b>왜 인터페이스인가</b>: 명령 워드 배치는 헤드 SDK 의 일이고, 시퀀스가 알아야 하는
    /// 것은 <b>순서</b>뿐이다. 갈라 두면 헤드 없이도 시퀀스의 순서를 확인할 수 있다
    /// (<see cref="IHeadVoltage"/> 와 같은 자리·같은 이유).</para>
    ///
    /// <para><b>구현이 없으면 단계를 만들지 않는다.</b> Dry Run 이거나 헤드가 없으면 시퀀스가
    /// 인쇄 단계를 아예 내지 않는다 — 단계를 내 놓고 안에서 건너뛰면 화면에는 인쇄하는 것처럼
    /// 보이는 목록이 남는다. 목록은 실제로 도는 것만 보여야 한다(GlassAlign 과 같은 원칙).</para>
    /// </summary>
    public interface IPrintJobCommands
    {
        /// <summary>사람이 읽을 이름 — 화면에 "무엇으로 찍었는가" 를 남긴다.</summary>
        string Name { get; }

        /// <summary>
        /// 인쇄 작업 시작. <b>X 절대 좌표의 기준점도 여기서 잡는다</b> —
        /// 캐리지가 인쇄 원점에 있는 상태에서 불러야 한다.
        /// </summary>
        void StartJob(int jobId);

        /// <summary>스와스(=문서) 시작. 방향이 정·역을 정한다.</summary>
        void StartSwath(bool forward);

        /// <summary>이 스와스에 찍을 이미지 — 이미 올라가 있는 버퍼를 가리킨다.</summary>
        void SendImage(uint bufferId, int plane, int xLeftPx, int yTopPx, int widthPx);

        /// <summary>스와스 끝.</summary>
        void EndSwath();

        /// <summary>인쇄 작업 끝.</summary>
        void EndJob();
    }
}
