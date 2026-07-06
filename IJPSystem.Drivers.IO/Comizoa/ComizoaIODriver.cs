using System;
using System.Collections.Generic;
using System.Linq;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.IO;

namespace IJPSystem.Drivers.IO.Comizoa
{
    // 코미조아(COMIZOA) EtherCAT 디지털 I/O 드라이버 — 모듈: ETS-D08MN.
    //
    // SDK: ComiEcatSdk.dll (COMIZOA EtherCAT SDK) 를 P/Invoke 로 호출.
    //      실장비 HAL(Mizar.Hal.Io.EtherCatDigitalIo)과 동일한 ecat_* 함수 시그니처를 사용한다.
    //      모션(ComizoaMotionDriver, czm_*)과 동일한 EtherCAT 네트워크를 공유하므로,
    //      마스터 초기화(enumerate)는 모션 쪽에서 1회 수행되면 여기서는 생략 가능하다.
    //
    // 채널 매핑(중요):
    //   IO.json 의 Address(예: "X100"/"Y101") → SDK 채널(int) 로 환산해 ecat_* 함수를 호출한다.
    //   SDK 는 DIN/DOUT 을 각각 0-base 채널 인덱스로 접근하므로 아래 규칙을 사용:
    //     · Address 숫자 n 을 slave = n/100, bit = n%100 으로 분해 (X100 → slave1 bit0)
    //     · 채널 = slave * PointsPerModule(8) + bit          (ETS-D08MN = 8점 모듈) 
    //     · DIN(X)/DOUT(Y) 는 서로 다른 채널 공간(ecat_GetDin vs ecat_SetDout)
    //   ※ slave 열거 순서/모듈 점수는 실배선에 따라 다를 수 있으므로 Connect 시 경고를 남긴다.
    //     실장비에서 채널이 어긋나면 PointsPerModule / MapChannel 만 조정하면 된다.
    public class ComizoaIODriver : IIODriver
    {
        private const int PointsPerModule = 8;   // ETS-D08MN = 디지털 8점

        // 저수준 EtherCAT DIO (ComiEcatSdk.dll 래퍼) — 하드웨어 채널 접근을 위임한다.
        private readonly IDigitalIo _dio = new ComiEcatDigitalIo();

        // IO 설정 및 상태 (미연결/개발 시 echo 로 화면·시퀀스 동작 유지)
        private readonly Dictionary<string, IODeviceInfo> _ioMap         = new();
        private readonly Dictionary<string, bool>         _digitalStates = new();
        private readonly Dictionary<string, double>       _analogStates  = new();

        // Index → (출력 여부, SDK 채널). Initialize 단계에서 채운다.
        private readonly Dictionary<string, (bool isOutput, int channel)> _addrMap = new();

        public bool IsConnected { get; private set; }

        // 저수준 SDK(ecat_*) 를 실제로 호출할 수 있는지 여부.
        // false 면 하드웨어 호출을 건너뛰고 echo(가상) 상태로만 동작한다(크래시 방지).
        private bool _hwUsable;
        private bool _degradeLogged;

        public bool Connect()
        {
            // 모션(ComizoaMotionDriver)이 동일 EtherCAT 마스터를 이미 enumerate 했다는 전제.
            // 저수준 SDK 가 실제로 호출 가능한지 1회 프로브 — DLL 미존재/엔트리포인트 불일치 시
            // 예외가 나므로, 그때는 echo 모드로 안전하게 강등한다(무한 에러창 방지).
            try
            {
                _dio.GetInputBits();          // 무해한 읽기로 SDK 가용성 확인
                _hwUsable = true;
            }
            catch (Exception ex)
            {
                _hwUsable = false;
                LoggerService.WriteToFile("WARN",
                    $"[Comizoa IO] SDK 사용 불가 — echo(가상) 모드로 동작합니다: {ex.GetType().Name}: {ex.Message}");
            }
            IsConnected = _hwUsable;
            return true;   // echo 모드라도 앱은 계속 진행(화면/시퀀스 동작 유지)
        }

        /// <summary>런타임 저수준 호출 실패 시 echo 모드로 영구 강등(1회만 로깅).</summary>
        private void DegradeToEcho(Exception ex)
        {
            _hwUsable   = false;
            IsConnected = false;
            if (!_degradeLogged)
            {
                _degradeLogged = true;
                LoggerService.WriteToFile("ERROR",
                    $"[Comizoa IO] 하드웨어 호출 실패 — echo 모드로 전환: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            // 마스터 자원은 모션 쪽에서 해제하므로 IO 는 상태 플래그만 내린다.
            IsConnected = false;
        }

        public void Initialize(List<IODeviceInfo> configList)
        {
            if (configList == null) return;

            _ioMap.Clear();
            _digitalStates.Clear();
            _analogStates.Clear();
            _addrMap.Clear();

            foreach (var item in configList)
            {
                if (string.IsNullOrEmpty(item.Index)) continue;

                _ioMap[item.Index]         = item;
                _digitalStates[item.Index] = false;
                _analogStates[item.Index]  = 0.0;

                if (TryMapChannel(item.Address, out bool isOutput, out int channel))
                    _addrMap[item.Index] = (isOutput, channel);
            }
        }

        // Address("X100"/"Y101") → (출력 여부, SDK 채널). slave*8+bit 규칙.
        // 실배선 슬레이브 순서에 맞지 않으면 이 메서드만 조정하면 된다.
        private static bool TryMapChannel(string? address, out bool isOutput, out int channel)
        {
            isOutput = false;
            channel  = -1;
            if (string.IsNullOrEmpty(address) || address.Length < 2) return false;

            char kind = char.ToUpperInvariant(address[0]);
            if (kind != 'X' && kind != 'Y') return false;   // 디지털 X/Y 만 대상
            if (!int.TryParse(address.Substring(1), out int n)) return false;

            isOutput = kind == 'Y';
            int slave = n / 100;      // 백의 자리 = 슬레이브 노드
            int bit   = n % 100;      // 나머지 = 모듈 내 비트
            channel   = slave * PointsPerModule + bit;
            return true;
        }

        public List<IODeviceInfo> GetAllIOInfo() => _ioMap.Values.ToList();

        // ── 디지털 I/O ────────────────────────────────────────────────────────

        public bool GetInput(string indexName)
        {
            if (string.IsNullOrEmpty(indexName)) return false;
            if (IsConnected && _addrMap.TryGetValue(indexName, out var a) && !a.isOutput)
                return GetInput(a.channel);
            // 미연결/미매핑: echo 상태 반환(개발/가상)
            return _digitalStates.TryGetValue(indexName, out bool value) && value;
        }

        public bool GetOutput(string indexName)
        {
            if (string.IsNullOrEmpty(indexName)) return false;
            if (IsConnected && _addrMap.TryGetValue(indexName, out var a) && a.isOutput)
                return GetOutput(a.channel);
            return _digitalStates.TryGetValue(indexName, out bool value) && value;
        }

        public void SetOutput(string indexName, bool on)
        {
            if (string.IsNullOrEmpty(indexName) || !_ioMap.ContainsKey(indexName)) return;
            if (IsConnected && _addrMap.TryGetValue(indexName, out var a) && a.isOutput)
                SetOutput(a.channel, on);
            _digitalStates[indexName] = on;   // echo(현재값 조회/가상용)
        }

        // ── 아날로그 I/O ──────────────────────────────────────────────────────
        // ETS-D08MN 은 디지털 전용이라 아날로그는 미사용. 인터페이스 충족용 echo 구현.

        public double GetAnalogInput(string indexName)
        {
            if (string.IsNullOrEmpty(indexName)) return 0.0;
            return _analogStates.TryGetValue(indexName, out double value) ? value : 0.0;
        }

        public double GetAnalogOutput(string indexName)
        {
            if (string.IsNullOrEmpty(indexName)) return 0.0;
            return _analogStates.TryGetValue(indexName, out double value) ? value : 0.0;
        }

        public void SetAnalogOutput(string indexName, double value)
        {
            if (string.IsNullOrEmpty(indexName)) return;
            if (_ioMap.ContainsKey(indexName))
                _analogStates[indexName] = value;
        }

        // ── int 채널 기반 (저수준 ComiEcatDigitalIo 로 위임) ─────────────────
        // 저수준 호출은 예외를 UI 로 전파하지 않는다. 실패 시 echo 모드로 강등(무한 에러창 방지).
        public bool GetInput(int channel)
        {
            if (channel < 0 || !_hwUsable) return false;
            try { return _dio.GetInput(channel); }
            catch (Exception ex) { DegradeToEcho(ex); return false; }
        }

        public bool GetOutput(int channel)
        {
            if (channel < 0 || !_hwUsable) return false;
            try { return _dio.GetOutput(channel); }
            catch (Exception ex) { DegradeToEcho(ex); return false; }
        }

        public void SetOutput(int channel, bool on)
        {
            if (channel < 0 || !_hwUsable) return;
            try { _dio.SetOutput(channel, on); }
            catch (Exception ex) { DegradeToEcho(ex); }
        }

        // 여러 입력/출력을 한 번에 (비트마스크) — 실장비 HAL 과 동일.
        public uint GetInputBits()
        {
            if (!_hwUsable) return 0;
            try { return _dio.GetInputBits(); }
            catch (Exception ex) { DegradeToEcho(ex); return 0; }
        }

        public void SetOutputBits(uint bits)
        {
            if (!_hwUsable) return;
            try { _dio.SetOutputBits(bits); }
            catch (Exception ex) { DegradeToEcho(ex); }
        }

        // 아날로그 int 채널 — ETS-D08MN 미지원(no-op)
        public double GetAnalogInput(int channel) => 0.0;
        public double GetAnalogOutput(int channel) => 0.0;
        public void SetAnalogOutput(int channel, double value) { }

        // 실제 하드웨어는 물리 신호가 자동 발생하므로 no-op
        public void ScheduleInput(string indexName, bool value, int delayMs) { }
    }
}
