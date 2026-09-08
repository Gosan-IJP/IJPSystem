// SerialScan — 어느 COM 포트에 Modbus RTU 슬레이브가 붙어 있는지 찾아낸다. 읽기 전용.
//
// 왜 필요한가:
//   새 제어PC 마다 DMD(메니스커스)와 iCore(스트로브)가 몇 번 포트에 잡히는지 다르다.
//   USB 시리얼은 꽂는 순서에 따라 번호가 바뀌어서 다른 호기의 설정을 옮겨 적을 수 없다.
//   2026-09-07 11호기: MeniscusConfig 는 COM6 인데 그 포트는 Intel AMT 가상 장치였고
//   (포트는 열리는데 응답이 없어 'DMD 연결됨' 뒤에 타임아웃), StrobeConfig 의 COM12 는
//   장치 관리자에 아예 없었다. 눈으로는 구분이 안 된다 — 두드려 봐야 안다.
//
// 무엇을 하나:
//   포트 × 보레이트 × UnitId 를 돌면서 홀딩/입력 레지스터를 한 워드 읽어 본다.
//   ·응답 있음      → 그 자리에 장치가 있다. 포트·보레이트·UnitId 가 확정된다.
//   ·Modbus 예외    → 장치는 있는데 그 주소가 없다. 이것도 '있다'는 증거다(오히려 확실하다).
//   ·타임아웃       → 아무도 없다.
//
// 쓰기는 하지 않는다. 모르는 장치에 값을 넣으면 되돌리기 어렵다.

using System.IO.Ports;
using NModbus;
using NModbus.Serial;   // SerialPort → IStreamResource 어댑터(CreateRtuMaster 확장)

const int DefaultTimeoutMs = 300;

// 예기치 못한 오류라도 창이 닫히기 전에 사유를 보여 준다. 현장에서 스택 한 줄만 보이고
// 사라지면 아무것도 못 한다(2026-09-07 11호기).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.WriteLine();
    Console.WriteLine("예기치 못한 오류: " + ((e.ExceptionObject as Exception)?.ToString() ?? "알 수 없음"));
    Console.Write("아무 키나 누르면 닫힙니다...");
    try { Console.ReadKey(intercept: true); } catch { }
};

var argv = Environment.GetCommandLineArgs();

string? Arg(string name)
{
    int i = Array.FindIndex(argv, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}
bool Has(string name) => argv.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

if (Has("--help") || Has("-h"))
{
    Console.WriteLine("""
        SerialScan — Modbus RTU 슬레이브 탐색(읽기 전용)

          SerialScan                       모든 포트 · 9600/19200/38400/115200 · UnitId 1~4
          SerialScan --ports COM8,COM9     포트 지정
          SerialScan --bauds 9600          보레이트 지정
          SerialScan --units 1-8           UnitId 범위
          SerialScan --addr 0x300          읽어 볼 레지스터(기본 0)
          SerialScan --timeout 300         한 번 두드리고 기다릴 ms

        찾은 장치에서 '살아 있는 주소'를 훑으려면 (포트·보레이트·UnitId 를 하나로 고정):
          SerialScan --ports COM10 --bauds 115200 --units 3 --sweep 0-1000

        조합 하나에 timeout 만큼 걸린다. 포트 3개 × 보레이트 4개 × UnitId 4개 = 48회 ≈ 15초.
        범위를 좁힐수록 빠르다.
        """);
    return;
}

// ★ 인자 하나 잘못 왔다고 진단 도구가 죽으면 안 된다 — 현장에서는 예외 스택만 남고
//   창이 닫혀 무엇이 잘못됐는지 알 길이 없다(2026-09-07 11호기: line 55 스택만 보였다).
//   못 읽은 값은 기본값으로 물러나고, 무엇을 못 읽었는지 말한 뒤 계속 간다.
Console.WriteLine("명령줄: " + string.Join(" ", argv.Skip(1)));
Console.WriteLine();

string[] ports = SplitList(Arg("--ports")) is { Length: > 0 } p
    ? p
    : SerialPort.GetPortNames().OrderBy(NumberOf).ToArray();

int[] bauds = ParseInts(Arg("--bauds"), "--bauds") is { Length: > 0 } b
    ? b
    : new[] { 9600, 19200, 38400, 115200 };

byte[] units = ParseRange(Arg("--units") ?? "1-4");
ushort addr  = ParseAddr(Arg("--addr") ?? "0");
int timeout  = int.TryParse(Arg("--timeout"), out var t) ? t : DefaultTimeoutMs;

if (ports.Length == 0)
{
    Console.WriteLine("COM 포트가 하나도 없습니다.");
    return;
}

// ── 주소 훑기 ────────────────────────────────────────────────────────────
// 포트·보레이트·UnitId 가 확정된 뒤, "그럼 어느 주소가 살아 있나"를 찾는 단계.
// 장치가 붙어 있으면 없는 주소도 즉시 예외로 대답하므로(타임아웃 대기 없음) 수백 개를
// 몇 초에 훑는다. 2026-09-07 11호기 DMD: COM10/115200/UnitId3 은 확정됐는데
// PressureReadAddress 가 placeholder(0) 라 Illegal Data Address 로 막혀 있었다.
string? sweep = Arg("--sweep");
if (sweep != null)
{
    RunSweep(ports.FirstOrDefault() ?? "", bauds.FirstOrDefault(), units.FirstOrDefault(), sweep, timeout);
    if (!Has("--no-pause"))
    {
        Console.WriteLine();
        Console.Write("아무 키나 누르면 닫힙니다...");
        try { Console.ReadKey(intercept: true); } catch { }
    }
    return;
}

Console.WriteLine($"포트   : {string.Join(", ", ports)}");
Console.WriteLine($"보레이트: {string.Join(", ", bauds)}");
Console.WriteLine($"UnitId : {string.Join(", ", units)}");
Console.WriteLine($"주소   : 0x{addr:X} · 타임아웃 {timeout}ms");
Console.WriteLine(new string('─', 72));

var hits = new List<string>();

foreach (string port in ports)
{
    foreach (int baud in bauds)
    {
        SerialPort sp;
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
            // 앱이 켜져 있으면 그 포트를 쥐고 있다 — 이것도 정보다(그 포트를 쓰는 무언가가 있다).
            Console.WriteLine($"{port,-7} {baud,-7} ── 점유 중(다른 프로그램이 열어 둠). 앱을 닫고 다시 실행하세요.");
            break;   // 이 포트는 보레이트를 바꿔 봐야 소용없다
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{port,-7} {baud,-7} ── 열기 실패: {ex.GetType().Name}");
            break;
        }

        using (sp)
        {
            var master = new ModbusFactory().CreateRtuMaster(sp);

            foreach (byte unit in units)
            {
                string result = Probe(master, unit, addr);
                if (result.Length == 0) continue;

                string line = $"{port,-7} {baud,-7} UnitId {unit,-3} → {result}";
                Console.WriteLine("★ " + line);
                hits.Add(line);
            }
        }
        Console.Write($"\r{port} @ {baud} 확인 완료          ");
    }
    Console.WriteLine();
}

Console.WriteLine(new string('─', 72));
if (hits.Count == 0)
{
    NoDevices();
}
else
{
    Console.WriteLine($"찾은 장치 {hits.Count}개:");
    foreach (string h in hits) Console.WriteLine("  " + h);
    Console.WriteLine();
    Console.WriteLine("이 값을 Config\\MeniscusConfig.json(DMD) 또는 Config\\StrobeConfig.json(iCore) 에 적으세요.");
    Console.WriteLine("어느 쪽인지 모르면 iCore 는 Tools\\IPulseProbe 로 --light 를 걸어 실제로 켜 보면 확실합니다.");
}

// 탐색기에서 더블클릭하면 끝나는 순간 창이 닫혀 결과를 볼 수 없다.
// 진단 도구는 사람이 결과를 읽으라고 있는 것이므로 기다린다(--no-pause 로 끌 수 있다).
if (!Has("--no-pause"))
{
    Console.WriteLine();
    Console.Write("아무 키나 누르면 닫힙니다...");
    try { Console.ReadKey(intercept: true); } catch { /* 출력이 파일로 넘어간 경우 */ }
}

void NoDevices()
{
    Console.WriteLine("응답한 장치가 없습니다.");
    Console.WriteLine();
    Console.WriteLine("  · 장치 전원이 들어와 있습니까?");
    Console.WriteLine("  · RS-485 면 A/B 극성이 바뀌지 않았습니까?");
    Console.WriteLine("  · 앱이 그 포트를 이미 쥐고 있지 않습니까? (앱을 닫고 다시 실행)");
    Console.WriteLine("  · 보레이트가 목록 밖일 수 있습니다 — --bauds 4800,57600 처럼 넓혀 보세요.");
}

// ── 한 조합 두드리기 ─────────────────────────────────────────────────────
// 빈 문자열 = 응답 없음. 그 외 = 무언가 있다.
static string Probe(IModbusMaster master, byte unit, ushort addr)
{
    try
    {
        ushort[] r = master.ReadHoldingRegisters(unit, addr, 1);
        return $"홀딩[0x{addr:X}] = {r.FirstOrDefault()}";
    }
    catch (SlaveException ex)
    {
        // 대답을 했다 = 장치가 거기 있다. 주소가 틀린 것뿐이라 오히려 확실한 증거다.
        return $"응답함(Modbus 예외: {ex.SlaveExceptionCode}) — 장치는 있고 주소만 다릅니다";
    }
    catch (TimeoutException) { }
    catch (InvalidOperationException) { }
    catch (IOException) { }

    try
    {
        ushort[] r = master.ReadInputRegisters(unit, addr, 1);
        return $"입력[0x{addr:X}] = {r.FirstOrDefault()}";
    }
    catch (SlaveException ex)
    {
        return $"응답함(Modbus 예외: {ex.SlaveExceptionCode}) — 장치는 있고 주소만 다릅니다";
    }
    catch { }

    return "";
}

// ── 주소 훑기 본체 ───────────────────────────────────────────────────────
/// <summary>
/// 한 장치(포트·보레이트·UnitId 고정)에서 <b>어느 레지스터 주소가 살아 있는지</b> 찾는다.
/// 홀딩(FC3)과 입력(FC4)을 따로 본다 — 기종마다 쓰는 함수코드가 다르다.
///
/// <para>읽기만 한다. 모르는 장치의 모르는 주소에 값을 넣으면 되돌릴 수 없다.</para>
/// </summary>
static void RunSweep(string port, int baud, byte unit, string range, int timeout)
{
    if (port.Length == 0 || baud <= 0 || unit == 0)
    {
        Console.WriteLine("--sweep 은 포트·보레이트·UnitId 를 하나씩 정해야 합니다.");
        Console.WriteLine("  예: SerialScan --ports COM10 --bauds 115200 --units 3 --sweep 0-1000");
        return;
    }

    int lo, hi;
    int dash = range.IndexOf('-');
    if (dash > 0)
    {
        if (!TryParseAddr(range[..dash], out lo)
            || !TryParseAddr(range[(dash + 1)..], out hi) || hi < lo)
        {
            Console.WriteLine($"--sweep 값 '{range}' 을 읽지 못했습니다. 예: --sweep 0-1000 또는 --sweep 0x0-0x400");
            return;
        }
    }
    else if (TryParseAddr(range, out lo))
    {
        // 숫자 하나만 주면 "거기부터 1000개" 로 본다 — 범위를 안 쓰고 '1' 만 넣는 일이 잦다.
        hi = lo + 1000;
        Console.WriteLine($"('{range}' 하나만 주셔서 {lo}~{hi} 로 봅니다. 범위를 정하려면 0-1000 처럼 적으세요.)");
    }
    else
    {
        Console.WriteLine($"--sweep 값 '{range}' 을 읽지 못했습니다. 예: --sweep 0-1000 또는 --sweep 0x0-0x400");
        return;
    }

    Console.WriteLine($"주소 훑기 : {port} @ {baud} · UnitId {unit} · 0x{lo:X}~0x{hi:X} ({hi - lo + 1}개)");
    Console.WriteLine("장치가 붙어 있으면 없는 주소도 즉시 대답하므로 금방 끝납니다.");
    Console.WriteLine(new string('─', 72));

    SerialPort sp;
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
        Console.WriteLine($"{port} 점유 중 — IJPSystem 앱을 닫고 다시 실행하세요.");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{port} 열기 실패: {ex.GetType().Name}: {ex.Message}");
        return;
    }

    using (sp)
    {
        var master = new ModbusFactory().CreateRtuMaster(sp);
        int quiet = 0;

        foreach (var (label, read) in new (string, Func<byte, ushort, ushort[]>)[]
                 {
                     ("홀딩(FC3)", (u, a) => master.ReadHoldingRegisters(u, a, 1)),
                     ("입력(FC4)", (u, a) => master.ReadInputRegisters(u, a, 1)),
                 })
        {
            Console.WriteLine($"[{label}]");
            int found = 0;

            for (int a = lo; a <= hi; a++)
            {
                try
                {
                    ushort[] v = read(unit, (ushort)a);
                    Console.WriteLine($"  0x{a:X4} ({a,5}) = {v.FirstOrDefault()}");
                    found++;
                }
                catch (SlaveException) { }          // 없는 주소 — 조용히 넘어간다
                catch (TimeoutException) { quiet++; }
                catch (IOException) { quiet++; }
                catch (InvalidOperationException) { quiet++; }

                // 갑자기 전부 무응답이면 장치가 빠졌거나 통신이 깨진 것이다. 끝까지 도는 건 낭비다.
                if (quiet > 20)
                {
                    Console.WriteLine("  응답이 끊겼습니다 — 장치 전원/배선을 확인하세요.");
                    return;
                }
            }

            Console.WriteLine(found == 0 ? "  읽을 수 있는 주소 없음" : $"  → {found}개");
            Console.WriteLine();
        }
    }

    Console.WriteLine(new string('─', 72));
    Console.WriteLine("값이 나온 주소를 Config 의 PressureReadAddress 등에 적으세요.");
    Console.WriteLine("어느 주소가 '압력'인지는 실제 압력계와 비교해서 정합니다.");
}

static bool TryParseAddr(string s, out int value)
{
    s = s.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        try { value = Convert.ToInt32(s[2..], 16); return true; } catch { value = 0; return false; }
    }
    return int.TryParse(s, out value);
}

static string[] SplitList(string? s) =>
    string.IsNullOrWhiteSpace(s)
        ? Array.Empty<string>()
        : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

/// <summary>숫자 목록. 못 읽은 조각은 이름을 대고 알린 뒤 건너뛴다 — 죽지 않는다.</summary>
static int[] ParseInts(string? s, string flag)
{
    var ok = new List<int>();
    foreach (string tok in SplitList(s))
    {
        if (int.TryParse(tok, out int v) && v > 0) ok.Add(v);
        else Console.WriteLine($"  (무시함) {flag} 의 '{tok}' 은 숫자가 아닙니다.");
    }
    return ok.ToArray();
}

/// <summary>"1-16" 또는 "1,2,3". 못 읽으면 기본 1~4 로 물러난다.</summary>
static byte[] ParseRange(string s)
{
    int dash = s.IndexOf('-');
    if (dash > 0
        && byte.TryParse(s[..dash], out byte lo) && lo > 0
        && byte.TryParse(s[(dash + 1)..], out byte hi) && hi >= lo)
        return Enumerable.Range(lo, hi - lo + 1).Select(v => (byte)v).ToArray();

    var ok = new List<byte>();
    foreach (string tok in SplitList(s))
    {
        if (byte.TryParse(tok, out byte v) && v > 0) ok.Add(v);
        else Console.WriteLine($"  (무시함) --units 의 '{tok}' 을 읽지 못했습니다.");
    }
    if (ok.Count > 0) return ok.ToArray();

    Console.WriteLine("  --units 를 읽지 못해 기본값 1~4 로 진행합니다.");
    return new byte[] { 1, 2, 3, 4 };
}

/// <summary>"0x300" 또는 "768". 못 읽으면 0.</summary>
static ushort ParseAddr(string s)
{
    try
    {
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt16(s[2..], 16)
            : ushort.Parse(s);
    }
    catch
    {
        Console.WriteLine($"  (무시함) --addr 의 '{s}' 을 읽지 못해 0 으로 진행합니다.");
        return 0;
    }
}

// COM10 이 COM9 앞에 오지 않도록 숫자로 정렬한다.
static int NumberOf(string port) =>
    int.TryParse(new string(port.Where(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue;
