using System.IO.Pipes;
using System.Text.Json;
using IJPSystem.MeteorBridge;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;

// MeteorBridge.exe — x64. 앱(x86)이 Meteor PrinterInterface(x64) 를 인프로세스로 못 부르므로,
// 이 프로세스가 named pipe 서버로 명령을 받아 컨트롤러에 위임한다.
//
// 지금은 MockMeteorController(가상). 실장 시:
//   1) csproj 에 PrinterInterfaceCLS.dll 참조 추가
//   2) IMeteorController 를 PrinterInterfaceCLS 로 감싼 구현으로 교체
//   3) 아래 controller 생성부만 교체 — 파이프/디스패치 코드는 그대로.

var logFile = Path.Combine(Path.GetTempPath(), "IJP_MeteorBridge.log");
void Log(string m)
{
    string line = $"[{DateTime.Now:HH:mm:ss.fff}] {m}";
    Console.WriteLine(line);
    try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
}

IMeteorController controller = new MockMeteorController(Log);
Log($"MeteorBridge 시작 (x64, {(Environment.Is64BitProcess ? "64bit" : "32bit")}) — pipe '{MeteorBridgeProtocol.PipeName}'");

// 앱 종료 후에도 유휴로 남지 않도록: 연결이 한 번 있었고 이후 끊기면 종료한다.
bool hadClient = false;

while (true)
{
    using var server = new NamedPipeServerStream(MeteorBridgeProtocol.PipeName,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);

    try { await server.WaitForConnectionAsync(); }
    catch (Exception ex) { Log($"연결 대기 오류: {ex.Message}"); continue; }

    hadClient = true;
    using var reader = new StreamReader(server);
    using var writer = new StreamWriter(server) { AutoFlush = true };

    try
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var resp = Dispatch(line, controller, Log);
            await writer.WriteLineAsync(JsonSerializer.Serialize(resp, MeteorBridgeProtocol.Json));
        }
    }
    catch (Exception ex) { Log($"세션 오류: {ex.Message}"); }

    Log("클라이언트 연결 종료");
    // 앱이 닫히면(파이프 끊김) 브리지도 정리하고 끝낸다.
    if (hadClient) { controller.Close(); Log("브리지 종료"); break; }
}

static BridgeResponse Dispatch(string line, IMeteorController c, Action<string> log)
{
    BridgeRequest? req;
    try { req = JsonSerializer.Deserialize<BridgeRequest>(line, MeteorBridgeProtocol.Json); }
    catch (Exception ex) { return new BridgeResponse { Ok = false, Err = $"요청 파싱 실패: {ex.Message}" }; }
    if (req == null) return new BridgeResponse { Ok = false, Err = "빈 요청" };

    try
    {
        switch (req.Cmd)
        {
            case MeteorBridgeProtocol.CmdPing:
                return new BridgeResponse { Ok = true };

            case MeteorBridgeProtocol.CmdOpen:
            {
                var (ok, err) = c.Open(req.Cfg ?? "");
                return new BridgeResponse { Ok = ok, Err = err };
            }
            case MeteorBridgeProtocol.CmdPower:
            {
                var (ok, err) = c.SetPower(req.Volts ?? 0);
                return new BridgeResponse { Ok = ok, Err = err };
            }
            case MeteorBridgeProtocol.CmdSpit:
            {
                var (ok, err) = c.Spit(req.Nozzles ?? Array.Empty<int>(),
                    req.FreqHz ?? 0, req.GreyLevel ?? 255, req.TickleLevel ?? 0);
                return new BridgeResponse { Ok = ok, Err = err };
            }
            case MeteorBridgeProtocol.CmdAbort:
                return new BridgeResponse { Ok = true, Result = c.Abort() };
            case MeteorBridgeProtocol.CmdBusy:
                return new BridgeResponse { Ok = true, Busy = c.IsBusy() };

            case MeteorBridgeProtocol.CmdClose:
                c.Close();
                return new BridgeResponse { Ok = true };

            default:
                return new BridgeResponse { Ok = false, Err = $"알 수 없는 명령: {req.Cmd}" };
        }
    }
    catch (Exception ex)
    {
        log($"명령 처리 오류({req.Cmd}): {ex.Message}");
        return new BridgeResponse { Ok = false, Err = ex.Message };
    }
}
