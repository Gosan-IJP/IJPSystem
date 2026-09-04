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

        /// <summary>
        /// Meteor PrintEngine 을 띄운다(<c>PiStartPrintEngine</c>).
        ///
        /// <para><b>왜 여기인가</b>: 엔진을 붙잡는 자물쇠가 이 안에 있다. 폴링과 겹치면
        /// 같은 API 를 두 스레드가 부르게 되므로, 시작도 같은 자물쇠 안에서 해야 한다.</para>
        ///
        /// <para><b>왜 필요한가</b>: 상태 모니터는 <b>이미 도는</b> 엔진에 붙기만 한다
        /// (<c>PiOpenPrinter</c>). 엔진 자체를 띄우는 곳은 스핏 경로 하나뿐이라, 아무도
        /// 스핏을 누르지 않으면 화면은 영영 "엔진 미실행"이다. 그때마다 Meteor 도구를
        /// 따로 띄우게 하는 대신 화면에서 시작할 수 있게 한다.</para>
        ///
        /// <para>노즐을 쏘는 명령이 아니다 — 엔진 프로세스를 올리고 설정을 읽힐 뿐이다.</para>
        /// </summary>
        /// <param name="configPath">엔진이 읽을 <c>.cfg</c> 전체 경로.</param>
        /// <returns>성공 여부와 사람이 읽을 사유. 예외를 던지지 않는다.</returns>
        (bool Ok, string Message) StartEngine(string configPath);
    }
}
