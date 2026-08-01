namespace IJPSystem.Drivers.IO.Comizoa
{
    /// <summary>
    /// LabVIEW "Comi_Motion_lib/3_IO_Con" (Comizoa EtherCAT DIO) 대응.
    /// Get DIN State / Get DOUT State / Set DOUT 를 추상화한 저수준(채널 기반) 인터페이스.
    /// 모션과 동일한 EtherCAT 네트워크의 DIO 슬레이브를 제어한다.
    /// </summary>
    public interface IDigitalIo
    {
        /// <summary>입력 1채널 읽기. (Get DIN State.vi)</summary>
        bool GetInput(int channel);
        /// <summary>출력 1채널 현재값 읽기. (Get DOUT State.vi)</summary>
        bool GetOutput(int channel);
        /// <summary>출력 1채널 설정. (Set DOUT.vi) — 밸브/스트링거 등.</summary>
        void SetOutput(int channel, bool on);

        /// <summary>입력 전체를 비트마스크로 읽기.</summary>
        uint GetInputBits();
        /// <summary>입력 전체 비트마스크 + SDK errCode(진단용).</summary>
        uint GetInputBits(out int errCode);
        /// <summary>iniChannel 부터 32채널 블록을 읽기(진단용 — 채널 오프셋 탐색).</summary>
        uint GetInputBits(uint iniChannel, out int errCode);
        /// <summary>출력 전체를 비트마스크로 쓰기.</summary>
        void SetOutputBits(uint bits);

        /// <summary>
        /// 연결 프로브: 무해한 읽기로 마스터/네트워크가 실제로 유효한지 확인한다.
        /// errCode==0 이면 정상, 아니면 SDK 오류코드(예: -20 INVALID_NETID = 마스터 미로드).
        /// ※ Get 계열은 예외를 안 던지므로, 이 메서드로 errCode 를 봐야 "거짓 연결"을 걸러낸다.
        /// </summary>
        bool Probe(out int errCode);
    }
}
