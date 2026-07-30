namespace IJPSystem.Drivers.Motion.Comizoa
{
    /// <summary>모션 축(=물리 HW 축 번호). MotorConfig.json 의 HwAxis 값이 그대로 (AxisId)로 캐스팅된다.
    /// 라벨은 로그/가독성용일 뿐이며, 미정의 정수(6축 이상)도 캐스팅되어 정상 동작한다.
    /// 2호기(6축) = X/Y/Z + Theta(T)/DwX/DwY.</summary>
    public enum AxisId { X = 0, Y = 1, Z = 2, Theta = 3, DwX = 4, DwY = 5 }

    /// <summary>홈(원점 복귀) 파라미터. (Set Home Parameters 대응)</summary>
    public sealed class HomeParameters
    {
        public int Direction { get; set; } = -1;     // +1/-1
        public double HighVelocity { get; set; } = 20.0;
        public double LowVelocity { get; set; } = 2.0;
        public double Acceleration { get; set; } = 100.0;
        public int HomeMode { get; set; } = 0;        // 홈 방식(센서/리밋 등)
        public double Offset { get; set; } = 0.0;     // 원점 오프셋
    }

    /// <summary>속도 프로파일. (Set Velocity 대응)</summary>
    public sealed class VelocityProfile
    {
        public double Velocity { get; set; } = 50.0;
        public double Acceleration { get; set; } = 200.0;
        public double Deceleration { get; set; } = 200.0;
    }

    /// <summary>소프트웨어 리밋. (Set SW_Limite 대응)</summary>
    public sealed class SoftLimit
    {
        public bool Enabled { get; set; } = true;
        public double PositiveLimit { get; set; }
        public double NegativeLimit { get; set; }
    }

    /// <summary>축 상태. (State 대응)</summary>
    public sealed class AxisState
    {
        public double Position { get; set; }
        public bool ServoOn { get; set; }
        public bool IsMoving { get; set; }
        public bool IsHomed { get; set; }
        public bool HomeBusy { get; set; }   // 원점복귀 진행 중(홈 모션 busy). 홈 완료 판정용.
        public bool Alarm { get; set; }
        public bool UpperLimit { get; set; }   // 상한 하드리밋(+EL)
        public bool LowerLimit { get; set; }   // 하한 하드리밋(-EL)
        public bool HomeSensor { get; set; }   // 원점(ORG/HOME) 센서
    }
}
