using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.IO
{
    public class VirtualIODriver : IIODriver
    {
        private readonly Dictionary<string, IODeviceInfo> _ioMap = new();

        // 디지털 입력/출력 분리
        private readonly Dictionary<string, bool> _inputStates = new();
        private readonly Dictionary<string, bool> _outputStates = new();

        // 아날로그 입력/출력 분리
        private readonly Dictionary<string, double> _analogInputStates = new();
        private readonly Dictionary<string, double> _analogOutputStates = new();

        public bool IsConnected { get; private set; } = false;

        public List<IODeviceInfo> GetAllIOInfo() => _ioMap.Values.ToList();

        public bool Connect()
        {
            IsConnected = true;
            return true;
        }

        public void Disconnect()
        {
            IsConnected = false;
        }

        public void Initialize(List<IODeviceInfo> configList)
        {
            if (configList == null) return;

            _ioMap.Clear();
            _inputStates.Clear();
            _outputStates.Clear();
            _analogInputStates.Clear();
            _analogOutputStates.Clear();

            foreach (var item in configList)
            {
                if (string.IsNullOrEmpty(item.Index)) continue;

                _ioMap[item.Index] = item;

                string category = item.IoCategory?.ToLower().Replace(" ", "") ?? "";

                if (category.Contains("analog"))
                {
                    // 아날로그: Address 기준으로 Input/Output 분리
                    if (item.Address?.StartsWith("X") == true)
                        _analogInputStates[item.Index] = 0.0;
                    else if (item.Address?.StartsWith("Y") == true)
                        _analogOutputStates[item.Index] = 0.0;
                }
                else
                {
                    // 디지털: Address 기준으로 Input/Output 분리
                    if (item.Address?.StartsWith("X") == true)
                        _inputStates[item.Index] = false;
                    else if (item.Address?.StartsWith("Y") == true)
                        _outputStates[item.Index] = false;
                }
            }

        }

        // ── 디지털 Input ──
        public bool GetInput(string indexName) =>
            !string.IsNullOrEmpty(indexName) &&
            _inputStates.TryGetValue(indexName, out bool v) && v;

        // ── 디지털 Output ──
        public bool GetOutput(string indexName) =>
            !string.IsNullOrEmpty(indexName) &&
            _outputStates.TryGetValue(indexName, out bool v) && v;

        public void SetOutput(string indexName, bool on)
        {
            if (string.IsNullOrEmpty(indexName) || !_outputStates.ContainsKey(indexName)) return;
            _outputStates[indexName] = on;
        }

        // ── 아날로그 Input ──
        public double GetAnalogInput(string indexName) =>
            !string.IsNullOrEmpty(indexName) &&
            _analogInputStates.TryGetValue(indexName, out double v) ? v : 0.0;

        // ── 아날로그 Output ──
        public double GetAnalogOutput(string indexName) =>
            !string.IsNullOrEmpty(indexName) &&
            _analogOutputStates.TryGetValue(indexName, out double v) ? v : 0.0;

        public void SetAnalogOutput(string indexName, double value)
        {
            if (string.IsNullOrEmpty(indexName) || !_analogOutputStates.ContainsKey(indexName)) return;
            _analogOutputStates[indexName] = value;
        }

        // ── int 기반 (인터페이스 구현용, 미사용) ──
        public bool GetInput(int bitNo) => false;
        public uint GetInputBits() => 0;   // 가상 드라이버 — 진단 비트 없음
        public bool GetOutput(int bitNo) => false;
        public void SetOutput(int bitNo, bool on) { }
        public double GetAnalogInput(int channel) => 0.0;
        public double GetAnalogOutput(int channel) => 0.0;
        public void SetAnalogOutput(int channel, double value) { }

        /// <summary>
        /// Virtual 모드 전용: delayMs 후 입력 신호를 자동으로 세팅합니다.
        /// 시퀀스에서 WaitHelper.ForIOSignal과 함께 사용합니다.
        /// 실제 드라이버(EtherCat 등)는 하드웨어가 자연적으로 신호를 발생시키므로 no-op.
        /// </summary>
        public void ScheduleInput(string indexName, bool value, int delayMs)
        {
            if (!_inputStates.ContainsKey(indexName)) return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                _inputStates[indexName] = value;
            });
        }
    }
}