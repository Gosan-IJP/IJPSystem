using System;

namespace IJPSystem.Platform.Infrastructure.Vision
{
    /// <summary>
    /// 화면 한 축이 기계에서 가리키는 방향. 카메라를 어느 쪽으로 돌려 달았는지를 적는 값이다.
    ///
    /// <para>사양 µm/px 은 <b>크기</b>만 말한다. 화면 오른쪽이 기계 +X 인지 -Y 인지는
    /// 설치가 정하는 값이라 설정으로 받는다 — 코드가 짐작하면 보정이 반대로 나간다.</para>
    /// </summary>
    public enum StageAxisDir { PlusX, MinusX, PlusY, MinusY }

    /// <summary>설정 문자열("+X" 같은)과 <see cref="StageAxisDir"/> 사이.</summary>
    public static class StageAxis
    {
        /// <summary>"+X" "-X" "+Y" "-Y" (부호 없으면 +). 못 읽으면 false — 짐작하지 않는다.</summary>
        public static bool TryParse(string? text, out StageAxisDir dir)
        {
            dir = StageAxisDir.PlusX;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim().Replace(" ", "").ToUpperInvariant();
            bool minus = s.StartsWith("-");
            if (minus || s.StartsWith("+")) s = s.Substring(1);

            if (s == "X") { dir = minus ? StageAxisDir.MinusX : StageAxisDir.PlusX; return true; }
            if (s == "Y") { dir = minus ? StageAxisDir.MinusY : StageAxisDir.PlusY; return true; }
            return false;
        }

        /// <summary>"CW"/"CCW"(또는 시계/반시계). 못 읽으면 false — 여기서 기본값을 정하면
        /// 아무도 확인하지 않은 방향으로 T 가 돈다.</summary>
        public static bool TryParseRotation(string? text, out RotationSense sense)
        {
            sense = RotationSense.CounterClockwise;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim().Replace(" ", "").ToUpperInvariant();
            if (s is "CW" or "시계" or "시계방향")     { sense = RotationSense.Clockwise;        return true; }
            if (s is "CCW" or "반시계" or "반시계방향") { sense = RotationSense.CounterClockwise; return true; }
            return false;
        }

        /// <summary>단위 벡터(기계 X, Y).</summary>
        public static (double X, double Y) Vector(StageAxisDir dir) => dir switch
        {
            StageAxisDir.PlusX  => ( 1,  0),
            StageAxisDir.MinusX => (-1,  0),
            StageAxisDir.PlusY  => ( 0,  1),
            _                   => ( 0, -1),
        };
    }

    /// <summary>
    /// T 축의 + 가 어느 쪽으로 도는가 — <b>카메라 화면에서 본</b> 방향.
    ///
    /// <para>정렬 계산이 내는 각도는 기계 XY(화면 오른쪽 +X, 화면 위쪽 +Y)에서 잰 값이라
    /// <b>반시계가 +</b> 다. 그런데 T 축의 + 가 어느 쪽인지는 모터 배선과 감속기가 정한다 —
    /// 10호기는 <b>시계방향이 +</b> 다(2026-08-26 확인). 이 둘이 어긋나면 보정이 반대로 돌아
    /// 기울기가 두 배가 되므로, 짐작하지 않고 설정에서 받는다.</para>
    /// </summary>
    public enum RotationSense { CounterClockwise, Clockwise }

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

        /// <summary>
        /// 기계에서 (dx,dy) mm 움직인 것이 화면에서 몇 픽셀인가 — <see cref="ToMm"/> 의 역이다.
        ///
        /// <para>정렬 계산은 픽셀 → mm 한 방향만 쓴다. 역방향은 <b>가상 모드에서 마크가 화면
        /// 어디에 보일지</b>를 계산할 때 필요하다. 같은 행렬을 뒤집어 쓰므로 두 방향이 어긋날
        /// 자리가 없다.</para>
        /// </summary>
        public (double U, double V) ToPx(double dxMm, double dyMm)
        {
            double det = Determinant;
            if (Math.Abs(det) < 1e-12) return (0, 0);

            return (( Kyv * dxMm - Kxv * dyMm) / det,
                    (-Kyu * dxMm + Kxu * dyMm) / det);
        }

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

        /// <summary>
        /// 사양값 교정 — 렌즈 사양 µm/px 와 <b>카메라가 어느 방향으로 달렸는지</b>로 만든다.
        ///
        /// <para><b>왜 µm/px 하나로는 안 되는가</b>: 이 행렬은 네 숫자, 곧 <i>크기 + 방향</i>이다.
        /// 사양 1.125µm/px 은 크기만 말한다 — 화면 오른쪽이 기계 +X 인지 -Y 인지는 카메라를
        /// 어느 쪽으로 돌려 달았는지에 달렸고, 그건 사양서가 아니라 장비에 적혀 있다.
        /// 방향을 반대로 잡으면 보정이 오차를 줄이는 대신 두 배로 키운다.</para>
        ///
        /// <para>그래서 방향만 설정으로 받는다(VisionConfig 의 PixelUAxis/PixelVAxis).
        /// 현장에서 X 를 조금 조그하고 마크가 화면에서 어디로 가는지 한 번 보면 정해지는 값이다.
        /// 이렇게 만든 교정은 <see cref="IsNominal"/> 이 true — <b>실측이 아니라 사양값</b>이라
        /// 렌즈 공차·작동거리 차이만큼 배율이 어긋나 있을 수 있다(그래서 한 번에 못 잡고
        /// 되풀이할 수 있다). 카메라가 비뚤게 달린 몫도 여기에는 없다.</para>
        ///
        /// <para>두 축이 같은 축이면 행렬이 특이해진다 — null 을 낸다.</para>
        /// </summary>
        public static PixelToStage? FromNominal(double micronPerPx, StageAxisDir uAxis, StageAxisDir vAxis)
        {
            if (micronPerPx <= 0) return null;

            var (ux, uy) = StageAxis.Vector(uAxis);
            var (vx, vy) = StageAxis.Vector(vAxis);
            if (Math.Abs(ux * vy - vx * uy) < 1e-9) return null;   // 같은 축을 두 번 골랐다

            double mm = micronPerPx / 1000.0;
            return new PixelToStage
            {
                Kxu = mm * ux, Kyu = mm * uy,
                Kxv = mm * vx, Kyv = mm * vy,
                IsNominal = true,
            };
        }

        /// <summary>
        /// 실측이 아니라 사양값으로 만든 교정인가.
        ///
        /// <para>계산 방법은 같지만 <b>믿는 정도가 다르다</b> — 배율이 렌즈 공차만큼 어긋나 있을 수
        /// 있어 한 번에 다 못 잡을 수 있고, 카메라 기울기는 아예 0 으로 본다. 어느 쪽을 썼는지
        /// 로그에 남기려고 들고 다닌다.</para>
        /// </summary>
        public bool IsNominal { get; set; }
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

        /// <summary>
        /// 이보다 멀리 벗어났으면 자동으로 고치지 않는다.
        ///
        /// <para>정렬 한 판에서 이 선에 걸리는 자리는 <b>회전 보정 직후</b>다 — T 는 척 회전중심을
        /// 기준으로 돌아서, 돌린 만큼 글라스가 딸려 나간다. 그 이동을 흡수하는 것이 뒤이은 X·Y
        /// 보정의 일이므로, 이 값은 <see cref="MaxAngleDeg"/> 가 허락한 회전이 만들어 낼 수 있는
        /// 이동을 덮어야 한다. 반으로 접어 두면 각도를 고칠 때마다 스스로를 막는다
        /// (실장 2026-08-28, <see cref="ForCamera"/> 참고).</para>
        /// </summary>
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

        /// <summary>
        /// 카메라 시야에서 거절선을 뽑는다 — <b>화면 밖은 잴 수 없다</b>.
        ///
        /// <para>10호기 글라스 카메라는 1.125µm/px · 1280×1024 라 시야가 <b>1.44 × 1.15mm</b> 뿐이다.
        /// 기선 160mm 에서 2° 는 마크2 를 5.6mm(≈5000px) 밀어낸다 — 화면에 있을 수가 없다.
        /// 손으로 적은 한계값이 광학과 어긋나면 "너무 많이 돌아 있습니다" 대신 "마크를 못
        /// 찾았습니다"가 뜨고, 원인이 글라스인지 조명인지 매칭인지 알 수 없게 된다.</para>
        ///
        /// <para>그래서 한계를 <b>시야에서 계산</b>한다. 가장자리는 템플릿이 잘려 매칭이 무너지므로
        /// 화면 반폭의 75%까지만 쓴다.</para>
        ///
        /// <para><b>둘은 예산을 나눠 갖지 않는다</b>(2026-08-28 에 절반씩 쪼개던 것을 고쳤다).
        /// 겹친다고 본 것이 잘못이었다 — 두 한계는 <b>서로 다른 순간, 다른 마크</b>에서 걸린다:</para>
        /// <list type="bullet">
        ///   <item><see cref="MaxAngleDeg"/> 는 <b>회전 보정 전</b> 마크2 에서. 큰 값은 기선×sinθ 다.</item>
        ///   <item><see cref="MaxShiftMm"/> 는 <b>회전 보정 뒤</b> 마크1 에서. 큰 값은 척 회전중심
        ///         때문에 딸려 나간 이동(회전반경×θ)이다 — 그걸 흡수하는 것이 그 단계의 일이다.</item>
        /// </list>
        /// <para>그래서 어긋남 한계를 반으로 접으면 <b>각도를 고칠 때마다 스스로 막는다</b>. 실제로
        /// 0.103° 짜리 판이 8단계를 통과해도 12단계에서 "너무 많이 벗어났습니다"로 섰다.</para>
        ///
        /// <para>둘 다 시야 전체(75%)를 쓰면 아귀가 맞는다 — 허용 각까지 돌아 있는 판을 고쳤을 때
        /// 딸려 나가는 이동은 <c>회전반경/기선 × 시야</c> 이므로, <b>회전중심이 기선보다 가까우면</b>
        /// 언제나 어긋남 한계 안이다. 10호기는 회전반경 ≈121mm · 기선 150mm 라 80% 만 쓴다
        /// (실측: T -0.055° 에 마크가 116px 이동 → 121mm).</para>
        /// </summary>
        /// <param name="micronPerPx">화소 크기[µm]. 0 이하면 기본값을 그대로 둔다.</param>
        /// <param name="baselineMm">두 마크 사이 거리[mm] — 기선이 길수록 허용 각이 좁아진다.</param>
        public static AlignLimits ForCamera(double micronPerPx, int widthPx, int heightPx, double baselineMm)
        {
            var lim = new AlignLimits();
            if (micronPerPx <= 0 || widthPx <= 0 || heightPx <= 0) return lim;

            // 각도용 — 짧은 변으로 잡는다. 회전을 고치면 마크1 이 딸려 나가는데, 그때도
            // 매칭이 되어야 하므로 가장자리 25% 는 버린다(템플릿이 잘린다).
            double halfShortMm = Math.Min(widthPx, heightPx) * micronPerPx / 2000.0;
            double reach       = halfShortMm * 0.75;

            if (baselineMm > 1.0)
                lim.MaxAngleDeg = Math.Asin(Math.Min(1.0, reach / baselineMm)) * 180.0 / Math.PI;

            // 어긋남용 — <b>화면 전체</b>를 쓴다. 이 한계는 마크를 <b>이미 찾은 뒤</b>에 걸린다.
            //
            // 찾았다는 것은 화면 안에 있다는 뜻이고, 그 자리를 기준으로 되돌리는 것은 마크를
            // 시야 <b>안쪽으로</b> 끌어오는 이동이라 언제나 안전하다. 잃어버릴 수가 없다.
            // 그런데 예전에는 각도와 같은 75% 를 써서, 화면에 멀쩡히 보이는 마크를 두고
            // "0.62mm 로 너무 많이 벗어났습니다 — 글라스를 다시 놓으세요"로 세웠다
            // (실장 2026-09-01). 볼 수 있으면 고칠 수 있다.
            //
            // 긴 변으로 잡는 이유: 이 검사는 반지름(√(dx²+dy²))으로 하는데, 가로로 0.6mm
            // 벗어난 것은 실제로 화면 안에 있다. 짧은 변으로 자르면 그것을 거절한다.
            //
            // 그래도 한계를 남겨 두는 이유는 <b>교정이 엉터리일 때</b>다 — µm/px 가 10배로
            // 잘못 잡히면 몇 픽셀 어긋남이 몇 mm 로 둔갑해 스테이지가 크게 나간다.
            lim.MaxShiftMm = Math.Max(widthPx, heightPx) * micronPerPx / 2000.0;

            return lim;
        }

        /// <summary>사람이 읽는 한 줄 — 화면에 왜 이 값인지 설명할 때 쓴다.</summary>
        public string Summary =>
            $"어긋남 한계 {MaxShiftMm * 1000:F0}µm · 기울기 한계 {MaxAngleDeg:F3}° " +
            $"(허용 오차 X {ShiftToleranceXMm * 1000:F0} / Y {ShiftToleranceYMm * 1000:F0}µm · {AngleToleranceDeg:F3}°)";
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

    /// <summary>보정이 실제로 먹혔는지.</summary>
    public enum ProgressVerdict
    {
        /// <summary>오차가 줄었다.</summary>
        Improved,
        /// <summary>줄긴 했으나 덜 줄었다 — 배율이 어긋나 있을 수 있다. 되풀이하면 들어온다.</summary>
        Stalled,
        /// <summary>오차가 늘었다 — 방향이 반대다. 되풀이하면 더 벌어지므로 멈춰야 한다.</summary>
        Diverged,
    }

    /// <summary>보정 전후 오차 비교 결과.</summary>
    public readonly record struct ProgressCheck(
        ProgressVerdict Verdict, double BeforePx, double AfterPx, string Message)
    {
        /// <summary>계속해도 되는가. 벌어졌으면 멈춘다.</summary>
        public bool Ok => Verdict != ProgressVerdict.Diverged;
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
        /// <para><b>마크2 는 마크1 에서 +Y 쪽(글라스 위쪽)에 있다.</b> 카메라는 고정이므로
        /// 위에 있는 마크를 렌즈로 내리려면 글라스를 <b>-Y 로</b> 밀어야 한다.
        /// 그래서 이동 부호는 레시피 간격의 반대다(10호기 배치 도면, 2026-08-27 확정).</para>
        ///
        /// <para>이 함수와 <see cref="DesignedSeparation"/> 는 항상 짝으로 쓴다 —
        /// 둘 중 하나만 부호를 뒤집으면 정렬이 반대로 돌고, 그건 모터가 반대로 나간다는 뜻이다.
        /// 짝을 놓칠 자리를 없애려고 <see cref="SolveAngleFromPitch"/> 를 따로 두었다.</para>
        ///
        /// <para><b>X 는 움직이지 않는다(2026-08-27).</b> 두 마크는 글라스에서 Y 로만 떨어져 있다 —
        /// 그래서 마크2 로 가는 이동은 순수 -Y 다. <paramref name="pitchXMm"/> 을 받고도 쓰지 않는
        /// 이유는 레시피에 값이 잘못 들어와도 <b>X 가 나가지 않게</b> 하기 위해서다. 예전에는 그 값을
        /// 그대로 이동에 썼는데, 시야가 1.4mm(1280px × 1.125µm/px)뿐이라 X 로 조금만 나가도 마크가
        /// 화면 밖으로 사라진다 — 그러면 "못 찾았습니다"만 뜨고 원인을 짚을 수 없다.</para>
        ///
        /// <para>X 가 고정이라 <b>마크2 가 화면에서 X 로 벗어난 양이 곧 글라스 기울기</b>가 된다.
        /// 눈으로도 정렬 여부를 볼 수 있다는 뜻이다.</para>
        /// </summary>
        public static (double Dx, double Dy) StageMoveToMark2(double pitchXMm, double pitchYMm)
            => (0, -pitchYMm);

        /// <summary>
        /// 설계상 마크1 → 마크2 벡터. 마크2 가 +Y 쪽이므로 레시피 간격 그대로다.
        /// <see cref="StageMoveToMark2"/> 와 정확히 반대여야 한다 — X 를 안 움직이니 여기도 X 는 없다.
        /// </summary>
        public static (double X, double Y) DesignedSeparation(double pitchXMm, double pitchYMm)
            => (0, pitchYMm);

        /// <summary>
        /// 잰 각도를 <b>T 축에 줄 값</b>으로 바꾼다.
        ///
        /// <para>잰 각 θ 는 기계 XY 에서 <b>반시계가 +</b> 다. 고치려면 물리적으로 -θ 만큼
        /// 돌려야 하는데, T 축의 + 가 시계방향이면 그 -θ 는 <b>+θ 명령</b>이 된다.
        /// 이 뒤집힘을 한 곳에 가둬 둔다 — 두 군데에서 부호를 다루면 언젠가 하나만 고친다.</para>
        ///
        /// <para>여기를 틀리면 보정이 기울기를 두 배로 만든다. 그런데 정렬 시퀀스는 회전 뒤
        /// 각도를 다시 재므로(마크2 재확인) 그 자리에서 드러난다.</para>
        /// </summary>
        public static double RotationCommand(double angleDeg, RotationSense tPositive)
            => tPositive == RotationSense.Clockwise ? angleDeg : -angleDeg;

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
                    // 한계는 시야에서 계산돼 나오므로 0.073° 같은 값이다. F1 로 찍으면 0.1°
                    // 로 반올림돼, 0.103° 가 "간발의 차로 걸렸다"처럼 읽힌다 — 실제로는 40%
                    // 넘게 벗어난 값이다(실장 2026-08-28 11:16). Summary 와 같은 자릿수로 맞춘다.
                    $"{angle:+0.000;-0.000}° 로 너무 많이 돌아 있습니다(한계 ±{lim.MaxAngleDeg:F3}°) — " +
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

        /// <summary>
        /// 보정이 오차를 줄였는지 본다 — <b>방향이 반대인지 스스로 알아내는 자리다.</b>
        ///
        /// <para>사양값(µm/px)만으로 만든 교정은 크기는 맞아도 방향이 틀릴 수 있다. 그런데 방향이
        /// 틀리면 증상이 분명하다: 보정한 뒤 오차가 <b>늘어난다</b>. 이미 찍고 있는 사진 한 장으로
        /// 그걸 잡을 수 있으니, 설정을 사람이 확인해 주기를 기다리지 않고 여기서 잡는다.</para>
        ///
        /// <para>되풀이할수록 벌어지기 때문에 한 번 벌어진 순간 멈추는 것이 중요하다 —
        /// 첫 보정 한 번(허용된 최대 이동 안쪽)으로 값을 치르고 끝난다.</para>
        /// </summary>
        /// <param name="beforePx">보정 전, 기준 자리에서 벗어난 픽셀 거리.</param>
        /// <param name="afterPx">보정 뒤 같은 값.</param>
        /// <param name="noisePx">이 정도 차이는 측정 잡음으로 본다.</param>
        public static ProgressCheck CheckProgress(double beforePx, double afterPx, double noisePx = 2.0)
        {
            if (afterPx > beforePx + noisePx)
                return new ProgressCheck(ProgressVerdict.Diverged, beforePx, afterPx,
                    $"보정 뒤 오차가 늘었습니다({beforePx:F1} → {afterPx:F1}px) — " +
                    "화면 축 방향(VisionConfig 의 PixelUAxis/PixelVAxis)이 반대일 수 있습니다.");

            if (beforePx > noisePx && afterPx > beforePx * 0.5)
                return new ProgressCheck(ProgressVerdict.Stalled, beforePx, afterPx,
                    $"오차가 덜 줄었습니다({beforePx:F1} → {afterPx:F1}px) — " +
                    "사양값 교정이면 배율이 어긋나 있을 수 있습니다(실측 교정 권장).");

            return new ProgressCheck(ProgressVerdict.Improved, beforePx, afterPx,
                $"오차 {beforePx:F1} → {afterPx:F1}px");
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
