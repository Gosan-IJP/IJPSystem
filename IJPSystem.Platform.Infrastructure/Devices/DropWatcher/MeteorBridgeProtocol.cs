using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// x86 HMI ↔ x64 MeteorBridge.exe 사이의 IPC 규약(named pipe + 한 줄 JSON).
    ///
    /// <b>왜 별도 프로세스인가</b>: Meteor PrinterInterface.dll 은 x64 인데 본 앱은 x86(Comizoa 32bit)이라
    /// 한 프로세스에 같이 로드할 수 없다(BadImageFormatException). x64 브리지가 Meteor DLL 을 호스팅하고,
    /// 앱은 이 파이프로 명령만 보낸다. (이 파일은 브리지 프로젝트에도 링크되어 단일 진실 소스가 된다)
    /// </summary>
    public static class MeteorBridgeProtocol
    {
        public const string PipeName = "IJPSystem.MeteorBridge";

        // 명령 이름 — 대소문자 구분.
        public const string CmdOpen  = "OPEN";    // cfg 로 프린터 열기
        public const string CmdSpit  = "SPIT";    // 선택 노즐 연속 토출 시작
        public const string CmdAbort = "ABORT";   // 토출 중단(1회)
        public const string CmdBusy  = "BUSY";    // 컨트롤러 busy 조회
        public const string CmdPower = "POWER";   // 헤드 전압 인가
        public const string CmdClose = "CLOSE";   // 프린터 닫기
        public const string CmdPing  = "PING";    // 연결 확인

        // ABORT 결과 문자열(SpitAbortResult 와 1:1).
        public const string AbortOk     = "Ok";
        public const string AbortBusy   = "Busy";
        public const string AbortFailed = "Failed";

        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>앱 → 브리지 명령. 필드는 명령별로 선택적으로 채운다.</summary>
    public sealed class BridgeRequest
    {
        public string Cmd { get; set; } = "";
        public string? Cfg { get; set; }
        public int[]? Nozzles { get; set; }
        public double? FreqHz { get; set; }
        public int? GreyLevel { get; set; }
        public int? TickleLevel { get; set; }
        public int? Volts { get; set; }
    }

    /// <summary>브리지 → 앱 응답.</summary>
    public sealed class BridgeResponse
    {
        public bool Ok { get; set; }
        public bool Busy { get; set; }         // BUSY 조회 결과
        public string? Result { get; set; }    // ABORT 결과(Ok/Busy/Failed)
        public string? Err { get; set; }       // 실패 사유
    }

    /// <summary>
    /// 브리지 파이프 클라이언트 — 요청/응답 한 쌍을 직렬화한다(_io 락으로 트랜잭션 보호).
    /// 연결이 끊기면 다음 호출에서 재연결을 시도한다.
    /// </summary>
    public sealed class MeteorPipeClient : IDisposable
    {
        private readonly object _io = new();
        private readonly int _connectTimeoutMs;
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public MeteorPipeClient(int connectTimeoutMs = 2000) => _connectTimeoutMs = connectTimeoutMs;

        public bool IsConnected => _pipe?.IsConnected == true;

        /// <summary>명령 1건 왕복. 연결이 없으면 먼저 연결한다. 실패 시 Ok=false 응답을 만든다.</summary>
        public BridgeResponse Send(BridgeRequest req)
        {
            lock (_io)
            {
                try
                {
                    EnsureConnected();
                    string line = JsonSerializer.Serialize(req, MeteorBridgeProtocol.Json);
                    _writer!.WriteLine(line);
                    _writer.Flush();

                    string? resp = _reader!.ReadLine();
                    if (resp == null) throw new IOException("브리지가 응답 없이 연결을 닫았습니다.");
                    return JsonSerializer.Deserialize<BridgeResponse>(resp, MeteorBridgeProtocol.Json)
                           ?? new BridgeResponse { Ok = false, Err = "응답 파싱 실패" };
                }
                catch (Exception ex)
                {
                    Close();   // 다음 호출에서 재연결
                    return new BridgeResponse { Ok = false, Err = ex.Message };
                }
            }
        }

        private void EnsureConnected()
        {
            if (_pipe?.IsConnected == true) return;
            Close();

            var pipe = new NamedPipeClientStream(".", MeteorBridgeProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(_connectTimeoutMs);   // 브리지 미기동이면 TimeoutException
            _pipe   = pipe;
            _reader = new StreamReader(pipe);
            _writer = new StreamWriter(pipe) { AutoFlush = false };
        }

        public void Close()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null; _reader = null; _pipe = null;
        }

        public void Dispose() => Close();
    }
}
