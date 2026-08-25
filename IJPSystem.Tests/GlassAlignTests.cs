using System;
using IJPSystem.Platform.Infrastructure.Vision;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 글라스 자동 정렬 계산.
    ///
    /// <para>여기서 나온 숫자가 그대로 T·X·Y 이동 명령이 된다. 그래서 확인하는 것은
    /// "값이 나온다"가 아니라 <b>부호가 맞는가</b>와 <b>못 믿을 때 멈추는가</b>다 —
    /// 엉뚱하게 잡은 매칭으로 모터가 나가는 것을 막는 자리가 이 계산이다.</para>
    /// </summary>
    public class GlassAlignTests
    {
        // 시험용 카메라: 화면 축과 기계 축이 나란하고 1px = 5µm.
        private static PixelToStage Cam5um() =>
            PixelToStage.FromMoves(10.0, 2000, 0, 10.0, 0, 2000)!;

        private static MarkReading At(double x, double y, double score = 0.95)
            => new(true, score, x, y);

        // ── 교정 ─────────────────────────────────────────────────────────

        [Fact]
        public void 축을_10mm_움직여_2000픽셀이면_1픽셀은_5마이크론이다()
        {
            var cal = Cam5um();

            Assert.True(cal.IsCalibrated);
            Assert.Equal(5.0, cal.MicronPerPxX, 6);
            Assert.Equal(5.0, cal.MicronPerPxY, 6);
            Assert.Equal(0.0, cal.CameraAngleDeg, 6);
        }

        [Fact]
        public void 카메라가_90도_돌아_붙었어도_교정이_그대로_담는다()
        {
            // +X 로 움직였는데 마크가 화면에서 아래로 갔다 = 카메라가 90° 돌아 있다.
            var cal = PixelToStage.FromMoves(10.0, 0, 2000, 10.0, -2000, 0)!;

            Assert.True(cal.IsCalibrated);
            Assert.Equal(5.0, cal.MicronPerPxX, 6);

            // 화면 u 축 +1px 이 기계 -Y 를 향한다 = -90°.
            Assert.Equal(-90.0, cal.CameraAngleDeg, 6);

            // 화면 세로 1px 아래 = 기계 X +5µm.
            var mm = cal.ToMm(0, 1);
            Assert.Equal(0.005, mm.X, 9);
            Assert.Equal(0.000, mm.Y, 9);
        }

        [Fact]
        public void 두_교정_이동이_같은_방향이면_교정이_서지_않는다()
        {
            // 이동이 평행하면 두 축을 구분할 수 없다 — 마크를 놓쳤거나 축이 안 움직인 경우다.
            Assert.Null(PixelToStage.FromMoves(10, 2000, 0, 10, 1000, 0));
            Assert.Null(PixelToStage.FromMoves(10, 0, 0, 10, 0, 0));
        }

        [Fact]
        public void 교정이_없으면_아무_계산도_하지_않는다()
        {
            var none = new PixelToStage();
            Assert.False(none.IsCalibrated);

            var a = GlassAlign.SolveAngle(At(0, 0), At(0, 0), 0, -160, 0, 160, none);
            Assert.Equal(AlignVerdict.NotCalibrated, a.Verdict);

            var s = GlassAlign.SolveShift(At(0, 0), 0, 0, null);
            Assert.Equal(AlignVerdict.NotCalibrated, s.Verdict);
        }

        // ── 각도 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 글라스가 <paramref name="deg"/> 만큼 돌아 있을 때 마크2 가 찍힐 픽셀.
        ///
        /// <para>m = -ΔS + K·Δp 를 뒤집어 Δp 를 만든다. 실제 장비에서 나올 값을 흉내 내는 것이라,
        /// 계산식을 그대로 되풀이하지 않고 이 방향으로만 쓴다.</para>
        /// </summary>
        private static MarkReading Mark2For(double deg, double pitchXMm, double pitchYMm,
                                            double stageDxMm, double stageDyMm,
                                            MarkReading mark1, double mmPerPx = 0.005)
        {
            double r = deg * Math.PI / 180.0;
            double mx = pitchXMm * Math.Cos(r) - pitchYMm * Math.Sin(r);
            double my = pitchXMm * Math.Sin(r) + pitchYMm * Math.Cos(r);

            // K·Δp = m + ΔS
            double du = (mx + stageDxMm) / mmPerPx;
            double dv = (my + stageDyMm) / mmPerPx;
            return At(mark1.PxX + du, mark1.PxY + dv);
        }

        [Fact]
        public void 똑바로_놓였으면_회전은_0이다()
        {
            var cal = Cam5um();
            var m1 = At(640, 480);

            // 마크2 는 글라스 +Y 로 160mm. 그것을 카메라 밑으로 데려오려면 스테이지는 -Y 로 간다.
            var m2 = Mark2For(0, 0, 160, 0, -160, m1);

            var r = GlassAlign.SolveAngle(m1, m2, 0, -160, 0, 160, cal);

            Assert.True(r.Ok);
            Assert.Equal(0.0, r.AngleDeg, 9);
            Assert.Equal(160.0, r.MeasuredPitchMm, 6);
            Assert.Equal(640, m2.PxX, 6);   // 똑바르면 마크2 는 마크1 과 같은 픽셀에 온다
            Assert.Equal(480, m2.PxY, 6);
        }

        [Theory]
        [InlineData(0.10)]
        [InlineData(-0.10)]
        [InlineData(1.50)]
        [InlineData(-1.50)]
        public void 돌아간_만큼_그대로_되나온다(double deg)
        {
            var cal = Cam5um();
            var m1 = At(640, 480);
            var m2 = Mark2For(deg, 0, 160, 0, -160, m1);

            var r = GlassAlign.SolveAngle(m1, m2, 0, -160, 0, 160, cal);

            Assert.True(r.Ok);
            Assert.Equal(deg, r.AngleDeg, 6);
            Assert.Equal(160.0, r.MeasuredPitchMm, 4);   // 회전해도 두 마크 거리는 그대로다
        }

        [Fact]
        public void 마크가_대각으로_놓여도_각도가_나온다()
        {
            var cal = Cam5um();
            var m1 = At(640, 480);
            var m2 = Mark2For(0.25, 120, 160, -120, -160, m1);

            var r = GlassAlign.SolveAngle(m1, m2, -120, -160, 120, 160, cal);

            Assert.True(r.Ok);
            Assert.Equal(0.25, r.AngleDeg, 6);
            Assert.Equal(200.0, r.MeasuredPitchMm, 4);   // 3:4:5
        }

        [Fact]
        public void 픽셀_한_개_오차는_각도_0_002도_안쪽이다()
        {
            // 이 기능의 분해능 주장 — 기선 160mm, 1px=5µm 에서 픽셀 하나가 얼마인가.
            var cal = Cam5um();
            var m1 = At(640, 480);
            var m2 = Mark2For(0, 0, 160, 0, -160, m1);
            var off = At(m2.PxX + 1, m2.PxY);           // 가로로 딱 1픽셀 틀리게 잡았다

            var r = GlassAlign.SolveAngle(m1, off, 0, -160, 0, 160, cal);

            Assert.True(r.Ok);
            Assert.InRange(Math.Abs(r.AngleDeg), 0.0015, 0.0020);
        }

        // ── 각도 거절 ────────────────────────────────────────────────────

        [Fact]
        public void 간격이_레시피에_없으면_계산하지_않는다()
        {
            var r = GlassAlign.SolveAngle(At(0, 0), At(0, 0), 0, -160, 0, 0, Cam5um());

            Assert.Equal(AlignVerdict.PitchNotSet, r.Verdict);
            Assert.Contains("간격", r.Message);
        }

        [Fact]
        public void 마크를_못_찾으면_어느_쪽인지_말한다()
        {
            var cal = Cam5um();

            var a = GlassAlign.SolveAngle(MarkReading.Miss, At(0, 0), 0, -160, 0, 160, cal);
            Assert.Equal(AlignVerdict.MarkNotFound, a.Verdict);
            Assert.Contains("마크1", a.Message);

            var b = GlassAlign.SolveAngle(At(0, 0), MarkReading.Miss, 0, -160, 0, 160, cal);
            Assert.Equal(AlignVerdict.MarkNotFound, b.Verdict);
            Assert.Contains("마크2", b.Message);
        }

        [Fact]
        public void 점수가_모자라면_찾았어도_쓰지_않는다()
        {
            var cal = Cam5um();
            var m1 = At(640, 480, score: 0.42);
            var m2 = Mark2For(0, 0, 160, 0, -160, m1);

            var r = GlassAlign.SolveAngle(m1, m2, 0, -160, 0, 160, cal,
                                          new AlignLimits { MinScore = 0.70 });

            Assert.Equal(AlignVerdict.LowScore, r.Verdict);
            Assert.Contains("0.42", r.Message);
        }

        [Fact]
        public void 같은_마크를_두_번_잡으면_기선이_짧아_거절한다()
        {
            // 스테이지가 실제로 안 움직였거나 검색이 마크1 을 다시 문 경우.
            var cal = Cam5um();
            var m1 = At(640, 480);
            var m2 = At(640, 480);      // 이동을 명령했는데 픽셀이 그대로다

            var r = GlassAlign.SolveAngle(m1, m2, 0, 0, 0, 160, cal);

            Assert.Equal(AlignVerdict.BaselineTooShort, r.Verdict);
        }

        [Fact]
        public void 너무_많이_돌아_있으면_자동으로_고치지_않는다()
        {
            var cal = Cam5um();
            var m1 = At(640, 480);
            var m2 = Mark2For(5.0, 0, 160, 0, -160, m1);

            var r = GlassAlign.SolveAngle(m1, m2, 0, -160, 0, 160, cal,
                                          new AlignLimits { MaxAngleDeg = 2.0 });

            Assert.Equal(AlignVerdict.AngleTooLarge, r.Verdict);
            Assert.Equal(5.0, r.AngleDeg, 4);           // 값은 알려 준다 — 사람이 판단하도록
            Assert.Contains("다시 놓고", r.Message);
        }

        // ── 평행이동 ─────────────────────────────────────────────────────

        [Fact]
        public void 기준에서_벗어난_픽셀이_이동량이_된다()
        {
            var cal = Cam5um();

            // 기준 (500,400) 인데 지금 (600,450) 에 있다 → 100px·50px 더 간 만큼 되돌린다.
            var r = GlassAlign.SolveShift(At(600, 450), 500, 400, cal);

            Assert.True(r.Ok);
            Assert.Equal(-0.5,  r.DxMm, 9);
            Assert.Equal(-0.25, r.DyMm, 9);
        }

        [Fact]
        public void 이동량을_더하면_기준_픽셀로_돌아온다()
        {
            var cal = Cam5um();
            var r = GlassAlign.SolveShift(At(600, 450), 500, 400, cal);

            // 스테이지를 Δ 만큼 옮기면 마크는 화면에서 K⁻¹Δ 만큼 움직인다.
            double du = r.DxMm / 0.005, dv = r.DyMm / 0.005;
            Assert.Equal(500, 600 + du, 6);
            Assert.Equal(400, 450 + dv, 6);
        }

        [Fact]
        public void 제자리면_이동량은_0이다()
        {
            var r = GlassAlign.SolveShift(At(500, 400), 500, 400, Cam5um());

            Assert.True(r.Ok);
            Assert.Equal(0.0, r.DistanceMm, 12);
        }

        [Fact]
        public void 너무_많이_벗어났으면_자동으로_고치지_않는다()
        {
            var cal = Cam5um();
            var r = GlassAlign.SolveShift(At(500 + 2000, 400), 500, 400, cal,
                                          new AlignLimits { MaxShiftMm = 5.0 });

            Assert.Equal(AlignVerdict.ShiftTooLarge, r.Verdict);
            Assert.Equal(10.0, Math.Abs(r.DxMm), 6);    // 값은 알려 준다
        }

        [Fact]
        public void 못_찾았으면_이동량을_내지_않는다()
        {
            var r = GlassAlign.SolveShift(MarkReading.Miss, 500, 400, Cam5um());

            Assert.Equal(AlignVerdict.MarkNotFound, r.Verdict);
            Assert.Equal(0.0, r.DxMm);
            Assert.Equal(0.0, r.DyMm);
        }

        // ── 이동 방향 · 실제 광학계 ──────────────────────────────────────

        /// <summary>
        /// 마크2 는 마크1 에서 -Y 쪽에 있다. 그것을 고정 카메라 밑으로 올리려면 글라스는 +Y 로 간다.
        /// 부호가 뒤집히면 정렬이 반대로 돌므로 규약을 못으로 박아 둔다.
        /// </summary>
        [Fact]
        public void 마크2로_갈_때_스테이지는_플러스Y로_간다()
        {
            var move = GlassAlign.StageMoveToMark2(0, 160);
            Assert.Equal(0, move.Dx);
            Assert.Equal(+160, move.Dy);

            // 설계 간격 벡터는 그 반대 — 마크2 가 아래쪽이므로.
            var sep = GlassAlign.DesignedSeparation(0, 160);
            Assert.Equal(0, sep.X);
            Assert.Equal(-160, sep.Y);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.2)]
        [InlineData(-0.35)]
        public void 레시피_간격만_줘도_부호가_맞는다(double deg)
        {
            var cal = Cam5um();
            var m1 = At(640, 480);

            // 실제 장비대로: 스테이지 +Y 160, 설계 간격은 (0,-160).
            var m2 = Mark2For(deg, 0, -160, 0, +160, m1);

            var direct = GlassAlign.SolveAngle(m1, m2, 0, +160, 0, -160, cal);
            var byPitch = GlassAlign.SolveAngleFromPitch(m1, m2, 0, 160, cal);

            Assert.True(byPitch.Ok);
            Assert.Equal(deg, byPitch.AngleDeg, 6);
            Assert.Equal(direct.AngleDeg, byPitch.AngleDeg, 12);   // 짝을 놓칠 자리가 없다
        }

        /// <summary>10호기 글라스 카메라 실제 배율 — 1픽셀 = 1.125µm.</summary>
        private static PixelToStage Cam1125() =>
            PixelToStage.FromMoves(10.0, 10000.0 / 1.125, 0, 10.0, 0, 10000.0 / 1.125)!;

        [Fact]
        public void 실제_광학계는_1_125마이크론이고_사양_검사를_통과한다()
        {
            var cal = Cam1125();

            Assert.Equal(1.125, cal.MicronPerPxX, 6);
            Assert.Equal(1.125, cal.MicronPerPxY, 6);
            Assert.True(cal.MatchesNominal(1.125));
        }

        [Fact]
        public void 사양에서_크게_벗어난_교정은_거부한다()
        {
            // 축이 실제로는 5mm 만 갔는데 10mm 로 알고 교정하면 배율이 두 배로 나온다.
            var wrong = PixelToStage.FromMoves(10.0, 10000.0 / 2.25, 0, 10.0, 0, 10000.0 / 2.25)!;

            Assert.Equal(2.25, wrong.MicronPerPxX, 6);
            Assert.False(wrong.MatchesNominal(1.125));
            Assert.True(wrong.MatchesNominal(0));          // 사양 미입력이면 검사하지 않는다
            Assert.False(new PixelToStage().MatchesNominal(1.125));
        }

        [Fact]
        public void 실제_배율에서_픽셀_한_개는_각도_0_0004도다()
        {
            // 1.125µm / 160mm = 7.03e-6 rad. 5µm/px 일 때보다 4배 이상 곱다.
            var cal = Cam1125();
            var m1 = At(640, 480);
            var m2 = Mark2For(0, 0, -160, 0, +160, m1);
            var off = At(m2.PxX + 1, m2.PxY);

            var r = GlassAlign.SolveAngleFromPitch(m1, off, 0, 160, cal);

            Assert.True(r.Ok);
            Assert.InRange(Math.Abs(r.AngleDeg), 0.00035, 0.00045);
        }

        [Fact]
        public void 실제_배율에서_픽셀_한_개는_거리_1_125마이크론이다()
        {
            var r = GlassAlign.SolveShift(At(501, 400), 500, 400, Cam1125());

            Assert.True(r.Ok);
            Assert.Equal(0.001125, r.DistanceMm, 9);
        }

        // ── 허용 오차 ────────────────────────────────────────────────────
        //
        // Max* 는 "너무 커서 자동으로 못 고친다"는 거절선이고, Tolerance* 는 "이 정도면 됐다"는 선이다.
        // 뒤쪽이 없으면 스테이지가 못 내는 이동을 명령하고, 재보정이 끝나지 않는다.

        [Fact]
        public void 허용_오차_안이면_T를_돌리지_않는다()
        {
            var cal = Cam1125();
            var m1 = At(640, 480);
            var lim = new AlignLimits { AngleToleranceDeg = 0.010 };

            var inside = GlassAlign.SolveAngleFromPitch(
                m1, Mark2For(0.004, 0, -160, 0, +160, m1, 0.001125), 0, 160, cal, lim);
            Assert.True(inside.Ok);
            Assert.True(inside.WithinTolerance);
            Assert.False(inside.NeedsRotation);              // 건드리지 않는다
            Assert.Contains("보정 없음", inside.Message);

            var outside = GlassAlign.SolveAngleFromPitch(
                m1, Mark2For(0.030, 0, -160, 0, +160, m1, 0.001125), 0, 160, cal, lim);
            Assert.True(outside.Ok);                          // 고칠 수 있는 범위지만
            Assert.False(outside.WithinTolerance);            // 그냥 두면 안 된다
            Assert.True(outside.NeedsRotation);
            Assert.Contains("보정 필요", outside.Message);
        }

        [Fact]
        public void 허용_오차_안이면_XY를_움직이지_않는다()
        {
            var cal = Cam1125();
            var lim = new AlignLimits { ShiftToleranceXMm = 0.020, ShiftToleranceYMm = 0.020 };

            // 1픽셀 = 1.125µm. 10px = 11.25µm → 20µm 안.
            var inside = GlassAlign.SolveShift(At(510, 400), 500, 400, cal, lim);
            Assert.True(inside.WithinTolerance);
            Assert.False(inside.NeedsMove);
            Assert.Contains("이동 없음", inside.Message);

            // 30px = 33.75µm → 넘는다.
            var outside = GlassAlign.SolveShift(At(530, 400), 500, 400, cal, lim);
            Assert.True(outside.Ok);
            Assert.False(outside.WithinTolerance);
            Assert.True(outside.NeedsMove);
        }

        [Fact]
        public void 허용_오차는_축별로_따로_본다()
        {
            // 거리로 묶으면 좋은 축이 나쁜 축에 끌려간다 — X 는 빡빡하고 Y 는 헐렁한 경우.
            var cal = Cam1125();
            var lim = new AlignLimits { ShiftToleranceXMm = 0.005, ShiftToleranceYMm = 0.100 };

            // X 로 10px(11.25µm) → X 기준을 넘는다.
            Assert.False(GlassAlign.SolveShift(At(510, 400), 500, 400, cal, lim).WithinTolerance);

            // 같은 양을 Y 로만 → Y 기준 안이라 통과한다.
            Assert.True(GlassAlign.SolveShift(At(500, 410), 500, 400, cal, lim).WithinTolerance);
        }

        [Fact]
        public void 기본_허용_오차는_XY_20마이크론_T_0_01도다()
        {
            var lim = new AlignLimits();

            Assert.Equal(0.020, lim.ShiftToleranceXMm, 9);
            Assert.Equal(0.020, lim.ShiftToleranceYMm, 9);
            Assert.Equal(0.010, lim.AngleToleranceDeg, 9);
            Assert.Equal(3, lim.MaxPasses);
        }

        [Fact]
        public void 거절된_결과는_허용_오차_안이라고_말하지_않는다()
        {
            // 못 찾았는데 "오차 안"으로 읽히면 그대로 인쇄로 넘어간다.
            var cal = Cam1125();

            Assert.False(GlassAlign.SolveShift(MarkReading.Miss, 500, 400, cal).WithinTolerance);
            Assert.False(GlassAlign.SolveAngleFromPitch(
                MarkReading.Miss, At(0, 0), 0, 160, cal).WithinTolerance);
            Assert.False(GlassAlign.SolveShift(At(500, 400), 500, 400, null).WithinTolerance);
        }

        // ── 두 단계를 이어 붙인 시나리오 ─────────────────────────────────

        [Fact]
        public void 회전_보정_뒤_다시_재면_남은_어긋남만_나온다()
        {
            // ① 0.3° 돌아 있고 ② T 로 되돌린 뒤 ③ 마크1 을 다시 재니 0.8mm 남았다.
            var cal = Cam5um();
            var m1 = At(640, 480);
            var m2 = Mark2For(0.3, 0, 160, 0, -160, m1);

            var angle = GlassAlign.SolveAngle(m1, m2, 0, -160, 0, 160, cal);
            Assert.True(angle.Ok);
            Assert.Equal(-0.3, -angle.AngleDeg, 6);     // T 에 줄 값은 부호를 뒤집은 것

            // 회전 때문에 딸려 나간 이동까지 여기서 함께 잡힌다 — 척 회전중심을 몰라도 된다.
            var shift = GlassAlign.SolveShift(At(640 + 160, 480), 640, 480, cal);
            Assert.True(shift.Ok);
            Assert.Equal(-0.8, shift.DxMm, 9);
        }
    }
}
