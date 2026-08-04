using System;

namespace IJPSystem.Platform.HMI.Common
{
    /// <summary>조그 스텝 단계. 거리가 아니라 '미세/거침' 단계로 두고, 실제 값은 축 단위에 맞춰 고른다.</summary>
    public enum JogStepMode
    {
        /// <summary>누르고 있는 동안 연속 이동(dead-man).</summary>
        Continuous,
        /// <summary>미세 — 직선축 10µm / 회전축 0.1°.</summary>
        Fine,
        /// <summary>거침 — 직선축 100µm / 회전축 1°.</summary>
        Coarse,
    }

    /// <summary>
    /// 조그 스텝 값 계산. 위치 티칭 화면과 축 제어 화면이 같은 규칙을 쓰도록 여기 한 곳에만 둔다.
    ///
    /// 스텝은 축의 논리단위 기준이다 — 직선축은 mm, T 는 deg(MotorConfig.json 의 Unit).
    /// 그래서 버튼을 단위별로 나누지 않고(µm 2개 + ° 2개) 단계로 묶었다. 단위별로 두면
    /// '회전축에 µm 를 누른' 조합이 생겨 별도 규칙이 필요해지는데, 단계로 묶으면 그 조합 자체가 없다.
    /// </summary>
    public static class JogStep
    {
        /// <summary>회전축 판정 — MotorConfig.json 의 Unit 이 "deg" 인 축(현재 T).</summary>
        public static bool IsRotary(string? unit) =>
            string.Equals(unit, "deg", StringComparison.OrdinalIgnoreCase);

        /// <summary>해당 축에 적용할 스텝(그 축의 논리단위). 0 = 연속.</summary>
        public static double For(JogStepMode mode, string? unit)
        {
            bool rotary = IsRotary(unit);
            return mode switch
            {
                JogStepMode.Fine   => rotary ? 0.1 : 0.01,
                JogStepMode.Coarse => rotary ? 1.0 : 0.1,
                _                  => 0.0,   // Continuous
            };
        }
    }
}
