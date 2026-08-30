using System;
using IJPSystem.Platform.Domain.Models.Printing;

namespace IJPSystem.Platform.Infrastructure.Devices.PrintHead
{
    /// <summary>
    /// 헤드 없이 화면을 확인하기 위한 전압 보정.
    ///
    /// <para>값을 기억만 하고 아무 데도 보내지 않는다. 실물이 실패했을 때 여기로 떨어지는
    /// 길은 없다 — 설정(<c>DriverMode.Head = "Virtual"</c>)으로만 고른다.
    /// 안 붙은 헤드에 전압이 걸린 것처럼 보이면 그게 가장 위험하다.</para>
    /// </summary>
    public sealed class VirtualHeadVoltage : IHeadVoltage
    {
        private readonly Action<string>? _log;

        public VirtualHeadVoltage(Action<string>? log = null) => _log = log;

        public bool IsAvailable => true;
        public string? NotReadyReason => null;
        public double AppliedPercent { get; private set; }

        public void Apply(double percent)
        {
            AppliedPercent = HeadVoltageScale.ClampPercent(percent);
            _log?.Invoke($"헤드 전압 보정 {AppliedPercent:F2}% (배율 {HeadVoltageScale.ToCoefficient(AppliedPercent):F3}) · 가상 — 보내지 않음");
        }
    }
}
