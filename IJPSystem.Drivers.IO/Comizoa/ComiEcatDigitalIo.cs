using System;
using System.Runtime.InteropServices;

namespace IJPSystem.Drivers.IO.Comizoa
{
    /// <summary>
    /// Comizoa EtherCAT (SW Daemon) DIO 구현 — ComiEcatSdk.dll 래퍼.
    /// 실제 SDK 함수(EtherCAT Master SW Daemon, 헤더 ComiSWECATSdk_Api.h):
    ///   · secdiGetOne  : 단일 DI 채널 상태 읽기 (상태 0/1 을 반환)
    ///   · secdoGetOne  : 단일 DO 채널 상태 읽기 (상태 0/1 을 반환)
    ///   · secdoPutOne  : 단일 DO 채널 출력      (성공여부 1=성공 을 반환)
    ///   · secdiGetMulti: 다수 DI 상태를 비트로 읽기 (비트값 반환)
    ///   · secdoPutMulti: 다수 DO 상태를 비트로 출력 (성공여부 1=성공 을 반환)
    ///
    /// 규약 주의:
    ///   - Get 계열은 "리턴값 = 채널 상태(0/1)" 이고, 오류는 out ErrCode 로 전달된다.
    ///   - Put 계열은 "리턴값 = 성공여부(1=성공, 0=실패)" 이다.  (기존 placeholder 는 정반대였음)
    ///   - NetID 는 SW Daemon 기본 네트워크(0). 모션과 동일 마스터를 공유한다.
    ///   - 읽기(read)는 하드웨어에 무해하므로 ErrCode 로 예외를 던지지 않고 best-effort 로 값을 반환한다
    ///     (드라이버 Connect() 프로브가 벤디 채널수/일시오류로 잘못 echo 강등되는 것을 방지).
    ///     DLL 미존재/엔트리포인트 불일치 같은 마샬링 예외는 그대로 전파되어 상위에서 감지된다.
    ///   - x64 빌드에서는 호출규약(cdecl/stdcall) 차이가 무의미하지만 문서 기준 Cdecl 로 명시한다.
    /// </summary>
    public sealed class ComiEcatDigitalIo : IDigitalIo
    {
        private const string Dll = "ComiEcatSdk.dll";
        private const int NetID = 0;   // SW Daemon Network ID (필요 시 설정값으로 분리 가능)

        // t_i32→int, t_ui32→uint, t_ui8→byte, t_dword→uint, t_bool/t_success→int (SDK 상 int typedef)
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int secdiGetOne(int netId, uint diChannel, out int errCode);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int secdoGetOne(int netId, uint doChannel, out int errCode);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int secdoPutOne(int netId, uint doChannel, int outState, out int errCode);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint secdiGetMulti(int netId, uint iniChannel, byte numChannels, out int errCode);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int secdoPutMulti(int netId, uint iniChannel, byte numChannels, uint dwOutStates, out int errCode);

        // 단일 채널 ─────────────────────────────────────────────
        public bool GetInput(int ch)
        {
            int state = secdiGetOne(NetID, (uint)ch, out _);   // 리턴값이 곧 채널 상태(0/1)
            return state != 0;
        }

        public bool GetOutput(int ch)
        {
            int state = secdoGetOne(NetID, (uint)ch, out _);
            return state != 0;
        }

        public void SetOutput(int ch, bool on)
        {
            int ok = secdoPutOne(NetID, (uint)ch, on ? 1 : 0, out int err);   // 1=성공, 0=실패
            if (ok == 0)
                throw new InvalidOperationException($"secdoPutOne 실패 (ch={ch}, err={err})");
        }

        // 다중 채널(비트마스크) ─────────────────────────────────
        public uint GetInputBits()
        {
            return secdiGetMulti(NetID, 0, 32, out _);   // 0번 전역채널부터 32비트
        }

        public void SetOutputBits(uint bits)
        {
            int ok = secdoPutMulti(NetID, 0, 32, bits, out int err);   // 1=성공, 0=실패
            if (ok == 0)
                throw new InvalidOperationException($"secdoPutMulti 실패 (err={err})");
        }
    }
}
