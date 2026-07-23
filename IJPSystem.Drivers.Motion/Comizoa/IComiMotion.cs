using System;

namespace IJPSystem.Drivers.Motion.Comizoa
{
    /// <summary>
    /// LabVIEW "Comi_Motion_lib" (Comizoa EtherCAT 모션) 전체 함수 인터페이스.
    /// 각 메서드가 원본 VI 하나에 대응한다.
    /// </summary>
    public interface IComiMotion : IDisposable
    {
        // ---- 1_Comm ----
        /// <summary>Init.vi : EtherCAT 마스터 연결·초기화.</summary>
        void Init(ComiEcatConfig config);
        /// <summary>UnloadDevice.vi : 해제.</summary>
        void Unload();

        // ---- 2_Motion ----
        /// <summary>Servo On.vi : 서보 ON/OFF.</summary>
        void ServoOn(AxisId axis, bool on);
        /// <summary>Home.vi : 원점 복귀.</summary>
        void Home(AxisId axis);
        /// <summary>Move_ABS.vi : 절대 위치 이동.</summary>
        void MoveAbsolute(AxisId axis, double position);
        /// <summary>Move_RES.vi : 상대 위치 이동.</summary>
        void MoveRelative(AxisId axis, double distance);
        /// <summary>Jog.vi : 조그(연속 이동). dir=+1/-1.</summary>
        void Jog(AxisId axis, int dir);
        /// <summary>정지.</summary>
        void Stop(AxisId axis);
        /// <summary>전 축 정지.</summary>
        void StopAll();

        // ---- 파라미터 설정 (Set*) ----
        /// <summary>Set Velocity.vi : 속도/가감속 설정.</summary>
        void SetVelocity(AxisId axis, VelocityProfile profile);
        /// <summary>Set EncResolution.vi : 엔코더 분해능(펄스/단위) 설정.</summary>
        void SetEncoderResolution(AxisId axis, double pulsePerUnit);
        /// <summary>현재 위치를 지정값으로 재정의(모션 없이 카운터 재설정). 원점복귀 후 0 세팅용.</summary>
        void SetPosition(AxisId axis, double position);
        /// <summary>Set Home Parameters.vi : 원점 복귀 파라미터(모드/오프셋/속도패턴 전체).</summary>
        void SetHomeParameters(AxisId axis, HomeParameters home);
        /// <summary>원점 복귀 '속도 패턴만' 설정(ecmHomeCfg_SetSpeedPatt). LabVIEW Set Home Parameters.vi 와 동일 — 모드/방향/오프셋 미변경.</summary>
        void SetHomeSpeedPattern(AxisId axis, double velocity, double acceleration, double deceleration, double specVelocity);
        /// <summary>Set SW_Limite.vi : 소프트웨어 리밋.</summary>
        void SetSoftLimit(AxisId axis, SoftLimit limit);
        /// <summary>Set AlState.vi : 알람 상태 설정/리셋(clear).</summary>
        void SetAlarmState(AxisId axis, bool reset);

        // ---- 4_State ----
        /// <summary>축 상태 조회.</summary>
        AxisState GetState(AxisId axis);
        /// <summary>이동 완료 대기(단축 모션 busy 기준).</summary>
        bool WaitForDone(AxisId axis, int timeoutMs);
        /// <summary>원점복귀 완료 대기(홈 전용 플래그 기준 — 단축 busy 와 다름).</summary>
        bool WaitForHomeDone(AxisId axis, int timeoutMs);
    }
}
