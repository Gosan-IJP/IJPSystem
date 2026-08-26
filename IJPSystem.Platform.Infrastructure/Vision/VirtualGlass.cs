using System;

namespace IJPSystem.Platform.Infrastructure.Vision
{
    /// <summary>
    /// 글라스를 하나 놔둔 셈 치는 모형 — 카메라 대신 <b>계산으로</b> 마크 픽셀 위치를 낸다.
    ///
    /// <para><b>어디에 쓰나</b>: 정렬 계산이 <b>정말 수렴하는지</b>를 장비 없이 확인하는 데 쓴다.
    /// 척 위에 조금 밀리고 조금 돌아간 글라스가 있고, 스테이지가 움직이면 그만큼 마크가 화면에서
    /// 움직인다 — 실제와 같은 규칙이라, 보정을 하면 실제로 줄어들고 부호를 틀리면 실제로 벌어진다.
    /// 계산이 스스로와 아귀가 맞는지가 아니라 <b>고치면 나아지는지</b>를 보는 자리다.</para>
    ///
    /// <para>회전중심을 정렬 자리에서 일부러 떨어뜨려 둔다 — T 를 돌리면 글라스가 딸려 움직이는
    /// 실제 현상이 재현돼야 "회전 뒤 마크1 복귀"가 뜻이 있는지 볼 수 있다.</para>
    ///
    /// <para><b>가상 운전(DriverMode=Virtual)에서는 쓰지 않는다.</b> 거기서는 마크 읽기를 통째로
    /// 건너뛴다 — 가상 스테이지는 티칭 좌표까지 가지도 못해서, 모형을 돌려도 마크가 시야 밖에
    /// 있다고 나올 뿐이다.</para>
    /// </summary>
    public sealed class VirtualGlass
    {
        // 기본값은 <b>시야 안에 들어오는</b> 크기여야 한다. 1.125µm/px · 1280×1024 면 시야가
        // 1.44 × 1.15mm 뿐이라, 기선 160mm 에서 0.1° 만 돌아도 마크2 가 화면 밖으로 나간다.

        /// <summary>척 위에서 밀려 놓인 정도[mm] (T=0 기준).</summary>
        public double OffsetXMm { get; set; } = 0.12;
        public double OffsetYMm { get; set; } = -0.08;

        /// <summary>돌아간 정도[도] — 반시계가 +.</summary>
        public double RotationDeg { get; set; } = 0.04;

        /// <summary>척 회전중심 — 정렬 자리 기준[mm]. 0 이 아니어야 회전이 이동을 만든다.</summary>
        public double ChuckCenterXMm { get; set; } = 12.0;
        public double ChuckCenterYMm { get; set; } = -8.0;

        /// <summary>화면 크기[px] — 마크가 밖으로 나가면 "못 찾음"이 된다.</summary>
        public int WidthPx { get; set; } = 1280;
        public int HeightPx { get; set; } = 1024;

        /// <summary>가상 매칭 점수. 합격선보다 넉넉히 높게 둔다.</summary>
        public double Score { get; set; } = 0.97;

        /// <summary>
        /// 지금 자리에서 마크가 화면 어디에 보이는가.
        /// </summary>
        /// <param name="slot">1 = 마크1, 2 = 마크2.</param>
        /// <param name="stageDxMm">정렬 티칭 자리에서 스테이지가 옮겨 온 X[mm].</param>
        /// <param name="stageDyMm">같은 값의 Y.</param>
        /// <param name="tDeg">지금 T 축 값[도].</param>
        /// <param name="tPositive">T 축의 + 가 도는 방향.</param>
        public MarkReading Mark(
            int slot, double stageDxMm, double stageDyMm, double tDeg, RotationSense tPositive,
            double pitchXMm, double pitchYMm, PixelToStage cal, double refPxX, double refPxY)
        {
            if (cal == null || !cal.IsCalibrated) return MarkReading.Miss;

            // T 값 → 실제로 돈 각(반시계 +). 시계가 + 인 축이면 부호가 뒤집힌다.
            double alpha = tPositive == RotationSense.Clockwise ? -tDeg : tDeg;

            // 회전중심을 축으로 돌면 글라스가 딸려 움직인다.
            var moved = Rotate(OffsetXMm - ChuckCenterXMm, OffsetYMm - ChuckCenterYMm, alpha);
            double gx = ChuckCenterXMm + moved.X;
            double gy = ChuckCenterYMm + moved.Y;

            double theta = RotationDeg + alpha;

            // 마크가 글라스 위에서 어디 있는가 — 마크1 이 기준, 마크2 는 설계 간격만큼 떨어져 있다.
            var (dx, dy) = slot == 2 ? GlassAlign.DesignedSeparation(pitchXMm, pitchYMm) : (0.0, 0.0);
            var onGlass = Rotate(dx, dy, theta);

            // 카메라는 정렬 티칭 자리에 고정 — 거기서 얼마나 벗어났는지가 곧 화면 위치다.
            double offX = stageDxMm + gx + onGlass.X;
            double offY = stageDyMm + gy + onGlass.Y;

            var px = cal.ToPx(offX, offY);
            double u = refPxX + px.U, v = refPxY + px.V;

            // 화면 밖이면 실제로도 못 찾는다 — 그 경우를 감춰 두면 가상에서만 되는 정렬이 된다.
            if (u < 0 || v < 0 || u >= WidthPx || v >= HeightPx) return MarkReading.Miss;

            return new MarkReading(true, Score, u, v);
        }

        private static (double X, double Y) Rotate(double x, double y, double deg)
        {
            double r = deg * Math.PI / 180.0;
            double c = Math.Cos(r), s = Math.Sin(r);
            return (x * c - y * s, x * s + y * c);
        }
    }
}
