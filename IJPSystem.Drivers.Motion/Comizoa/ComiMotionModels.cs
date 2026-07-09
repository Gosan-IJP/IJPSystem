namespace IJPSystem.Drivers.Motion.Comizoa
{
    /// <summary>모션 축. 실제 축 구성에 맞게 확장.</summary>
    public enum AxisId { X = 0, Y = 1, Z = 2, Theta = 3 }

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
        public bool PositiveLimit { get; set; }
        public bool NegativeLimit { get; set; }
    }
}
