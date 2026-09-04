using Dapper;
using IJPSystem.Platform.Application.Sequences;   // PointNames — 포인트별 이동 규칙
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.HMI.ViewModels;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.HMI.Services
{
    /// <summary>
    /// 서보 ON 명령을 보냈는데 정해진 시간 안에 켜지지 않은 축이 있다.
    ///
    /// <para>일반 예외와 나누는 이유: 이 경우의 조치가 다르다. 다른 실패는 "로그 확인 후 재시작"
    /// 이지만, 서보가 안 켜지는 것은 <b>드라이버 쪽</b>(전원·EMO·드라이버 알람) 문제라 프로그램을
    /// 다시 띄우기 전에 장비를 봐야 한다. 알람 코드도 그래서 따로 둔다.</para>
    /// </summary>
    internal sealed class ServoOnFailedException : System.Exception
    {
        public ServoOnFailedException(string axisNames)
            : base($"서보 ON 실패 — 켜지지 않은 축: {axisNames}. 드라이버 확인 후 프로그램 재실행 해주세요.")
            => AxisNames = axisNames;

        /// <summary>안 켜진 축 이름 목록(쉼표 구분) — 알람 메시지의 {0} 에 들어간다.</summary>
        public string AxisNames { get; }
    }

    /// <summary>
    /// IMotionService를 HMI의 SharedAxisList 기반으로 구현한다.
    /// Application 레이어 시퀀스에 주입되는 구현체.
    /// </summary>
    internal class MotionServiceAdapter : IMotionService
    {
        private readonly MainViewModel _mainVM;

        public MotionServiceAdapter(MainViewModel mainVM) => _mainVM = mainVM;

        /// <summary>서보 ON 명령 뒤 실제로 켜지기를 기다리는 한계 [ms].</summary>
        private const int ServoOnWaitMs = 5_000;
        private const int ServoOnPollMs = 100;

        /// <summary>
        /// 전 축 서보 ON — <b>명령만 하지 않고 켜졌는지 확인한다</b>.
        ///
        /// <para>예전에는 명령을 보내고 곧바로 다음 단계(원점복귀)로 넘어갔다. 드라이버가 꺼져
        /// 있거나 EMO 가 걸려 있으면 그 축만 조용히 안 켜진 채 원점복귀로 들어가고, 증상은
        /// 한참 뒤 "원점복귀가 안 끝난다"로 나타난다 — 원인에서 먼 자리에서 터진다.</para>
        ///
        /// <para>확인은 <b>드라이버에 직접</b> 묻는다. <c>Status.IsServoOn</c> 은 명령 직후
        /// 낙관적으로 true 가 되므로(AxisViewModel.ForceServoOnAsync) 캐시로는 못 가린다.</para>
        /// </summary>
        /// <exception cref="ServoOnFailedException">5초 안에 켜지지 않은 축이 하나라도 있으면.</exception>
        public async Task ServoOnAllAsync()
        {
            var all = _mainVM.SharedAxisList.ToList();

            foreach (var ax in all)
                await ax.ForceServoOnAsync();

            // 드라이버가 실제로 여자되기까지 시간이 걸린다 — 그 사이 상태 폴링(100ms)이 돈다.
            List<AxisViewModel> off = new();
            for (int i = 0; i < ServoOnWaitMs / ServoOnPollMs; i++)
            {
                off = all.Where(ax => !ax.IsDriverServoOn()).ToList();
                if (off.Count == 0) return;
                await Task.Delay(ServoOnPollMs);
            }

            string names = string.Join(", ", off.Select(ax => ax.Info.Name));
            _mainVM.AddLog(
                $"[MOTION] 서보 ON 실패 — {ServoOnWaitMs / 1000}초 안에 켜지지 않은 축: {names}",
                LogLevel.Error);

            throw new ServoOnFailedException(names);
        }

        /// <summary>
        /// 전 축 원점복귀.
        ///
        /// <para><b>T 는 Y 가 끝난 뒤에 건다</b>(2026-08-27). 둘을 같이 돌리면 기구가 간섭한다.
        /// 나머지 축은 그 줄과 나란히 돌므로 전체 시간은 거의 그대로다 — T 를 마지막에 몰아
        /// 두면 늦게 끝나는 다른 축까지 기다리게 된다.</para>
        ///
        /// <para>이 순서가 성립하는 근거: <c>HomeAsync</c> 는 원점복귀가 <b>끝나야</b> 돌아온다
        /// (실장 드라이버는 WaitForHomeDone, 가상은 시뮬레이션 종료까지 대기). 명령만 던지고
        /// 돌아오는 함수였다면 이렇게 이어 붙여도 동시에 도는 셈이 된다.</para>
        /// </summary>
        public async Task HomeAllAsync(CancellationToken ct)
        {
            var all   = _mainVM.SharedAxisList.ToList();
            var yAxis = FindAxis(all, "Y");
            var tAxis = FindAxis(all, "T");

            var rest = all.Where(ax => ax != yAxis && ax != tAxis).Select(ax => ax.HomeAsync());
            await Task.WhenAll(rest.Prepend(HomeYThenTAsync(yAxis, tAxis)));

            // 최대 50초 대기
            for (int i = 0; i < 500; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (all.All(ax => ax.Status?.IsHomeDone == true)) break;
                await Task.Delay(100, ct);
            }
        }

        /// <summary>
        /// Y 를 끝내고 T 를 건다. 한쪽이 없는 장비(T 가 없는 3축기 등)면 있는 쪽만 돈다.
        /// Y 가 실패하면 예외가 그대로 올라가 <b>T 는 시작되지 않는다</b> — 간섭을 피하려고
        /// 나눈 순서이므로, 앞이 끝났는지 모르는 채 뒤를 걸면 안 된다.
        /// </summary>
        private async Task HomeYThenTAsync(AxisViewModel? yAxis, AxisViewModel? tAxis)
        {
            if (yAxis != null) await yAxis.HomeAsync();

            if (tAxis == null) return;
            if (yAxis != null)
                _mainVM.AddLog($"[MOTION] {yAxis.Info.Name} 원점복귀 완료 — 이어서 {tAxis.Info.Name} 시작(간섭 회피)");
            await tAxis.HomeAsync();
        }

        private static AxisViewModel? FindAxis(List<AxisViewModel> axes, string axisNo) =>
            axes.FirstOrDefault(ax => string.Equals(ax.Info?.AxisNo, axisNo,
                                                    System.StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Z 를 나머지 축이 <b>다 선 뒤에</b> 움직이는 포인트.
        ///
        /// <para>GLASS ALIGN 은 글라스를 카메라 밑으로 넣는 자리다. 여기서 Z(헤드 승강)를 X·Y·T 와
        /// 같이 돌리면, 스테이지가 아직 흐르는 중에 헤드가 내려온다 — 지나가는 글라스 위로
        /// 내려오는 셈이라 간섭 여지가 있고, 실장에서 그렇게 보였다(2026-09-02).</para>
        ///
        /// <para>원점복귀의 Y→T 순서와 같은 성격의 규칙이다(<see cref="HomeAllAsync"/>). 다른
        /// 포인트까지 넓히지 않은 이유: 인쇄 사이클의 헤드 승강은 전용 포인트(PRINT HEAD UP/DOWN)로
        /// 따로 돌아 Z 하나뿐이라, 여기서 순서를 바꿔도 얻는 것 없이 사이클만 늘어난다.</para>
        /// </summary>
        private static bool MovesZLast(string pointName)
            => string.Equals(pointName, PointNames.GlassAlign, System.StringComparison.OrdinalIgnoreCase);

        private static bool IsZAxis(AxisViewModel ax)
            => string.Equals(ax.Info?.AxisNo, "Z", System.StringComparison.OrdinalIgnoreCase);

        public async Task MoveToPointAsync(string pointName, CancellationToken ct,
                                           MotionProfileKind profile = MotionProfileKind.Move)
        {
            var usedAxes = GetUsedAxesForPoint(pointName);

            // 진단: 이 포인트로 어떤 축을 어디로 이동시키는지 명시. 비어 있으면 활성 스냅샷 문제(레시피
            // APPLY/저장 안 됐거나 IsUsed=0) — READY 미이동 등의 원인을 로그로 즉시 판별.
            if (usedAxes.Count == 0)
                _mainVM.AddLog(
                    $"[MOTION] {pointName} 이동 — 사용 축 없음(활성 스냅샷 비어있음). 레시피를 저장/적용하세요.",
                    LogLevel.Warning);
            else
                _mainVM.AddLog(
                    $"[MOTION] {pointName} 이동 → " + string.Join(", ", usedAxes.Select(kv => $"{kv.Key}={kv.Value:F1}")),
                    LogLevel.Info);

            async Task<(string Axis, double Target, double Actual, bool InPos)> MoveOneAsync(AxisViewModel ax)
            {
                    try
                    {
                        ax.IsAbsMode = true;
                        ax.TargetPosition = usedAxes[ax.Info.Name];

                        // 적용된 레시피의 모션 프로파일 (편집 중인 axis.Info.MotionConfig 무시)
                        var snapCfg = _mainVM.RecipeVM.GetActiveMotionConfig(ax.Info.AxisNo);
                        var profileOverride = snapCfg == null ? null : profile switch
                        {
                            MotionProfileKind.Printing => snapCfg.Printing,
                            MotionProfileKind.Jog      => snapCfg.Jog,
                            _                          => snapCfg.Move,
                        };

                        // 1. 이동 시작 지점 — 지정된 프로파일(Move/Printing/Jog) 사용
                        await ax.MoveAsync(profile, profileOverride);

                        // InPosition 대기 (최대 20초) — driver 직접 폴링으로 ViewModel 캐시 우회
                        // (캐시는 100ms 주기 갱신이라 첫 iteration이 직전 step의 stale 값으로 즉시 break됨)
                        bool inPos = false;
                        for (int i = 0; i < 200; i++)
                        {
                            ct.ThrowIfCancellationRequested();

                            // 2. 상태 체크 지점
                            if (ax.IsDriverInPosition()) { inPos = true; break; }
                            await Task.Delay(100, ct);
                        }

                        // 도달 결과 — 지시만 남기고 결과를 안 남기면 티칭 오차/InPosition 미달을 추적할 수 없다.
                        // ★ 위치도 드라이버에서 직접 읽는다. 위 InPosition 은 드라이버를 직접 보는데
                        //   위치만 100ms 캐시(ax.CurrentPos)에서 읽으면 감속 중 값이 찍혀, 제대로 선 축이
                        //   어긋난 것처럼 보인다(11호기 INITIALIZE 에서 X -0.449mm · T +1.028deg 로 찍혔으나
                        //   InPos 는 전부 정상이었다 — 실재하지 않는 오차).
                        double target = usedAxes[ax.Info.Name];
                        double actual = ax.ReadDriverPosition() ?? ax.CurrentPos;
                        return (Axis: ax.Info.AxisNo, Target: target, Actual: actual, InPos: inPos);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"에러 발생: {ax.Info.Name} - {ex.Message}");
                        throw;
                    }
            }

            var moving = _mainVM.SharedAxisList
                .Where(ax => usedAxes.ContainsKey(ax.Info.Name))
                .ToList();

            // Z 를 뒤로 미루는 포인트면 두 묶음으로 나눈다. 아니면 전부 한 묶음이라 예전 그대로다.
            var zAxes  = MovesZLast(pointName) ? moving.Where(IsZAxis).ToList() : new List<AxisViewModel>();
            var others = moving.Where(ax => !zAxes.Contains(ax)).ToList();

            // 3. 전체 완료 대기 (이곳에 브레이크를 걸어 전체 종료를 확인하세요)
            var sw = Stopwatch.StartNew();

            // Select 는 지연 실행이라 ToList 로 <b>여기서</b> 시작시킨다 — 안 그러면 WhenAll 이
            // 열거하는 시점에야 돌기 시작해, 나누는 의미는 있어도 앞 묶음이 늦게 출발한다.
            var results = (await Task.WhenAll(others.Select(MoveOneAsync).ToList())).ToList();

            if (zAxes.Count > 0)
            {
                // 앞 축이 <b>다 선 뒤에</b> Z 를 건다. MoveOneAsync 는 InPosition 까지 기다렸다
                // 돌아오므로, 여기서 이어 붙이면 실제로 순서가 지켜진다(HomeAllAsync 의 Y→T 와 같은 근거).
                _mainVM.AddLog(
                    $"[MOTION] {pointName} — X·Y·T 정지 확인, 이어서 Z 이동(간섭 회피)", LogLevel.Info);
                results.AddRange(await Task.WhenAll(zAxes.Select(MoveOneAsync).ToList()));
            }

            sw.Stop();

            if (results.Count > 0)
            {
                bool allInPos = results.All(r => r.InPos);
                string detail = string.Join(", ", results.Select(r =>
                    $"{r.Axis}={r.Actual:F3}(목표 {r.Target:F3}, 오차 {r.Actual - r.Target:+0.000;-0.000;0.000})" +
                    (r.InPos ? "" : " ★InPos미달")));

                _mainVM.AddLog(
                    $"[MOTION] {pointName} 도달 — {detail} · 소요 {sw.Elapsed.TotalSeconds:F2}s",
                    allInPos ? LogLevel.Info : LogLevel.Warning);
            }
        }

        // 단일 축을 특정 포인트의 해당 축 좌표로 이동(다른 축은 유지). 멀티 스와스 프린트 스캔용.
        public async Task MoveAxisToPointAsync(string axisNo, string pointName, CancellationToken ct,
                                               MotionProfileKind profile = MotionProfileKind.Move)
        {
            var ax = _mainVM.SharedAxisList.FirstOrDefault(
                a => string.Equals(a.Info.AxisNo, axisNo, System.StringComparison.OrdinalIgnoreCase));
            if (ax == null) return;

            double? pos = GetAxisPositionMm(pointName, ax.Info.Name);
            if (pos == null) return;                      // 포인트에 해당 축 좌표 없음 → 이동 안 함

            await MoveSingleAxisAsync(ax, absolute: true, value: pos.Value, profile, ct);
        }

        // 단일 축 상대 이동(현재 위치 기준 delta). 스와스 스텝오버(X += headLength)용.
        public async Task MoveAxisRelativeAsync(string axisNo, double delta, CancellationToken ct,
                                                MotionProfileKind profile = MotionProfileKind.Move)
        {
            var ax = _mainVM.SharedAxisList.FirstOrDefault(
                a => string.Equals(a.Info.AxisNo, axisNo, System.StringComparison.OrdinalIgnoreCase));
            if (ax == null) return;

            await MoveSingleAxisAsync(ax, absolute: false, value: delta, profile, ct);
        }

        // 단일 축 이동 공통 — 활성 레시피 프로파일 적용 + InPosition 대기(driver 직접 폴링)
        private async Task MoveSingleAxisAsync(AxisViewModel ax, bool absolute, double value,
                                               MotionProfileKind profile, CancellationToken ct)
        {
            ax.IsAbsMode = absolute;
            ax.TargetPosition = value;

            var snapCfg = _mainVM.RecipeVM.GetActiveMotionConfig(ax.Info.AxisNo);
            var profileOverride = snapCfg == null ? null : profile switch
            {
                MotionProfileKind.Printing => snapCfg.Printing,
                MotionProfileKind.Jog      => snapCfg.Jog,
                _                          => snapCfg.Move,
            };

            await ax.MoveAsync(profile, profileOverride);

            // InPosition 대기 (최대 60초 — 인쇄 스캔 대비). ViewModel 캐시 우회.
            for (int i = 0; i < 600; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (ax.IsDriverInPosition()) break;
                await Task.Delay(100, ct);
            }
        }

        // 활성 레시피에서 특정 포인트의 단일 축 mm 값 (IsUsed=1만)
        // 축 이름은 짧은 형식("X") 또는 긴 형식("X AXIS") 양쪽 모두 허용 — DB에는 "X AXIS"로 저장됨
        public double? GetAxisPositionMm(string pointName, string axisName)
        {
            var dict = GetUsedAxesForPoint(pointName);
            if (dict.TryGetValue(axisName, out var v)) return v;

            // 짧은 이름으로 들어온 경우: dict 키에서 " AXIS" 접미사를 제거하고 비교
            foreach (var kv in dict)
            {
                var shortKey = kv.Key.Replace(" AXIS", "", System.StringComparison.OrdinalIgnoreCase);
                if (string.Equals(shortKey, axisName, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        // 활성 레시피의 포인트 — RecipeVM 의 in-memory snapshot 에서 조회
        // (편집 중인 레시피는 DB에 저장돼도 snapshot 에 반영되지 않음 → APPLY 해야만 시퀀스에 적용됨)
        private Dictionary<string, double> GetUsedAxesForPoint(string pointName)
        {
            var snap = _mainVM.RecipeVM.GetActivePoint(pointName);
            return snap == null
                ? new Dictionary<string, double>()
                : new Dictionary<string, double>(snap);
        }
    }
}
