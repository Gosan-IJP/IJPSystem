using System.Runtime.InteropServices;

namespace IJPSystem.Drivers.Vision.Imaqdx
{
    /// <summary>IMAQdx 카메라 제어 모드.</summary>
    internal enum ImaqdxCameraControlMode : uint { Controller = 0, Listener = 1 }

    /// <summary>Grab 시 버퍼 선택 모드.</summary>
    internal enum ImaqdxBufferNumberMode : uint { Next = 0, Last = 1, BufferNumber = 2 }

    /// <summary>IMAQdx 속성 값 타입.</summary>
    internal enum ImaqdxAttributeType : uint
    { U32 = 0, I64 = 1, F64 = 2, String = 3, Enum = 4, Bool = 5, Command = 6, Blob = 7 }

    /// <summary>IMAQdxEnumerateCameras 가 채우는 카메라 정보(고정 길이 문자열).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct ImaqdxCameraInformation
    {
        public uint Type;
        public uint Version;
        public uint Flags;
        public uint SerialNumberHi;
        public uint SerialNumberLo;
        public uint BusType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string InterfaceName;   // Open 시 사용하는 이름
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string VendorName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string ModelName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string CameraFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string CameraAttributeURL;
    }

    /// <summary>
    /// NI-IMAQdx (niimaqdx.dll) C API P/Invoke 선언.
    /// DllImport 는 지연 바인딩이라 DLL 이 없어도 빌드는 되며, 실제 호출 시점에 바인딩된다.
    /// 반환값 0(=IMAQdxErrorSuccess) 이면 성공.
    /// ※ 실장비 연동 시 실제 니임aq dx 헤더로 시그니처를 검증할 것.
    /// </summary>
    internal static class ImaqdxInterop
    {
        private const string Dll = "niimaqdx.dll";
        public const uint Success = 0;

        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxOpenCamera(string name, ImaqdxCameraControlMode mode, out uint session);

        [DllImport(Dll)]
        public static extern uint IMAQdxConfigureGrab(uint session);

        [DllImport(Dll)]
        public static extern uint IMAQdxGetImageData(uint session, byte[] buffer, uint bufferSize,
            ImaqdxBufferNumberMode mode, uint desiredBufferNumber, out uint actualBufferNumber);

        [DllImport(Dll)]
        public static extern uint IMAQdxCloseCamera(uint session);

        /// <summary>연결 가능한 카메라 열거. array=null 로 먼저 count 조회 후, 배열 할당해 재호출.</summary>
        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxEnumerateCameras(
            [In, Out] ImaqdxCameraInformation[]? cameraInformationArray,
            ref uint count,
            [MarshalAs(UnmanagedType.U1)] bool connectedOnly);

        // ── 속성 설정 ─────────────────────────────────────────────────────────
        // ★ IMAQdxSetAttribute 만 __cdecl 가변인자(헤더의 NI_FUNCC)이고 나머지는 __stdcall(NI_FUNC).
        //   Cdecl 을 빠뜨리면 x86 에서 호출 때마다 스택이 깨진다(즉시 크래시가 아니라 이후 임의 시점에
        //   터져서 원인 추적이 매우 어렵다). 가변인자라 타입별 오버로드를 EntryPoint 로 나눠 선언한다.
        //   C 가변인자 승격 규칙상 float 는 double 로 올라가므로 F64 는 반드시 double 로 넘긴다.

        /// <summary>F64 속성(노출/게인 등).</summary>
        [DllImport(Dll, EntryPoint = "IMAQdxSetAttribute", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxSetAttributeF64(uint id, string name, ImaqdxAttributeType type, double value);

        /// <summary>문자열 속성. GenICam 열거값(TriggerMode="On" 등)도 String 타입으로 넘긴다.</summary>
        [DllImport(Dll, EntryPoint = "IMAQdxSetAttribute", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxSetAttributeString(uint id, string name, ImaqdxAttributeType type, string value);

        /// <summary>U32 속성(타임아웃 등).</summary>
        [DllImport(Dll, EntryPoint = "IMAQdxSetAttribute", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxSetAttributeU32(uint id, string name, ImaqdxAttributeType type, uint value);

        // ── 연속(트리거) 획득 ─────────────────────────────────────────────────
        // 하드웨어 트리거 촬영은 ConfigureGrab 이 아니라 ConfigureAcquisition(continuous) + Start 조합이다.
        // 링버퍼에 프레임이 들어오면 GetImageData(Next) 가 그 다음 새 프레임을 하나씩 꺼낸다.

        [DllImport(Dll)]
        public static extern uint IMAQdxConfigureAcquisition(uint session,
            [MarshalAs(UnmanagedType.U4)] uint continuous, uint bufferCount);

        [DllImport(Dll)]
        public static extern uint IMAQdxStartAcquisition(uint session);

        [DllImport(Dll)]
        public static extern uint IMAQdxStopAcquisition(uint session);

        [DllImport(Dll)]
        public static extern uint IMAQdxUnconfigureAcquisition(uint session);

        /// <summary>오류 코드 → 사람이 읽을 메시지.</summary>
        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxGetErrorString(uint error,
            [Out] System.Text.StringBuilder message, uint messageLength);

        /// <summary>오류 코드를 메시지로 변환. 실패하면 16진 코드만 반환.</summary>
        public static string ErrorText(uint code)
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                if (IMAQdxGetErrorString(code, sb, (uint)sb.Capacity) == Success && sb.Length > 0)
                    return $"0x{code:X8} {sb}";
            }
            catch { }
            return $"0x{code:X8}";
        }

        /// <summary>획득 타임아웃 속성(ms). 트리거가 드문 구성에서는 명시적으로 키워야 한다.</summary>
        public const string AttrTimeout = "AcquisitionAttributes::Timeout";
    }
}
