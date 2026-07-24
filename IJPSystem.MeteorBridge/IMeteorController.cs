namespace IJPSystem.MeteorBridge
{
    /// <summary>
    /// 브리지가 구동하는 실제 헤드 컨트롤러. 지금은 <see cref="MockMeteorController"/>(가상)만 있고,
    /// 실장 시 PrinterInterfaceCLS 를 감싼 구현으로 교체한다(파이프/서버 코드는 그대로 재사용).
    ///
    /// 각 메서드는 Meteor Pi* API 와 1:1 대응하도록 설계했다:
    ///   Open   → PiOpenPrinter(cfg)         Close → PiClosePrinter
    ///   Power  → PiSetHeadPower             Spit  → PiSetParam(CPEX_*) + PiSendCommand(패턴)
    ///   Abort  → PiAbort                    IsBusy→ PiIsBusy
    /// </summary>
    public interface IMeteorController
    {
        /// <summary>설정 파일(.cfg)로 프린터 연결. 성공 시 (true, null).</summary>
        (bool ok, string? err) Open(string cfgPath);

        /// <summary>헤드 구동 전압 인가.</summary>
        (bool ok, string? err) SetPower(int volts);

        /// <summary>선택 노즐을 주파수 F로 연속 토출 시작.</summary>
        (bool ok, string? err) Spit(int[] nozzles, double freqHz, int greyLevel, int tickleLevel);

        /// <summary>토출 중단(1회 명령). 결과는 MeteorBridgeProtocol.Abort* 문자열(Ok/Busy/Failed).</summary>
        string Abort();

        /// <summary>컨트롤러가 명령 처리 중인지(= 아직 구동 중).</summary>
        bool IsBusy();

        /// <summary>프린터 닫기.</summary>
        void Close();
    }
}
