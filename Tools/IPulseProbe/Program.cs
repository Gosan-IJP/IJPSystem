using System.IO.Ports;
using System.Text;
using NModbus;
using NModbus.Serial;

// ─────────────────────────────────────────────────────────────────────────────
// iCore iPulse LED 컨트롤러 진단 — 읽기 전용(FC3/FC4)
//
// 목적: 앱에 배선하기 전에 아래 세 가지를 실물로 확정한다.
//   ① Modbus RTU 로 응답하는가(함수코드 FC3/FC4 중 무엇인가)
//   ② 32bit 파라미터(Duration/Period/Trigger Delay)의 워드 순서
//   ③ 시간 파라미터의 스케일(1us 단위인가, 0.1us 단위인가)
//
// ②③ 은 컨피규레이터 화면에 보이는 값과 raw 를 비교해서 판정한다 →
// 실행 전 컨피규레이터에서 Duration / Period / Trigger Delay 표시값을 적어 둘 것.
//
// ★쓰기는 하지 않는다. 조명이 켜지거나 LED 가 손상될 수 있는 동작은 이 도구에 없다.
//   (Rated Current/Brightness 계열은 매뉴얼에 주소가 없어 추측 접근 자체를 금지)
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

// 탐색기에서 더블클릭하면 창이 바로 닫혀 출력을 볼 수 없다 →
//  ① 화면과 파일에 동시에 쓰고(Tee) ② 끝나면 키 입력을 기다린다.
// 로그는 exe 옆에 남긴다 — 실장 PC 에서 파일만 보내주면 되도록.
string exeDir  = AppContext.BaseDirectory;
string logPath = Path.Combine(exeDir, $"IPulseProbe_{DateTime.Now:yyyyMMdd_HHmmss}.log");
StreamWriter? logWriter = null;
try
{
    logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
    Console.SetOut(new TeeWriter(Console.Out, logWriter));
}
catch (Exception ex)
{
    Console.WriteLine($"(로그 파일 생성 실패 — 화면 출력만 합니다: {ex.Message})");
}

string port     = Arg("--port")  ?? "COM12";
int    baud     = int.TryParse(Arg("--baud"), out var b) ? b : 115200;
int    timeout  = int.TryParse(Arg("--timeout"), out var t) ? t : 1000;
byte[] unitIds  = ParseUnits(Arg("--units") ?? "1,2");

Console.WriteLine($"iPulse Probe — {port} @ {baud} 8N1, unit={string.Join(",", unitIds)}, timeout={timeout}ms");
Console.WriteLine("※ iPulse Configurator 가 포트를 잡고 있으면 열리지 않는다 — 'Port Close' 후 실행할 것.");
Console.WriteLine();

// 매뉴얼(iPulse configurator 사용법 rev03)의 GUI 라벨 괄호 = 레지스터 주소.
// 주소가 2씩 띄어진 항목은 32bit(2레지스터)로 본다.
var singles = new (ushort Addr, string Name, string? Enum)[]
{
    (0x200, "Slave Address",          null),
    (0x300, "Operation",              "0=OFF, 1=Continuous, 2=Pulse"),
    (0x301, "Trigger Input",          "0=Internal, 1=DigitalIO, 2=RJ45, 3=SoftTrigger, 4=ChannelPort"),
    (0x302, "Trigger Activation",     "0=Rising, 1=Falling"),
    (0x303, "Trigger Output",         "0=LEDSync, 1=Bypass, 2=Error, 3=Low, 4=High"),
    (0x304, "Trigger Out Inverter",   "0=OFF, 1=ON"),
    (0x305, "Sequence Mode",          "0=OFF, 1=Sequence"),
    (0x306, "SEQ_Start",              null),
    (0x307, "SEQ_Count / AutoVoltage","※ 매뉴얼에 두 항목이 같은 주소로 표기됨 — 확인 필요"),
    (0x308, "LPF Mode",               "0=OFF, 1=ON"),
    (0x309, "Auto Alarm Clear",       "0=OFF, 1=ON"),
};

var pairs = new (ushort Addr, string Name, string Unit)[]
{
    (0x310, "Duration",       "us"),
    (0x312, "Period",         "us"),
    (0x314, "Trigger Delay",  "us"),
    (0x316, "Maximum Voltage","V"),
    (0x318, "Multi Trigger",  "회"),
    (0x100, "Trigger_Count",  ""),
    (0x102, "Error_Count",    ""),
    (0x104, "AlarmCode",      ""),
    (0x106, "SequenceIndex",  ""),
    (0x108, "Period Limit",   "us"),
};

SerialPort? sp = null;
try
{
    sp = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
    {
        ReadTimeout = timeout,
        WriteTimeout = timeout,
    };
    sp.Open();
}
catch (UnauthorizedAccessException)
{
    Fail($"{port} 를 열 수 없습니다 — 다른 프로그램이 점유 중입니다.\n" +
         "  iPulse Configurator 의 [Port Close] 를 누르거나 프로그램을 닫고 다시 실행하세요.");
    Done(logPath, logWriter);
    return;
}
catch (Exception ex)
{
    Fail($"{port} 열기 실패: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"  (사용 가능한 포트: {string.Join(", ", SerialPort.GetPortNames())})");
    Done(logPath, logWriter);
    return;
}

using (sp)
{
    var master = new ModbusFactory().CreateRtuMaster(sp);

    // ── 조명 켜보기(선택) — 어느 sID 가 어느 카메라인지 확인하는 유일한 방법 ──
    //   --light <sID> <mode>   mode: 0=OFF, 1=Continuous(상시 점등), 2=Pulse
    //   ※ 실제로 LED 가 켜진다. 사람이 보는 앞에서만 쓸 것.
    //     밝기·정격전류는 건드리지 않으므로 컨피규레이터에 설정된 값 그대로 켜진다.
    string? lightUnit = Arg("--light");
    if (lightUnit != null)
    {
        var la = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(la, x => x.Equals("--light", StringComparison.OrdinalIgnoreCase));
        byte u  = byte.TryParse(la.ElementAtOrDefault(i + 1), out var uu) ? uu : (byte)0;
        ushort m = ushort.TryParse(la.ElementAtOrDefault(i + 2), out var mm) ? mm : (ushort)0;

        if (u == 0 || m > 2)
        {
            Fail("사용법: --light <sID> <mode>   (mode 0=OFF, 1=Continuous, 2=Pulse)");
        }
        else
        {
            string modeName = m switch { 1 => "Continuous(상시 점등)", 2 => "Pulse", _ => "OFF(소등)" };
            Console.WriteLine($"▶ sID {u} → Operation(0x300) = {m} : {modeName}");
            try
            {
                master.WriteSingleRegister(u, 0x300, m);
                Console.WriteLine("  쓰기 완료.");
            }
            catch (Exception ex)
            {
                // 실장 iCore 는 쓰기 응답 프레임이 비표준이라 CRC 검증에 걸릴 수 있다(2026-07-23 관측).
                // 반영 여부는 아래 리드백으로 판정한다.
                Console.WriteLine($"  쓰기 응답 이상({ex.GetType().Name}) — 리드백으로 확인합니다.");
                try { sp.DiscardInBuffer(); } catch { }
            }

            var back = Read(master, u, 0x300, 1, "FC3");
            Console.WriteLine(back == null
                ? "  리드백 실패 — 반영 여부 불명."
                : $"  리드백 Operation = {back[0]} → {(back[0] == m ? "반영됨" : "반영 안 됨")}");
            Console.WriteLine("  ※ 불이 안 들어오면 컨피규레이터에서 LED Enable(채널) 체크를 확인하세요.");
            Console.WriteLine();
        }
    }

    foreach (byte unit in unitIds)
    {
        Console.WriteLine(new string('─', 78));
        Console.WriteLine($"■ Unit(sID) {unit}");
        Console.WriteLine(new string('─', 78));

        // ① 어떤 함수코드로 읽히는지 먼저 판정 — Operation(0x300) 한 칸으로 시험한다.
        string? fc = Probe(master, unit, 0x300);
        if (fc == null)
        {
            Console.WriteLine("  응답 없음 — 이 sID 는 이 포트에 없거나, Modbus RTU 가 아닙니다.");
            Console.WriteLine("  (보레이트/패리티, DIP-SW 로 정한 sID, 종단저항을 확인하세요)");
            Console.WriteLine();
            continue;
        }
        Console.WriteLine($"  응답 함수코드: {fc}");
        Console.WriteLine();

        Console.WriteLine("  [단일 레지스터]");
        foreach (var (addr, name, meaning) in singles)
        {
            var v = TryRead(master, unit, addr, 1, fc);
            string raw = v == null ? "실패" : $"{v[0]}  (0x{v[0]:X4})";
            Console.WriteLine($"    0x{addr:X3}  {name,-24} = {raw}{(meaning == null ? "" : $"    {meaning}")}");
        }

        Console.WriteLine();
        Console.WriteLine("  [32bit 파라미터] — 두 워드를 양쪽 순서로 모두 계산해 표시(어느 쪽이 화면값과 맞는지 보세요)");
        foreach (var (addr, name, unitText) in pairs)
        {
            var v = TryRead(master, unit, addr, 2, fc);
            if (v == null)
            {
                Console.WriteLine($"    0x{addr:X3}  {name,-16} = 실패");
                continue;
            }
            // 실측 확정(2026-08-05): 시간 계열은 IEEE-754 float32, 하위 워드 먼저.
            //   Duration [0,16672] → 0x41200000 → 10.0us, Period → 10000.0us 로 딱 떨어졌다.
            //   반면 Maximum Voltage/Multi Trigger 는 16bit 정수(첫 워드)다 — 혼재 구조라 둘 다 보여준다.
            uint loFirst = ((uint)v[1] << 16) | v[0];
            float asFloat = BitConverter.Int32BitsToSingle(unchecked((int)loFirst));
            Console.WriteLine($"    0x{addr:X3}  {name,-16} = [{v[0]}, {v[1]}]   " +
                              $"float={asFloat:G9} {unitText}   int(첫워드)={v[0]}");
        }
        Console.WriteLine();
    }
}

Console.WriteLine(new string('─', 78));
Console.WriteLine("읽는 법 (2026-08-05 실측으로 확정)");
Console.WriteLine("  · 시간 계열(Duration/Period/Trigger Delay/Period Limit) = IEEE-754 float32, 하위 워드 먼저");
Console.WriteLine("    → 'float=' 값을 그대로 읽으면 된다(us). 스케일 환산 불필요.");
Console.WriteLine("  · Maximum Voltage / Multi Trigger = 16bit 정수 → 'int(첫워드)' 를 본다.");
Console.WriteLine("  · Operation(0x300) : 0=OFF, 1=Continuous, 2=Pulse.");
Console.WriteLine();
Console.WriteLine("어느 sID 가 어느 카메라인지 확인하려면 (LED 가 실제로 켜집니다):");
Console.WriteLine("    IPulseProbe.exe --light 1 1     ← sID1 상시 점등");
Console.WriteLine("    IPulseProbe.exe --light 1 0     ← 소등");

Done(logPath, logWriter);

// ── helpers ──────────────────────────────────────────────────────────────────

// 로그 위치를 알려주고 창이 닫히지 않도록 대기. --no-pause 면 대기하지 않는다(배치 실행용).
static void Done(string logPath, StreamWriter? log)
{
    Console.WriteLine();
    Console.WriteLine($"로그 파일: {logPath}");
    log?.Flush();
    log?.Dispose();

    if (Environment.GetCommandLineArgs().Any(a => a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase)))
        return;

    Console.WriteLine("아무 키나 누르면 닫힙니다...");
    try { Console.ReadKey(true); } catch { /* 리다이렉트 환경에선 키 입력 불가 — 그냥 종료 */ }
}


static string? Arg(string name)
{
    var a = Environment.GetCommandLineArgs();
    for (int i = 1; i < a.Length - 1; i++)
        if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
    return null;
}

static byte[] ParseUnits(string s) =>
    s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
     .Select(x => byte.TryParse(x, out var v) ? v : (byte)0)
     .Where(x => x > 0)
     .ToArray();

// FC3(Holding) → 실패 시 FC4(Input) 순으로 시험. 어느 쪽이 응답하는지 돌려준다.
static string? Probe(IModbusMaster master, byte unit, ushort addr)
{
    if (Read(master, unit, addr, 1, "FC3") != null) return "FC3";
    if (Read(master, unit, addr, 1, "FC4") != null) return "FC4";
    return null;
}

static ushort[]? TryRead(IModbusMaster master, byte unit, ushort addr, ushort count, string fc)
    => Read(master, unit, addr, count, fc);

static ushort[]? Read(IModbusMaster master, byte unit, ushort addr, ushort count, string fc)
{
    try
    {
        return fc == "FC4"
            ? master.ReadInputRegisters(unit, addr, count)
            : master.ReadHoldingRegisters(unit, addr, count);
    }
    catch
    {
        return null;   // 미지원 주소/무응답 — 진단 도구이므로 조용히 넘어간다
    }
}

static void Fail(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(msg);
    Console.ResetColor();
}

/// <summary>화면과 파일에 동시에 쓰는 TextWriter. (타입 선언은 최상위 문 뒤에 와야 하므로 파일 끝)</summary>
sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override Encoding Encoding => _a.Encoding;
    public override void Write(char value)        { _a.Write(value); _b.Write(value); }
    public override void Write(string? value)     { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush()                  { _a.Flush(); _b.Flush(); }
}
