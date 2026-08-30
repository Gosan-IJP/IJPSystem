using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.Domain.Models.Printing
{
    /// <summary>
    /// 헤드 구동 전압 보정(Voltage offset).
    ///
    /// <para><b>무엇을 조절하는가</b> — 액적 속도다. 토출이 느리면(4 m/s 미만) 착탄이 뒤로
    /// 밀리고, 빠르면(6 m/s 초과) 튄다. 랩뷰 5_WIZ 절차가 스핏 → 드랍와처 측정 → 오프셋
    /// 조정을 4~6 m/s 가 나올 때까지 되풀이하는 이유가 이것이다.</para>
    ///
    /// <para><b>단위가 %인 이유</b> — 이 장비 헤드(EPSON S3200)에서 Meteor 가 받아 주는 것은
    /// 절대 전압이 아니라 <b>파형 전압 배율</b>(CPEX_WF_VScaleCoeff)이다. 기준 전압은 레시피의
    /// 웨이브폼 파일이 이미 들고 있고(Vst), 여기서는 그 파형을 통째로 몇 % 키우거나 줄인다.
    /// 그래서 화면 단위가 V 가 아니라 % 다.</para>
    /// </summary>
    public interface IHeadVoltage
    {
        /// <summary>지금 걸 수 있는가. 못 걸면 <see cref="NotReadyReason"/> 이 이유를 말한다.</summary>
        bool IsAvailable { get; }

        /// <summary>못 거는 이유. 걸 수 있으면 null.</summary>
        string? NotReadyReason { get; }

        /// <summary>마지막으로 <b>헤드에 실제로 들어간</b> 보정값(%). 화면 입력값이 아니다.</summary>
        double AppliedPercent { get; }

        /// <summary>보정값을 헤드에 적용한다. 실패하면 이유를 담아 던진다.</summary>
        void Apply(double percent);
    }

    /// <summary>
    /// 보정값을 Meteor 파형 배율로 옮기는 셈 — 하드웨어를 모른다(그래서 시험할 수 있다).
    /// </summary>
    public static class HeadVoltageScale
    {
        /// <summary>화면이 받는 범위(%). 랩뷰 화면의 빨간 안내 -25~25 와 같다.</summary>
        public const double MinPercent = -25.0;
        public const double MaxPercent =  25.0;

        /// <summary>Meteor 가 받는 배율 범위. 매뉴얼 CPEX_WF_VScaleCoeff = [0.5, 1.5].</summary>
        public const double MinCoefficient = 0.5;
        public const double MaxCoefficient = 1.5;

        public static double ClampPercent(double percent)
            => Math.Clamp(percent, MinPercent, MaxPercent);

        /// <summary>
        /// 보정 % → 파형 배율. 0% = 1.0(파형 그대로), +25% = 1.25.
        ///
        /// <para>화면 범위(±25%)가 이미 매뉴얼 범위(0.5~1.5) 안쪽이라 배율에서 잘릴 일은 없다.
        /// 그래도 한 번 더 조이는 이유는, 화면 범위를 나중에 넓혔을 때 헤드로 범위 밖 값이
        /// 넘어가는 것을 여기서 막기 위해서다.</para>
        /// </summary>
        public static double ToCoefficient(double percent)
            => Math.Clamp(1.0 + ClampPercent(percent) / 100.0, MinCoefficient, MaxCoefficient);

        /// <summary>
        /// 현재값에서 목표값까지 밟아 갈 중간값들(마지막은 반드시 목표값).
        ///
        /// <para><b>왜 한 번에 안 넣는가</b> — 랩뷰 화면의 [Rate of volt] 가 하던 일이다.
        /// 압전 헤드에 전압을 계단으로 던지면 그 순간 액면이 출렁여 노즐이 빈다. 몇 단계로
        /// 나눠 올리면 같은 자리에 도달하면서 그 충격이 없다.</para>
        ///
        /// <para><paramref name="stepPercent"/> 가 0 이하면 한 번에 간다(중간값 없음).</para>
        /// </summary>
        public static IReadOnlyList<double> Ramp(double fromPercent, double toPercent, double stepPercent)
        {
            double from = ClampPercent(fromPercent);
            double to   = ClampPercent(toPercent);

            if (stepPercent <= 0 || from == to) return new[] { to };

            var path = new List<double>();
            double dir = Math.Sign(to - from);
            double cur = from;

            // 부동소수 누적으로 목표를 스쳐 지나가지 않도록, 매번 남은 거리로 판단한다.
            while (Math.Abs(to - cur) > stepPercent)
            {
                cur += dir * stepPercent;
                path.Add(cur);
            }
            path.Add(to);
            return path;
        }
    }
}
