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

        /// <summary>F64 속성 설정(노출/게인 등). 문자열 속성명은 GenICam 경로(카메라별 상이).</summary>
        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern uint IMAQdxSetAttribute(uint id, string name, ImaqdxAttributeType type, double value);
    }
}
