using System;
using System.Collections.Generic;
using System.Linq;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.IO;

namespace IJPSystem.Drivers.IO
{
    public class EtherCatIODriver : IIODriver
    {
        // IO 정보 및 상태 저장 (실제 EtherCAT SDK 데이터와 연동 예정)
        private readonly Dictionary<string, IODeviceInfo> _ioMap = new();
        private readonly Dictionary<string, bool> _digitalStates = new();
        private readonly Dictionary<string, double> _analogStates = new();

        public bool IsConnected { get; private set; }

        public bool Connect()
        {
            // TODO: 실제 EtherCAT Master SDK(예: Beckhoff, IntervalZero 등)의 
            // nio_Init() 또는 EcMasterInit() 로직이 이곳에 위치합니다.
            IsConnected = true;
            return true;
        }

        public void Disconnect()
        {
            // TODO: EtherCAT 통신 종료 및 리소스 해제
            IsConnected = false;
        }

        public void Initialize(List<IODeviceInfo> configList)
        {
            _ioMap.Clear();
            _digitalStates.Clear();
            _analogStates.Clear();

            foreach (var item in configList)
            {
                if (!string.IsNullOrEmpty(item.Index))
                {
                    _ioMap[item.Index] = item;
                    // 초기 상태 설정
                    _digitalStates[item.Index] = false;
                    _analogStates[item.Index] = 0.0;
                }
            }
        }

        public List<IODeviceInfo> GetAllIOInfo() => _ioMap.Values.ToList();

        // --- 디지털 I/O 구현 ---

        public bool GetInput(string indexName)
        {
            if (string.IsNullOrEmpty(indexName)) return false;
            // TODO: EtherCAT Input Process Image에서 bit 데이터 읽기
            return _digitalStates.TryGetValue(indexName, out bool value) ? value : false;
        }

        public bool GetOutput(string indexName) // 추가: 현재 출력 상태 확인용
        {
            if (string.IsNullOrEmpty(indexName)) return false;
            return _digitalStates.TryGetValue(indexName, out bool value) ? value : false;
        }

        public void SetOutput(string indexName, bool on)
        {
            if (string.IsNullOrEmpty(indexName)) return;
            if (_ioMap.ContainsKey(indexName))
            {
                // TODO: EtherCAT Output Process Image에 bit 데이터 쓰기
                _digitalStates[indexName] = on;
            }
        }

        // --- 아날로그 I/O 구현 (CS1061 오류 해결 핵심) ---

        public double GetAnalogInput(string indexName)
        {
            if (string.IsNullOrEmpty(indexName)) return 0.0;
            // TODO: EtherCAT 아날로그 입력 모듈(예: EL3062)에서 값 수신
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
            {
                // TODO: EtherCAT 아날로그 출력 모듈(예: EL4002)에 값 전송
                _analogStates[indexName] = value;
            }
        }

        // --- 인터페이스 규격을 위한 int 기반 메서드 (Stub) ---
        public bool GetInput(int bitNo) => false;
        public uint GetInputBits() => 0;
        public bool GetOutput(int bitNo) => false;
        public void SetOutput(int bitNo, bool on) { }
        public double GetAnalogInput(int channel) => 0.0;
        public double GetAnalogOutput(int channel) => 0.0;
        public void SetAnalogOutput(int channel, double value) { }

        // 실제 하드웨어는 물리 신호가 자동으로 발생하므로 no-op
        public void ScheduleInput(string indexName, bool value, int delayMs) { }
    }
}