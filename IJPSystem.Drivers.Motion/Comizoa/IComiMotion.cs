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
        /// <summary>Set Home Parameters.vi : 원점 복귀 파라미터.</summary>
        void SetHomeParameters(AxisId axis, HomeParameters home);
        /// <summary>Set SW_Limite.vi : 소프트웨어 리밋.</summary>
        void SetSoftLimit(AxisId axis, SoftLimit limit);
        /// <summary>Set AlState.vi : 알람 상태 설정/리셋(clear).</summary>
        void SetAlarmState(AxisId axis, bool reset);

        // ---- 4_State ----
        /// <summary>축 상태 조회.</summary>
        AxisState GetState(AxisId axis);
        /// <summary>이동 완료 대기.</summary>
        bool WaitForDone(AxisId axis, int timeoutMs);
    }
}
