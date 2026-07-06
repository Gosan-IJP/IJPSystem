using IJPSystem.Platform.Domain.Models.IO;
using System.Collections.Generic;

namespace IJPSystem.Platform.Domain.Interfaces
{
    public interface IIODriver
    {
        bool Connect();
        void Disconnect();
        bool IsConnected { get; }
        void Initialize(List<IODeviceInfo> configList);
        List<IODeviceInfo> GetAllIOInfo();

        // ==========================================
        // 1. 디지털 입출력 (Digital IO)
        // ==========================================

        // 입력(Input) 읽기
        bool GetInput(int bitNo);
        bool GetInput(string indexName);

        // 출력(Output) 읽기
        bool GetOutput(int bitNo);
        bool GetOutput(string indexName); 

        // 출력(Output) 쓰기
        void SetOutput(int bitNo, bool on);
        void SetOutput(string indexName, bool on);


        // ==========================================
        // 2. 아날로그 입출력 (Analog IO)
        // ==========================================

        // 아날로그 입력(AI) 읽기
        double GetAnalogInput(int channel);
        double GetAnalogInput(string indexName);

        // 아날로그 출력(AO) 읽기 (현재 상태 확인용)
        double GetAnalogOutput(int channel);
        double GetAnalogOutput(string indexName);

        // 아날로그 출력(AO) 쓰기 (제어용)
        void SetAnalogOutput(int channel, double value);
        void SetAnalogOutput(string indexName, double value);

        // ==========================================
        // 3. 가상 드라이버 지원 (Virtual / Simulation)
        // ==========================================

        // 실제 드라이버: 하드웨어가 자연적으로 신호를 발생시키므로 no-op
        // 가상 드라이버: delayMs 후 해당 입력을 자동으로 세팅 (센서 응답 시뮬레이션)
        void ScheduleInput(string indexName, bool value, int delayMs);
    }
}