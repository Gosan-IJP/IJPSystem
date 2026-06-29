using System;

namespace IJPSystem.Drivers.Meniscus
{
    /// <summary>
    /// 메니스커스(잉크 부압) 압력 제어.
    /// LabVIEW "3_WIZ_Set Meniscus.vi" 의 압력 읽기/설정 로직에 대응한다.
    /// 레지스터 raw 값 ↔ 실제 압력[kPa] 변환을 담당.
    /// </summary>
    public sealed class MeniscusController : IDisposable
    {
        private readonly DmdModbusClient _client;
        private readonly PressureDataType _dataType;

        public MeniscusController(DmdModbusClient client,
            PressureDataType dataType = PressureDataType.Int16Scaled)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _dataType = dataType;
        }

        /// <summary>현재 메니스커스 압력[kPa] 읽기 (DMD_Read Pressure 대응).</summary>
        public double ReadPressureKpa()
        {
            ushort count = _dataType == PressureDataType.Int16Scaled ? (ushort)1 : (ushort)2;
            ushort[] regs = _client.ReadHoldingRegisters(DmdRegisterMap.PressureReadAddress, count);
            return DecodePressure(regs);
        }

        /// <summary>메니스커스 압력 목표값[kPa] 설정.</summary>
        public void SetPressureKpa(double kpa)
        {
            switch (_dataType)
            {
                case PressureDataType.Int16Scaled:
                    {
                        ushort raw = (ushort)Math.Round(
                            (kpa - DmdRegisterMap.PressureOffset) / DmdRegisterMap.PressureScale);
                        _client.WriteSingleRegister(DmdRegisterMap.PressureSetpointAddress, raw);
                        break;
                    }
                case PressureDataType.Int32Scaled:
                    {
                        int rawInt = (int)Math.Round(
                            (kpa - DmdRegisterMap.PressureOffset) / DmdRegisterMap.PressureScale);
                        _client.WriteMultipleRegisters(
                            DmdRegisterMap.PressureSetpointAddress, SplitInt32(rawInt));
                        break;
                    }
                case PressureDataType.Float32:
                    {
                        _client.WriteMultipleRegisters(
                            DmdRegisterMap.PressureSetpointAddress, SplitFloat((float)kpa));
                        break;
                    }
            }
        }

        /// <summary>압력 제어 on/off (제어 레지스터 쓰기).</summary>
        public void SetControlEnabled(bool enabled)
            => _client.WriteSingleRegister(DmdRegisterMap.ControlAddress, (ushort)(enabled ? 1 : 0));

        // ---- 디코딩/인코딩 ----

        private double DecodePressure(ushort[] regs)
        {
            switch (_dataType)
            {
                case PressureDataType.Int16Scaled:
                    // 부호(음압) 대응 위해 short 로 해석
                    return (short)regs[0] * DmdRegisterMap.PressureScale + DmdRegisterMap.PressureOffset;

                case PressureDataType.Int32Scaled:
                    {
                        int v = (regs[0] << 16) | regs[1]; // 하이워드 먼저(big-endian 워드 가정)
                        return v * DmdRegisterMap.PressureScale + DmdRegisterMap.PressureOffset;
                    }
                case PressureDataType.Float32:
                    {
                        uint bits = ((uint)regs[0] << 16) | regs[1];
                        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        private static ushort[] SplitInt32(int v)
            => new[] { (ushort)((v >> 16) & 0xFFFF), (ushort)(v & 0xFFFF) };

        private static ushort[] SplitFloat(float f)
        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            return new[] { (ushort)((bits >> 16) & 0xFFFF), (ushort)(bits & 0xFFFF) };
        }

        public void Dispose() => _client?.Dispose();
    }
}
