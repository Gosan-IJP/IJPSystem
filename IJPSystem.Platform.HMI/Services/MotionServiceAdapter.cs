using Dapper;
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
    /// IMotionService를 HMI의 SharedAxisList 기반으로 구현한다.
    /// Application 레이어 시퀀스에 주입되는 구현체.
    /// </summary>
    internal class MotionServiceAdapter : IMotionService
    {
        private readonly MainViewModel _mainVM;

        public MotionServiceAdapter(MainViewModel mainVM) => _mainVM = mainVM;

        public async Task ServoOnAllAsync()
        {
            foreach (var ax in _mainVM.SharedAxisList)
                await ax.ForceServoOnAsync();
        }

        public async Task HomeAllAsync(CancellationToken ct)
        {
            var tasks = _mainVM.SharedAxisList.Select(ax => ax.HomeAsync());
            await Task.WhenAll(tasks);

            // 최대 50초 대기
            for (int i = 0; i < 500; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_mainVM.SharedAxisList.All(ax => ax.Status?.IsHomeDone == true)) break;
                await Task.Delay(100, ct);
            }
        }

        public async Task MoveToPointAsync(string pointName, CancellationToken ct,
                                           MotionProfileKind profile = MotionProfileKind.Move)
        {
            var usedAxes = GetUsedAxesForPoint(pointName);

            var tasks = _mainVM.SharedAxisList
                .Where(ax => usedAxes.ContainsKey(ax.Info.Name))
                .Select(async ax =>
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
                        for (int i = 0; i < 200; i++)
                        {
                            ct.ThrowIfCancellationRequested();

                            // 2. 상태 체크 지점
                            if (ax.IsDriverInPosition()) break;
                            await Task.Delay(100, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"에러 발생: {ax.Info.Name} - {ex.Message}");
                        throw;
                    }
                }).ToList(); // 중요: 여기서 바로 실행 예약됨

            // 3. 전체 완료 대기 (이곳에 브레이크를 걸어 전체 종료를 확인하세요)
            await Task.WhenAll(tasks);
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
