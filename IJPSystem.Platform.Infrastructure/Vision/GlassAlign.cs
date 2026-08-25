using System;

namespace IJPSystem.Platform.Infrastructure.Vision
{
    /// <summary>
    /// 픽셀 → 기계좌표 변환(2×2). 배율·카메라 기울기·부호를 한 덩어리로 담는다.
    ///
    /// <para><b>왜 µm/px 두 개가 아니라 행렬인가</b>: 배율만 두면 "화면 아래가 기계 +Y 인가"
    /// 같은 부호와 카메라가 몇 도 틀어져 붙었는지를 따로 들고 다녀야 한다. 그 조합에서
    /// 부호를 하나 잘못 잡으면 정렬이 반대로 가고, 그건 모터가 반대로 나간다는 뜻이다.
    /// 교정 이동 두 번이 이 네 숫자를 한 번에 정하므로 헷갈릴 자리가 없다.</para>
    ///
    /// <para>기준: 스테이지가 Δ 만큼 움직이면 글라스 위의 마크는 화면에서 K⁻¹Δ 만큼 움직인다.
    /// 즉 이 행렬은 <b>화면에서 움직인 픽셀 → 기계에서 움직인 mm</b> 다.</para>
    /// </summary>
    public sealed class PixelToStage
    {
        /// <summary>mm/px. 첫 열(Kxu,Kyu)=화면 u 1px 의 기계 변위, 둘째 열=v 1px 의 기계 변위.</summary>
        public double Kxu { get; set; }
        public double Kxv { get; set; }
        public double Kyu { get; set; }
        public double Kyv { get; set; }

        public double Determinant => Kxu * Kyv - Kxv * Kyu;

        /// <summary>교정 전이면 0 행렬이거나 특이행렬이다. 이때는 어떤 계산도 하면 안 된다.</summary>
        public bool IsCalibrated => Math.Abs(Determinant) > 1e-12;

        /// <summary>화면에서 (du,dv) 픽셀 움직인 것이 기계에서 몇 mm 인가.</summary>
        public (double X, double Y) ToMm(double du, double dv)
            => (Kxu * du + Kxv * dv, Kyu * du + Kyv * dv);

        /// <summary>사람이 읽는 값 — 화면 가로 1px 이 기계에서 몇 µm 인가.</summary>
        public double MicronPerPxX => Math.Sqrt(Kxu * Kxu + Kyu * Kyu) * 1000.0;

        /// <summary>사람이 읽는 값 — 화면 세로 1px 이 기계에서 몇 µm 인가.</summary>
        public double MicronPerPxY => Math.Sqrt(Kxv * Kxv + Kyv * Kyv) * 1000.0;

        /// <summary>화면 u 축이 기계 X 축에서 돌아간 각(도). 카메라를 얼마나 비뚤게 달았는지.</summary>
        public double CameraAngleDeg => Math.Atan2(Kyu, Kxu) * 180.0 / Math.PI;

        /// <summary>
        /// 교정 결과가 광학계 사양에 맞는지. 크게 벗어나면 교정 자체가 잘못된 것이다.
        ///
        /// <para>교정은 "축을 10mm 움직였다"를 믿고 계산한다. 축이 실제로 안 움직였거나
        /// 마크를 엉뚱하게 잡았어도 그럴듯한 숫자가 나오고, 그 배율로 만든 이동량이
        /// 그대로 모터로 간다. 렌즈 사양(10호기 글라스 카메라 = 1.125µm/px)과 대조해 그런 교정을 거른다.</para>
        /// </summary>
        /// <param name="nominalMicronPerPx">광학계 사양값. 0 이면 검사하지 않는다(사양 미입력).</param>
        /// <param name="tolerancePercent">허용 오차[%]. 렌즈·작동거리 공차를 감안해 넉넉히 둔다.</param>
        public bool MatchesNominal(double nominalMicronPerPx, double tolerancePercent = 15.0)
        {
            if (nominalMicronPerPx <= 0) return true;
            if (!IsCalibrated) return false;

            double tol = nominalMicronPerPx * tolerancePercent / 100.0;
            return Math.Abs(MicronPerPxX - nominalMicronPerPx) <= tol
                && Math.Abs(MicronPerPxY - nominalMicronPerPx) <= tol;
        }

        /// <summary>
        /// 교정 — 축을 정해진 거리만큼 움직이고 마크가 화면에서 몇 픽셀 갔는지로 정한다.
        ///
        /// <para>X 로 <paramref name="moveXMm"/> 움직였을 때 마크가 <paramref name="duX"/>,
        /// <paramref name="dvX"/> 만큼 갔고, Y 로 <paramref name="moveYMm"/> 움직였을 때
        /// <paramref name="duY"/>, <paramref name="dvY"/> 만큼 갔다는 뜻이다.</para>
        ///
        /// <para>두 이동이 서로 평행하면(또는 한쪽이 0이면) 행렬을 정할 수 없다 — null 을 낸다.
        /// 마크를 놓쳤거나 이동이 실제로 일어나지 않은 경우가 여기로 온다.</para>
        /// </summary>
        public static PixelToStage? FromMoves(double moveXMm, double duX, double dvX,
                                              double moveYMm, double duY, double dvY)
        {
            // K·(duX,dvX) = (moveX, 0) · K·(duY,dvY) = (0, moveY)  →  K = D·P⁻¹
            double det = duX * dvY - duY * dvX;
            if (Math.Abs(det) < 1e-9) return null;
            if (Math.Abs(moveXMm) < 1e-9 || Math.Abs(moveYMm) < 1e-9) return null;

            return new PixelToStage
            {
                Kxu =  moveXMm * dvY / det,
                Kxv = -moveXMm * duY / det,
                Kyu = -moveYMm * dvX / det,
                Kyv =  moveYMm * duX / det,
            };
        }
    }

    /// <summary>정렬을 거절하는 선. 넘으면 계산은 하되 결과를 쓰지 않는다.</summary>
    public sealed class AlignLimits
    {
        /// <summary>이 점수 미만이면 못 찾은 것으로 본다.</summary>
        public double MinScore { get; set; } = 0.70;

        /// <summary>
        /// 이보다 크게 틀어져 있으면 자동으로 고치지 않는다.
        ///
        /// <para>크게 돌아 있으면 T 를 돌리는 순간 마크가 화면 밖으로 나가 다음 측정을
        /// 못 한다. 그리고 그만큼 틀어졌다면 글라스가 잘못 놓였을 가능성이 더 크다.</para>
        /// </summary>
        public double MaxAngleDeg { get; set; } = 2.0;

        /// <summary>이보다 멀리 벗어났으면 자동으로 고치지 않는다. 이유는 각도와 같다.</summary>
        public double MaxShiftMm { get; set; } = 5.0;

        /// <summary>두 마크가 이보다 가까우면 각도를 내지 않는다. 기선이 짧으면 오차가 그만큼 커진다.</summary>
        public double MinBaselineMm { get; set; } = 20.0;

        // ── 허용 오차 ────────────────────────────────────────────────────
        //
        // 위의 Max* 는 "너무 커서 자동으로 못 고친다"는 거절선이고, 아래는 "이 정도면 됐다"는 선이다.
        // 둘은 하는 일이 다르다 — 허용 오차가 없으면 시퀀스가 언제 멈출지를 모른다.
        //
        // 없으면 생기는 일 세 가지:
        //   ① 스테이지가 못 내는 0.3µm 짜리 이동을 명령하고, 측정 잡음을 끝없이 쫓는다.
        //   ② 재측정 → 재보정 반복이 끝나지 않는다.
        //   ③ 끝내 못 맞춘 글라스를 맞춘 것으로 알고 인쇄한다.

        /// <summary>이 안이면 T 를 돌리지 않는다. 측정 분해능(1.125µm/px·기선 160mm 에서 0.0004°) 아래로는 못 내려간다.</summary>
        public double AngleToleranceDeg { get; set; } = 0.010;

        /// <summary>
        /// 이 안이면 X 를 움직이지 않는다[mm].
        ///
        /// <para>X·Y 를 따로 두는 이유: 축마다 진직도와 분해능이 다르다. 한 값으로 묶으면
        /// 좋은 축이 나쁜 축에 끌려가거나 그 반대가 된다. 그래서 거리(반지름)가 아니라
        /// <b>축별로</b> 판정한다.</para>
        /// </summary>
        public double ShiftToleranceXMm { get; set; } = 0.020;

        /// <summary>이 안이면 Y 를 움직이지 않는다[mm].</summary>
        public double ShiftToleranceYMm { get; set; } = 0.020;

        /// <summary>재측정·재보정을 몇 번까지 되풀이할지. 이 횟수 안에 못 들어오면 글라스를 거절한다.</summary>
        public int MaxPasses { get; set; } = 3;
    }

    /// <summary>정렬을 멈추는 이유. <see cref="Ok"/> 가 아니면 스테이지를 움직이지 않는다.</summary>
    public enum AlignVerdict
    {
        Ok,
        /// <summary>µm/px 교정이 아직 없다.</summary>
        NotCalibrated,
        /// <summary>레시피에 피듀셜 마크 간격이 없다(0).</summary>
        PitchNotSet,
        /// <summary>매칭이 실패했다.</summary>
        MarkNotFound,
        /// <summary>찾긴 했으나 점수가 낮다 — 엉뚱한 곳일 수 있다.</summary>
        LowScore,
        /// <summary>두 마크가 너무 가깝다 — 각도를 믿을 수 없다.</summary>
        BaselineTooShort,
        /// <summary>너무 많이 돌아 있다.</summary>
        AngleTooLarge,
        /// <summary>너무 많이 벗어나 있다.</summary>
        ShiftTooLarge,
    }

    /// <summary>마크 한 개의 측정 결과(화면 픽셀).</summary>
    public readonly record struct MarkReading(bool Found, double Score, double PxX, double PxY)
    {
        public static MarkReading Miss => new(false, 0, 0, 0);
    }

    /// <summary>각도 계산 결과. 회전 보정량은 <c>-AngleDeg</c> 다.</summary>
    public readonly record struct AngleResult(
        AlignVerdict Verdict, double AngleDeg,
        double MeasuredPitchXMm, double MeasuredPitchYMm,
        bool WithinTolerance, string Message)
    {
        public bool Ok => Verdict == AlignVerdict.Ok;

        /// <summary>돌려야 하는가. 허용 오차 안이면 건드리지 않는다.</summary>
        public bool NeedsRotation => Ok && !WithinTolerance;

        /// <summary>실제로 잰 두 마크 사이 거리[mm]. 레시피 값과 크게 다르면 잘못 잡은 것이다.</summary>
        public double MeasuredPitchMm =>
            Math.Sqrt(MeasuredPitchXMm * MeasuredPitchXMm + MeasuredPitchYMm * MeasuredPitchYMm);
    }

    /// <summary>평행이동 보정량. 이 값만큼 스테이지를 움직이면 마크가 기준 자리로 돌아온다.</summary>
    public readonly record struct ShiftResult(
        AlignVerdict Verdict, double DxMm, double DyMm,
        bool WithinTolerance, string Message)
    {
        public bool Ok => Verdict == AlignVerdict.Ok;
        public double DistanceMm => Math.Sqrt(DxMm * DxMm + DyMm * DyMm);

        /// <summary>움직여야 하는가. 허용 오차 안이면 건드리지 않는다 —
        /// 스테이지가 못 내는 이동을 명령하고 잡음을 쫓게 된다.</summary>
        public bool NeedsMove => Ok && !WithinTolerance;
    }

    /// <summary>
    /// 글라스 정렬 계산 — 두 피듀셜 마크로 각도를, 한 마크로 어긋난 거리를 낸다.
    ///
    /// <para><b>여기에는 모터도 카메라도 없다.</b> 들어오는 것은 이미 측정된 픽셀 좌표뿐이다.
    /// 자동 정렬에서 위험한 부분은 전부 이 계산과 거절 조건에 있다 — 엉뚱하게 잡은 매칭으로
    /// 모터가 나가는 것을 막는 자리가 여기다. 그래서 장비 없이 검증할 수 있게 떼어 두었다.</para>
    ///
    /// <para><b>측정 원리</b>: 카메라는 고정이고 글라스가 스테이지를 타고 움직인다.
    /// 마크1 을 찍고 스테이지를 ΔS 만큼 옮겨 마크2 를 찍으면, 두 마크의 실제 간격 벡터는
    /// <c>m = -ΔS + K·Δp</c> 다(K = 픽셀→mm). 이 m 이 설계 간격에서 몇 도 돌아 있는지가 곧 글라스 회전이다.
    /// 이동 방향의 부호를 여기서 정하지 않고 <b>실제로 명령한 ΔS 를 그대로 받는</b> 이유는,
    /// 그 부호가 장비 배치에 달린 값이라 코드가 짐작하면 반대로 돌기 때문이다.</para>
    /// </summary>
    public static class GlassAlign
    {
        /// <summary>
        /// 마크2 를 카메라 밑으로 데려오는 스테이지 이동.
        ///
        /// <para><b>마크2 는 마크1 에서 -Y 쪽(글라스 아래쪽)에 있다.</b> 카메라는 고정이므로
        /// 아래에 있는 마크를 올려다 붙이려면 글라스를 <b>+Y 로</b> 밀어야 한다.
        /// 그래서 이동 부호는 레시피 간격 그대로다(10호기 배치, 2026-08-25).</para>
        ///
        /// <para>이 함수와 <see cref="DesignedSeparation"/> 는 항상 짝으로 쓴다 —
        /// 둘 중 하나만 부호를 뒤집으면 정렬이 반대로 돌고, 그건 모터가 반대로 나간다는 뜻이다.
        /// 짝을 놓칠 자리를 없애려고 <see cref="SolveAngleFromPitch"/> 를 따로 두었다.</para>
        /// </summary>
        public static (double Dx, double Dy) StageMoveToMark2(double pitchXMm, double pitchYMm)
            => (pitchXMm, pitchYMm);

        /// <summary>설계상 마크1 → 마크2 벡터. 마크2 가 -Y 쪽이므로 이동과 반대다.</summary>
        public static (double X, double Y) DesignedSeparation(double pitchXMm, double pitchYMm)
            => (-pitchXMm, -pitchYMm);

        /// <summary>
        /// 레시피 간격만 주면 이동 부호까지 알아서 맞춰 각도를 구한다. <b>시퀀스는 이쪽을 쓴다.</b>
        /// </summary>
        public static AngleResult SolveAngleFromPitch(
            MarkReading mark1, MarkReading mark2,
            double pitchXMm, double pitchYMm,
            PixelToStage? cal, AlignLimits? limits = null)
        {
            var move = StageMoveToMark2(pitchXMm, pitchYMm);
            var sep  = DesignedSeparation(pitchXMm, pitchYMm);
            return SolveAngle(mark1, mark2, move.Dx, move.Dy, sep.X, sep.Y, cal, limits);
        }

        /// <summary>
        /// 두 마크로 글라스가 몇 도 돌아 있는지 구한다.
        /// </summary>
        /// <param name="mark1">마크1 측정(스테이지가 <c>S₁</c> 일 때).</param>
        /// <param name="mark2">마크2 측정(스테이지가 <c>S₁+ΔS</c> 일 때).</param>
        /// <param name="stageDxMm">실제로 명령한 스테이지 이동 ΔS 의 X.</param>
        /// <param name="stageDyMm">실제로 명령한 스테이지 이동 ΔS 의 Y.</param>
        /// <param name="pitchXMm">설계상 두 마크 간격 X(레시피).</param>
        /// <param name="pitchYMm">설계상 두 마크 간격 Y(레시피).</param>
        public static AngleResult SolveAngle(
            MarkReading mark1, MarkReading mark2,
            double stageDxMm, double stageDyMm,
            double pitchXMm, double pitchYMm,
            PixelToStage? cal, AlignLimits? limits = null)
        {
            var lim = limits ?? new AlignLimits();

            if (cal == null || !cal.IsCalibrated)
                return Fail(AlignVerdict.NotCalibrated, "µm/px 교정이 없습니다 — 교정을 먼저 하세요.");

            double designed = Math.Sqrt(pitchXMm * pitchXMm + pitchYMm * pitchYMm);
            if (designed < 1e-9)
                return Fail(AlignVerdict.PitchNotSet,
                            "레시피에 피듀셜 마크 간격이 없습니다 — 글라스 정보에 X/Y 를 넣으세요.");

            var bad1 = Check(mark1, lim.MinScore, "마크1");
            if (bad1 != null) return Fail(bad1.Value.Verdict, bad1.Value.Message);
            var bad2 = Check(mark2, lim.MinScore, "마크2");
            if (bad2 != null) return Fail(bad2.Value.Verdict, bad2.Value.Message);

            // m = -ΔS + K·Δp
            var cam = cal.ToMm(mark2.PxX - mark1.PxX, mark2.PxY - mark1.PxY);
            double mx = -stageDxMm + cam.X;
            double my = -stageDyMm + cam.Y;
            double measured = Math.Sqrt(mx * mx + my * my);

            if (measured < lim.MinBaselineMm)
                return new AngleResult(AlignVerdict.BaselineTooShort, 0, mx, my, false,
                    $"두 마크 간격이 {measured:F1}mm 로 너무 짧습니다(최소 {lim.MinBaselineMm:F0}mm) — " +
                    "같은 마크를 두 번 잡았을 수 있습니다.");

            double angle = Normalize(
                (Math.Atan2(my, mx) - Math.Atan2(pitchYMm, pitchXMm)) * 180.0 / Math.PI);

            if (Math.Abs(angle) > lim.MaxAngleDeg)
                return new AngleResult(AlignVerdict.AngleTooLarge, angle, mx, my, false,
                    $"{angle:+0.000;-0.000}° 로 너무 많이 돌아 있습니다(한계 ±{lim.MaxAngleDeg:F1}°) — " +
                    "글라스를 다시 놓고 시작하세요.");

            bool within = Math.Abs(angle) <= lim.AngleToleranceDeg;

            return new AngleResult(AlignVerdict.Ok, angle, mx, my, within,
                $"회전 {angle:+0.000;-0.000}° · 잰 간격 {measured:F2}mm (설계 {designed:F2}mm)" +
                (within ? $" · 허용 오차 ±{lim.AngleToleranceDeg:F3}° 안 — 보정 없음" : " · 보정 필요"));
        }

        /// <summary>
        /// 마크 하나로 기준 자리에서 얼마나 벗어났는지 구한다.
        ///
        /// <para>결과를 그대로 스테이지에 더하면 마크가 기준 픽셀로 돌아온다.
        /// T 를 돌린 뒤 이것을 다시 재면 <b>회전 때문에 딸려 나간 이동까지 함께 흡수</b>되므로,
        /// 척 회전중심을 따로 교정하지 않아도 된다.</para>
        /// </summary>
        public static ShiftResult SolveShift(
            MarkReading mark, double refPxX, double refPxY,
            PixelToStage? cal, AlignLimits? limits = null)
        {
            var lim = limits ?? new AlignLimits();

            if (cal == null || !cal.IsCalibrated)
                return new ShiftResult(AlignVerdict.NotCalibrated, 0, 0, false,
                                       "µm/px 교정이 없습니다 — 교정을 먼저 하세요.");

            var bad = Check(mark, lim.MinScore, "마크");
            if (bad != null) return new ShiftResult(bad.Value.Verdict, 0, 0, false, bad.Value.Message);

            var d = cal.ToMm(refPxX - mark.PxX, refPxY - mark.PxY);
            double dist = Math.Sqrt(d.X * d.X + d.Y * d.Y);

            if (dist > lim.MaxShiftMm)
                return new ShiftResult(AlignVerdict.ShiftTooLarge, d.X, d.Y, false,
                    $"{dist:F2}mm 로 너무 많이 벗어났습니다(한계 {lim.MaxShiftMm:F1}mm) — " +
                    "글라스를 다시 놓고 시작하세요.");

            // 거리가 아니라 축별로 본다 — 축마다 허용 오차가 다르다.
            bool within = Math.Abs(d.X) <= lim.ShiftToleranceXMm
                       && Math.Abs(d.Y) <= lim.ShiftToleranceYMm;

            return new ShiftResult(AlignVerdict.Ok, d.X, d.Y, within,
                $"이동 ΔX {d.X:+0.000;-0.000} · ΔY {d.Y:+0.000;-0.000} mm" +
                (within
                    ? $" · 허용 오차 X {lim.ShiftToleranceXMm * 1000:F0} / Y {lim.ShiftToleranceYMm * 1000:F0}µm 안 — 이동 없음"
                    : " · 이동 필요"));
        }

        // ── 내부 ─────────────────────────────────────────────────────────

        private static (AlignVerdict Verdict, string Message)? Check(MarkReading m, double minScore, string who)
        {
            if (!m.Found)
                return (AlignVerdict.MarkNotFound, $"{who} 를 찾지 못했습니다.");
            if (m.Score < minScore)
                return (AlignVerdict.LowScore,
                        $"{who} 점수 {m.Score:F3} 가 합격 {minScore:F2} 에 못 미칩니다 — 엉뚱한 곳일 수 있습니다.");
            return null;
        }

        private static AngleResult Fail(AlignVerdict v, string message) => new(v, 0, 0, 0, false, message);

        /// <summary>(-180, 180] 로 접는다. 359° 를 -1° 로 읽어야 보정 방향이 맞다.</summary>
        private static double Normalize(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            if (deg <= -180.0) deg += 360.0;
            return deg;
        }
    }
}
