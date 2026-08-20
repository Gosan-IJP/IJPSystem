using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 헤드(Meteor) 상태를 한 번 조회하는 곳.
    ///
    /// <para><b>왜 인터페이스로 갈랐나</b>: 실물 <see cref="MeteorStatusMonitor"/> 안에
    /// "가상이면 가짜 값" 분기를 넣으면, 언젠가 그 분기가 제어PC 에서 켜진다.
    /// 네이티브 DLL 이 없거나 엔진이 안 떴을 때 조용히 가상으로 떨어지면
    /// <b>붙지도 않은 헤드가 초록불로 보인다</b> — 그게 가장 위험한 오작동이다.
    /// 그래서 가상은 설정(<c>DriverMode.Head = "Virtual"</c>)으로만 선택되는
    /// 별도 구현으로 두고, 실패는 실패로 남긴다.</para>
    /// </summary>
    public interface IMeteorStatusSource : IDisposable
    {
        /// <summary>상태 1회 조회. 절대 예외를 던지지 않는다(항상 결과 반환).</summary>
        MeteorHeadStatus Poll();

        /// <summary>고를 수 있는 상황 목록. 실물에는 없다(빈 목록).</summary>
        IReadOnlyList<string> Scenarios { get; }

        /// <summary>현재 상황. 실물에서는 설정해도 아무 일도 일어나지 않는다.</summary>
        string Scenario { get; set; }
    }
}
