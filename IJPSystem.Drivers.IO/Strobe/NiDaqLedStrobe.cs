namespace IJPSystem.Drivers.IO.Strobe
{
    /// <summary>
    /// NI-DAQ(NI-DAQmx) 기반 LED 스트로브 트리거.
    /// LabVIEW "DAQ Tesks.vi" + "20_DAQ LED Trigger/Pules Reducer.vi" 대응.
    /// 카운터 출력으로 지연/폭을 가진 펄스를 생성한다.
    /// ※ NationalInstruments.DAQmx (NI-DAQmx .NET) 필요. 아래는 골격(스켈레톤).
    /// </summary>
    public sealed class NiDaqLedStrobe : ILedStrobe
    {
        private readonly string _counterChannel; // 예: "Dev1/ctr0"
        private double _delayUs = 20, _widthUs = 5;
        private bool _open;

        public NiDaqLedStrobe(string counterChannel = "Dev1/ctr0")
            => _counterChannel = counterChannel;

        public void Open()
        {
            // TODO: new Task(); COPulseChannelTime 생성, 지연/폭 설정
            //   task.COChannels.CreatePulseChannelTime(_counterChannel, ... , _delayUs*1e-6, _widthUs*1e-6);
            _open = true;
        }

        public void Enable(bool on)
        {
            // TODO: on 이면 task.Start(), off 면 task.Stop()
            _ = _open;
        }

        public void SetDelayMicroseconds(double us) { _delayUs = us; /* TODO: 채널 지연 갱신 */ }
        public void SetWidthMicroseconds(double us) { _widthUs = us; /* TODO: 채널 폭 갱신 */ }

        public void Fire()
        {
            // TODO: 1펄스 발생 (finite 1 sample)
        }

        public void Close() { _open = false; /* TODO: task.Dispose() */ }
        public void Dispose() => Close();
    }
}
