using IJPSystem.Platform.Domain.Models.Motion;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Domain.Interfaces
{
    public interface IMotionService
    {
        // 기본은 Move 프로파일. 인쇄 구간 등에서는 Printing 프로파일 명시 가능.
        Task MoveToPointAsync(string pointName, CancellationToken ct, MotionProfileKind profile = MotionProfileKind.Move);
        Task ServoOnAllAsync();
        Task HomeAllAsync(CancellationToken ct);

        // 단일 축을 특정 포인트의 해당 축 좌표로 이동(다른 축 유지). 멀티 스와스 스캔에 사용.
        Task MoveAxisToPointAsync(string axisNo, string pointName, CancellationToken ct, MotionProfileKind profile = MotionProfileKind.Move);
        // 단일 축 상대 이동(현재 위치 기준 delta). 스와스 스텝오버에 사용.
        Task MoveAxisRelativeAsync(string axisNo, double delta, CancellationToken ct, MotionProfileKind profile = MotionProfileKind.Move);
    }
}
