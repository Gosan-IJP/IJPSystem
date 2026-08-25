using System;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>조그 스텝 단계. 거리가 아니라 단계로 두고, 실제 값은 축의 논리단위로 해석한다.</summary>
    public enum JogStepMode
    {
        /// <summary>누르고 있는 동안 연속 이동(dead-man).</summary>
        Continuous,
        /// <summary>미세 — 0.01 (직선축 10µm / 회전축 0.01°).</summary>
        Fine,
        /// <summary>보통 — 0.1 (직선축 100µm / 회전축 0.1°).</summary>
        Coarse,
        /// <summary>거침 — 1 (직선축 1000µm / 회전축 1°).</summary>
        Extra,
    }

    /// <summary>
    /// 조그 스텝 값 계산. 위치 티칭 화면과 축 제어 화면이 같은 규칙을 쓰도록 여기 한 곳에만 둔다.
    ///
    /// 스텝은 축의 논리단위 기준이다 — 직선축은 mm, T 는 deg(MotorConfig.json 의 Unit).
    /// 그래서 버튼을 단위별로 나누지 않고(µm 3개 + ° 3개) 단계로 묶었다. 단위별로 두면
    /// '회전축에 µm 를 누른' 조합이 생겨 별도 규칙이 필요해지는데, 단계로 묶으면 그 조합 자체가 없다.
    ///
    /// 값 자체는 축 종류와 무관하다 — 0.01 은 직선축에서 10µm, 회전축에서 0.01°다.
    /// 예전에는 회전축만 10배로 키웠는데(미세=0.1°), 그러면 라벨의 두 값이 같은 수치가 아니어서
    /// 화면 라벨과 실제 이동량이 어긋났다. 지금은 라벨 그대로가 곧 값이다.
    /// </summary>
    public static class JogStep
    {
        /// <summary>회전축 판정 — MotorConfig.json 의 Unit 이 "deg" 인 축(현재 T).</summary>
        public static bool IsRotary(string? unit) =>
            string.Equals(unit, "deg", StringComparison.OrdinalIgnoreCase);

        /// <summary>해당 축에 적용할 스텝(그 축의 논리단위). 0 = 연속.</summary>
        public static double For(JogStepMode mode, string? unit) => mode switch
        {
            JogStepMode.Fine   => 0.01,
            JogStepMode.Coarse => 0.1,
            JogStepMode.Extra  => 1.0,
            _                  => 0.0,   // Continuous
        };
    }
}
