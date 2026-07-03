using System;
using System.Runtime.InteropServices;

namespace IJPSystem.Drivers.IO.Comizoa
{
    /// <summary>
    /// Comizoa EtherCAT DIO 구현 (ComiEcatSdk.dll 래퍼).
    /// ※ [DllImport] 는 자리표시. 실제 SDK 함수로 교체.
    /// 모션(ComiEcatMotion)과 같은 EtherCAT 인스턴스를 공유하면 Init 은 모션 쪽 1회만 하면 됨.
    /// </summary>
    public sealed class ComiEcatDigitalIo : IDigitalIo
    {
        private const string Dll = "ComiEcatSdk.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_GetDin(int ch, out int s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_GetDout(int ch, out int s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetDout(int ch, int on);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_GetDinBits(out uint bits);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ecat_SetDoutBits(uint bits);

        private static void Check(int rc, string op)
        { if (rc != 0) throw new InvalidOperationException($"{op} 실패 (rc={rc})"); }

        public bool GetInput(int ch) { Check(ecat_GetDin(ch, out int s), "GetDin"); return s != 0; }
        public bool GetOutput(int ch) { Check(ecat_GetDout(ch, out int s), "GetDout"); return s != 0; }
        public void SetOutput(int ch, bool on) => Check(ecat_SetDout(ch, on ? 1 : 0), "SetDout");
        public uint GetInputBits() { Check(ecat_GetDinBits(out uint b), "GetDinBits"); return b; }
        public void SetOutputBits(uint bits) => Check(ecat_SetDoutBits(bits), "SetDoutBits");
    }
}
