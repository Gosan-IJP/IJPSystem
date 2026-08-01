using System;
using System.Runtime.InteropServices;

namespace IJPSystem.Drivers.IO.Comizoa
{
    /// <summary>
    /// Comizoa EtherCAT (PCI/Ex, ComiECAT) DIO 구현 — ComiEcatSdk.dll 래퍼.
    /// 실제 export 확인 결과 이 DLL 은 PCI/Ex EtherCAT SDK(헤더 ComiEcatSdk_Api.h)이며,
    /// 디지털 IO 는 secXXX 가 아니라 ecdi/ecdo 접두사를 사용한다.
    /// 실제 SDK 함수:
    ///   · ecdiGetOne  : 단일 DI 채널 상태 읽기 (상태 0/1 을 반환)
    ///   · ecdoGetOne  : 단일 DO 채널 상태 읽기 (상태 0/1 을 반환)
    ///   · ecdoPutOne  : 단일 DO 채널 출력      (성공여부 1=성공 을 반환)
    ///   · ecdiGetMulti: 다수 DI 상태를 비트로 읽기 (비트값 반환)
    ///   · ecdoPutMulti: 다수 DO 상태를 비트로 출력 (성공여부 1=성공 을 반환)
    ///
    /// 규약 주의:
    ///   - Get 계열은 "리턴값 = 채널 상태(0/1)" 이고, 오류는 out ErrCode 로 전달된다.
    ///   - Put 계열은 "리턴값 = 성공여부(1=성공, 0=실패)" 이다.  (기존 placeholder 는 정반대였음)
    ///   - NetID 는 단일 EtherCAT 네트워크(0). 모션과 동일 마스터를 공유한다.
    ///   - 읽기(read)는 하드웨어에 무해하므로 ErrCode 로 예외를 던지지 않고 best-effort 로 값을 반환한다
    ///     (드라이버 Connect() 프로브가 벤디 채널수/일시오류로 잘못 echo 강등되는 것을 방지).
    ///     DLL 미존재/엔트리포인트 불일치 같은 마샬링 예외는 그대로 전파되어 상위에서 감지된다.
    ///
    /// ※ 호출규약(중요): ComiEcatSdk.dll 은 stdcall(WINAPI) 이다(헤더 CECAT_API=WINAPI).
    ///   따라서 CallingConvention 을 지정하지 않고 기본값(Winapi=StdCall)을 쓴다 — 공식 C# 예제
    ///   ComiECAT.cs 와 동일. Cdecl 로 선언하면 x86 에서 스택이 깨져 호출이 조용히 실패한다.
    ///   반환값(t_bool/t_success)은 1바이트라 [return: MarshalAs(I1)] bool 로 받는다.
    /// </summary>
    public sealed class ComiEcatDigitalIo : IDigitalIo
    {
        private const string Dll = "ComiEcatSdk.dll";
        private const int NetID = 0;   // 단일 EtherCAT 네트워크 ID

        // 시그니처는 Comizoa 공식 C# 예제(ComiECAT.cs)와 동일.
        // t_i32→int, t_ui32→uint, t_ui8→byte, t_dword→uint, t_bool/t_success→bool(I1)
        [DllImport(Dll)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ecdiGetOne(int netId, uint diChannel, out int errCode);

        [DllImport(Dll)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ecdoGetOne(int netId, uint doChannel, out int errCode);

        [DllImport(Dll)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ecdoPutOne(int netId, uint doChannel,
                                              [MarshalAs(UnmanagedType.I1)] bool outState, out int errCode);

        [DllImport(Dll)]
        private static extern uint ecdiGetMulti(int netId, uint iniChannel, byte numChannels, out int errCode);

        [DllImport(Dll)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool ecdoPutMulti(int netId, uint iniChannel, byte numChannels, uint dwOutStates, out int errCode);

        // 단일 채널 ─────────────────────────────────────────────
        public bool GetInput(int ch) => ecdiGetOne(NetID, (uint)ch, out _);   // 리턴값이 곧 채널 상태

        public bool GetOutput(int ch) => ecdoGetOne(NetID, (uint)ch, out _);

        public void SetOutput(int ch, bool on)
        {
            bool ok = ecdoPutOne(NetID, (uint)ch, on, out int err);   // true=성공
            if (!ok)
                throw new InvalidOperationException($"ecdoPutOne 실패 (ch={ch}, err={err})");
        }

        // 연결 프로브 — errCode 를 반환해 "거짓 연결"(마스터 미로드인데 0 반환)을 걸러낸다.
        public bool Probe(out int errCode)
        {
            _ = ecdiGetMulti(NetID, 0, 32, out errCode);   // 비트값은 무시, errCode 만 확인
            return errCode == 0;
        }

        // 다중 채널(비트마스크) ─────────────────────────────────
        public uint GetInputBits() => ecdiGetMulti(NetID, 0, 32, out _);   // 0번 채널부터 32비트
        public uint GetInputBits(out int errCode) => ecdiGetMulti(NetID, 0, 32, out errCode);   // 진단: errCode 동반
        public uint GetInputBits(uint iniChannel, out int errCode) => ecdiGetMulti(NetID, iniChannel, 32, out errCode);   // 진단: 채널 오프셋 블록

        public void SetOutputBits(uint bits)
        {
            bool ok = ecdoPutMulti(NetID, 0, 32, bits, out int err);   // true=성공
            if (!ok)
                throw new InvalidOperationException($"ecdoPutMulti 실패 (err={err})");
        }
    }
}
