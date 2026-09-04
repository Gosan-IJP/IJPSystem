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
        public void 실측_교정은_잰_그대로_되돌려준다()
        {
            // FromMoves 는 "스테이지를 이만큼 밀었더니 마크가 이만큼 갔다"를 담는다.
            // 그러니 ToPx 에 같은 이동을 주면 <b>잰 픽셀이 그대로</b> 나와야 한다 —
            // 뒤집힌 부호가 필요한 자리는 없다.
            //
            // 이걸 못 지키면 교정 확인이 예측을 정반대로 잡아, 잘 잰 교정을
            // "예측과 어긋난다"며 버린다(실장 2026-08-31: 150µm 이동에 443px 어긋남).
            // 카메라가 비스듬히 달려 축이 섞인 경우로 잡았다 — 부호만 맞으면 통과하는
            // 나란한 배치로는 이 실수를 못 잡는다.
            var k = PixelToStage.FromMoves(0.300, 280.0, -95.0,
                                           0.322, 90.0, 305.0)!;

            // 두 이동을 동시에 하면 픽셀 변화도 합이다(선형).
            var (u, v) = k.ToPx(0.300, 0.322);

            Assert.Equal(280.0 + 90.0, u, 6);
            Assert.Equal(-95.0 + 305.0, v, 6);
        }

        [Fact]
        public void 실측_교정의_두_방향은_서로의_역이다()
        {
            var k = PixelToStage.FromMoves(0.300, 280.0, -95.0,
                                           0.322, 90.0, 305.0)!;

            var (u, v) = k.ToPx(0.21, -0.13);
            var (x, y) = k.ToMm(u, v);

            Assert.Equal(0.21, x, 9);
            Assert.Equal(-0.13, y, 9);
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
        /// 마크2 는 마크1 에서 +Y 쪽에 있다(배치 도면). 그것을 고정 카메라 밑으로 내리려면
        /// 글라스는 -Y 로 간다. 부호가 뒤집히면 정렬이 반대로 돌므로 규약을 못으로 박아 둔다.
        /// </summary>
        [Fact]
        public void 마크2로_갈_때_스테이지는_마이너스Y로_간다()
        {
            var move = GlassAlign.StageMoveToMark2(0, 160);
            Assert.Equal(0, move.Dx);
            Assert.Equal(-160, move.Dy);

            // 설계 간격 벡터는 그 반대 — 마크2 가 위쪽이므로 레시피 간격 그대로다.
            var sep = GlassAlign.DesignedSeparation(0, 160);
            Assert.Equal(0, sep.X);
            Assert.Equal(+160, sep.Y);
        }

        /// <summary>이동과 설계 간격은 항상 반대 부호다 — 한쪽만 뒤집으면 정렬이 반대로 돈다.</summary>
        [Theory]
        [InlineData(0, 160)]
        [InlineData(120, 0)]
        [InlineData(80, -40)]
        public void 이동과_설계간격은_항상_반대다(double px, double py)
        {
            var move = GlassAlign.StageMoveToMark2(px, py);
            var sep  = GlassAlign.DesignedSeparation(px, py);
            Assert.Equal(-move.Dx, sep.X, 9);
            Assert.Equal(-move.Dy, sep.Y, 9);
        }

        /// <summary>
        /// 마크2 이동에 <b>X 는 없다</b>(2026-08-27). 레시피에 X 간격이 잘못 들어와도 마찬가지다 —
        /// 시야가 1.4mm 뿐이라 X 로 조금만 나가도 마크가 화면 밖으로 사라지고, 그러면
        /// "못 찾았습니다"만 뜨고 원인을 짚을 수 없다.
        ///
        /// <para>X 가 고정이라야 마크2 가 화면에서 X 로 벗어난 양이 곧 기울기가 된다 —
        /// 정렬 확인이 성립하는 근거이므로 규약을 못으로 박아 둔다.</para>
        /// </summary>
        [Theory]
        [InlineData(0, 160)]
        [InlineData(120, 160)]     // X 간격이 들어와도
        [InlineData(-75.5, 160)]   // 부호가 어느 쪽이어도
        public void 마크2_이동은_X를_건드리지_않는다(double px, double py)
        {
            var move = GlassAlign.StageMoveToMark2(px, py);
            Assert.Equal(0, move.Dx);
            Assert.Equal(-py, move.Dy, 9);

            // 설계 간격도 같이 X 를 버려야 각도 계산이 이동과 어긋나지 않는다.
            var sep = GlassAlign.DesignedSeparation(px, py);
            Assert.Equal(0, sep.X);
            Assert.Equal(+py, sep.Y, 9);
        }

        /// <summary>
        /// X 를 안 움직였으므로, 반듯한 글라스는 마크2 가 <b>마크1 과 같은 화면 X</b> 에 온다.
        /// 화면 X 로 벗어난 만큼이 그대로 기울기로 나와야 정렬 확인이 성립한다.
        /// </summary>
        [Fact]
        public void 마크2가_화면X로_벗어난_만큼이_기울기다()
        {
            var cal = Cam5um();
            var m1  = At(640, 480);

            // 반듯하면 마크2 가 마크1 과 같은 화면 자리에 온다 — 회전 0.
            var straight = GlassAlign.SolveAngleFromPitch(m1, m1, 0, 160, cal);
            Assert.True(straight.Ok);
            Assert.Equal(0, straight.AngleDeg, 6);

            // 화면 X 로 벗어난 픽셀이 스테이지로 몇 mm 인지는 교정이 말해 준다.
            const int offPx = 200;
            double offMm = Math.Abs(cal.ToMm(offPx, 0).X);

            var tilted = GlassAlign.SolveAngleFromPitch(m1, At(640 + offPx, 480), 0, 160, cal);
            Assert.True(tilted.Ok);

            // 기선 160mm 에 대해 atan(벗어난 거리 / 160).
            double expected = Math.Atan2(offMm, 160.0) * 180.0 / Math.PI;
            Assert.Equal(expected, Math.Abs(tilted.AngleDeg), 4);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.2)]
        [InlineData(-0.35)]
        public void 레시피_간격만_줘도_부호가_맞는다(double deg)
        {
            var cal = Cam5um();
            var m1 = At(640, 480);
            // 실제 장비대로: 스테이지 -Y 160, 설계 간격은 (0,+160).
            var m2 = Mark2For(deg, 0, +160, 0, -160, m1);
            var direct = GlassAlign.SolveAngle(m1, m2, 0, -160, 0, +160, cal);
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

    /// <summary>
    /// 사양값 교정 — 렌즈 사양 µm/px 만으로 정렬을 돌릴 수 있는가.
    ///
    /// <para>사양은 <b>크기</b>를 말하고 설치가 <b>방향</b>을 정한다. 그 둘이 모이면 교정 마법사
    /// 없이도 픽셀을 mm 로 바꿀 수 있다 — 여기서 확인하는 것은 방향까지 맞느냐다.
    /// 방향이 틀리면 보정이 오차를 두 배로 키우므로, 그것을 스스로 알아채는지도 함께 본다.</para>
    /// </summary>
    public class NominalCalibrationTests
    {
        // 10호기 글라스 카메라 사양.
        private const double Spec = 1.125;

        [Theory]
        [InlineData("+X", StageAxisDir.PlusX)]
        [InlineData("-X", StageAxisDir.MinusX)]
        [InlineData("+Y", StageAxisDir.PlusY)]
        [InlineData("-Y", StageAxisDir.MinusY)]
        [InlineData("x",  StageAxisDir.PlusX)]      // 부호 없으면 +
        [InlineData(" -y ", StageAxisDir.MinusY)]   // 설정 파일에 공백이 섞여도
        public void 방향_문자열을_읽는다(string text, StageAxisDir expected)
        {
            Assert.True(StageAxis.TryParse(text, out var dir));
            Assert.Equal(expected, dir);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Z")]
        [InlineData("위쪽")]
        public void 못_읽는_방향은_짐작하지_않는다(string? text)
        {
            // 여기서 기본값을 정해 버리면 아무도 확인하지 않은 방향으로 모터가 나간다.
            Assert.False(StageAxis.TryParse(text, out _));
        }

        [Fact]
        public void 사양_분해능만으로_교정이_선다()
        {
            var k = PixelToStage.FromNominal(Spec, StageAxisDir.PlusX, StageAxisDir.MinusY)!;

            Assert.True(k.IsCalibrated);
            Assert.True(k.IsNominal);                       // 실측이 아니라는 표시가 남아야 한다
            Assert.Equal(Spec, k.MicronPerPxX, 9);
            Assert.Equal(Spec, k.MicronPerPxY, 9);
            Assert.True(k.MatchesNominal(Spec));
        }

        [Fact]
        public void 방향이_그대로_행렬에_들어간다()
        {
            // u 는 기계 +X, v 는 기계 -Y 로 달렸다고 적은 경우.
            var k = PixelToStage.FromNominal(Spec, StageAxisDir.PlusX, StageAxisDir.MinusY)!;

            var byU = k.ToMm(1, 0);
            Assert.Equal( Spec / 1000.0, byU.X, 12);
            Assert.Equal( 0.0,           byU.Y, 12);

            var byV = k.ToMm(0, 1);
            Assert.Equal( 0.0,           byV.X, 12);
            Assert.Equal(-Spec / 1000.0, byV.Y, 12);
        }

        [Fact]
        public void 두_축을_같게_적으면_교정이_서지_않는다()
        {
            // u 와 v 가 같은 방향이면 화면 두 축을 구분할 수 없다 — 여기서 막지 않으면
            // 특이행렬로 계산해 엉뚱한 이동이 나온다.
            Assert.Null(PixelToStage.FromNominal(Spec, StageAxisDir.PlusX, StageAxisDir.PlusX));
            Assert.Null(PixelToStage.FromNominal(Spec, StageAxisDir.PlusX, StageAxisDir.MinusX));
        }

        [Fact]
        public void 사양이_비어_있으면_교정이_서지_않는다()
        {
            Assert.Null(PixelToStage.FromNominal(0, StageAxisDir.PlusX, StageAxisDir.MinusY));
        }

        [Fact]
        public void 사양값_교정으로도_이동량이_바로_나온다()
        {
            // 마크가 기준보다 화면에서 오른쪽·아래로 100px 씩 가 있다.
            var k = PixelToStage.FromNominal(Spec, StageAxisDir.PlusX, StageAxisDir.MinusY)!;
            var res = GlassAlign.SolveShift(new MarkReading(true, 0.9, 740, 580), 640, 480, k);

            Assert.True(res.Ok);
            Assert.Equal(-0.1125, res.DxMm, 9);   // 100px × 1.125µm
            Assert.Equal( 0.1125, res.DyMm, 9);   // v 가 -Y 라 부호가 뒤집힌다
        }

        // ── 방향이 반대일 때 ─────────────────────────────────────────────

        [Fact]
        public void 오차가_줄면_계속한다()
        {
            var p = GlassAlign.CheckProgress(100, 4);

            Assert.Equal(ProgressVerdict.Improved, p.Verdict);
            Assert.True(p.Ok);
        }

        [Fact]
        public void 오차가_늘면_멈춘다()
        {
            // 방향이 반대면 나타나는 증상이 이것이다. 되풀이할수록 벌어지므로 여기서 멈춰야 한다.
            var p = GlassAlign.CheckProgress(100, 200);

            Assert.Equal(ProgressVerdict.Diverged, p.Verdict);
            Assert.False(p.Ok);
            Assert.Contains("PixelUAxis", p.Message);   // 어디를 고쳐야 하는지까지 말한다
        }

        [Fact]
        public void 덜_줄면_멈추지는_않고_짚어만_준다()
        {
            // 배율이 조금 어긋난 경우 — 되풀이하면 들어온다. 여기서 세우면 쓸 수 있는 글라스를 버린다.
            var p = GlassAlign.CheckProgress(100, 70);

            Assert.Equal(ProgressVerdict.Stalled, p.Verdict);
            Assert.True(p.Ok);
        }

        [Fact]
        public void 이미_맞아_있으면_그대로_통과한다()
        {
            // 허용 오차 안이라 아무것도 움직이지 않은 판 — 잡음만큼의 차이를 실패로 읽으면 안 된다.
            var p = GlassAlign.CheckProgress(1.0, 1.4);

            Assert.Equal(ProgressVerdict.Improved, p.Verdict);
        }

        // ── T 축의 + 방향 ────────────────────────────────────────────────

        [Theory]
        [InlineData("CW",     RotationSense.Clockwise)]
        [InlineData("cw",     RotationSense.Clockwise)]
        [InlineData("시계",   RotationSense.Clockwise)]
        [InlineData("CCW",    RotationSense.CounterClockwise)]
        [InlineData("반시계", RotationSense.CounterClockwise)]
        public void 회전_방향_문자열을_읽는다(string text, RotationSense expected)
        {
            Assert.True(StageAxis.TryParseRotation(text, out var sense));
            Assert.Equal(expected, sense);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("오른쪽")]
        public void 못_읽는_회전_방향은_짐작하지_않는다(string? text)
        {
            Assert.False(StageAxis.TryParseRotation(text, out _));
        }

        [Fact]
        public void T가_시계방향_플러스면_명령_부호가_뒤집힌다()
        {
            // 잰 각은 화면 좌표(오른쪽 +X, 위쪽 +Y)에서 재므로 반시계가 + 다.
            // 10호기는 T 의 + 가 시계방향이라, +0.3° 기울어진 글라스는 T 를 +0.3° 줘야 펴진다.
            Assert.Equal(0.3, GlassAlign.RotationCommand(0.3, RotationSense.Clockwise), 12);
            Assert.Equal(-0.3, GlassAlign.RotationCommand(-0.3, RotationSense.Clockwise), 12);
        }

        [Fact]
        public void T가_반시계_플러스면_명령이_잰_각의_반대다()
        {
            Assert.Equal(-0.3, GlassAlign.RotationCommand(0.3, RotationSense.CounterClockwise), 12);
            Assert.Equal(0.3, GlassAlign.RotationCommand(-0.3, RotationSense.CounterClockwise), 12);
        }

        [Fact]
        public void 두_방향의_명령은_언제나_반대다()
        {
            // 한쪽만 고치는 일을 막는다 — 부호를 다루는 자리가 여기 하나뿐이어야 한다.
            foreach (double a in new[] { -1.5, -0.01, 0.0, 0.02, 1.9 })
                Assert.Equal(GlassAlign.RotationCommand(a, RotationSense.Clockwise),
                             -GlassAlign.RotationCommand(a, RotationSense.CounterClockwise), 12);
        }
    }

    /// <summary>
    /// 가상 모드 — 카메라 없이 정렬 한 판이 실제로 <b>수렴하는가</b>.
    ///
    /// <para>가상 글라스는 실제와 같은 규칙으로 마크 자리를 낸다. 그래서 이 시험은 계산이
    /// 스스로와 아귀가 맞는지가 아니라, <b>보정을 하면 오차가 줄어드는지</b>를 본다 —
    /// 부호를 하나라도 뒤집으면 여기서 벌어진다.</para>
    /// </summary>
    public class VirtualGlassAlignTests
    {
        private const double Spec = 1.125;
        private const double PitchX = 0.0;
        private const double PitchY = 160.0;

        /// <summary>실제 광학에서 나온 거절선 — 시야가 1.44 × 1.15mm 뿐이라 한계가 아주 좁다.</summary>
        private static AlignLimits Lim() => AlignLimits.ForCamera(Spec, 1280, 1024, PitchY);

        private static PixelToStage Cal() =>
            PixelToStage.FromNominal(Spec, StageAxisDir.PlusX, StageAxisDir.MinusY)!;

        /// <summary>정렬 한 판을 그대로 돌린다 — 시퀀스가 부르는 순서와 같다.</summary>
        private static (double AngleAfterDeg, double ShiftXMm, double ShiftYMm, int Passes) RunAlign(
            VirtualGlass glass, RotationSense tSense, AlignLimits lim)
        {
            var cal = Cal();
            double refX = 640, refY = 512;
            double sx = 0, sy = 0, t = 0;

            MarkReading M(int slot) => glass.Mark(slot, sx, sy, t, tSense, PitchX, PitchY, cal, refX, refY);

            // ① 마크1 → ② 마크2(+피듀셜 간격) → ③ 각도
            var m1 = M(1);
            var move = GlassAlign.StageMoveToMark2(PitchX, PitchY);
            sx += move.Dx; sy += move.Dy;
            var m2 = M(2);

            var angle = GlassAlign.SolveAngleFromPitch(m1, m2, PitchX, PitchY, cal, lim);
            Assert.True(angle.Ok, angle.Message);

            // ④ T 보정 → ⑤ 마크1 복귀
            t += GlassAlign.RotationCommand(angle.AngleDeg, tSense);
            sx -= move.Dx; sy -= move.Dy;

            // ⑥ X·Y 보정을 허용 오차까지 되풀이
            int passes = 0;
            ShiftResult shift = default;
            for (int i = 1; i <= lim.MaxPasses; i++)
            {
                passes = i;
                shift = GlassAlign.SolveShift(M(1), refX, refY, cal, lim);
                Assert.True(shift.Ok, shift.Message);
                sx += shift.DxMm; sy += shift.DyMm;

                shift = GlassAlign.SolveShift(M(1), refX, refY, cal, lim);
                if (shift.WithinTolerance) break;
            }

            // ⑦ 마크2 로 다시 가서 각도 재확인
            var last1 = M(1);
            sx += move.Dx; sy += move.Dy;
            var after = GlassAlign.SolveAngleFromPitch(last1, M(2), PitchX, PitchY, cal, lim);
            Assert.True(after.Ok, after.Message);

            return (after.AngleDeg, shift.DxMm, shift.DyMm, passes);
        }

        [Fact]
        public void 가상_글라스가_허용_오차_안으로_들어온다()
        {
            var lim = Lim();
            var r = RunAlign(new VirtualGlass(), RotationSense.Clockwise, lim);

            Assert.True(Math.Abs(r.AngleAfterDeg) <= lim.AngleToleranceDeg,
                        $"회전이 남았다: {r.AngleAfterDeg:F4}°");
            Assert.True(Math.Abs(r.ShiftXMm) <= lim.ShiftToleranceXMm, $"X 가 남았다: {r.ShiftXMm:F4}mm");
            Assert.True(Math.Abs(r.ShiftYMm) <= lim.ShiftToleranceYMm, $"Y 가 남았다: {r.ShiftYMm:F4}mm");
        }

        [Fact]
        public void 한두_번이면_들어온다()
        {
            // 상한(3회)을 다 쓰면 뭔가 어긋난 것이다 — 계산이 맞으면 첫 판에 들어온다.
            var r = RunAlign(new VirtualGlass(), RotationSense.Clockwise, Lim());

            Assert.True(r.Passes <= 2, $"{r.Passes}번 걸렸다");
        }

        [Fact]
        public void 회전중심이_어디든_결과가_같다()
        {
            // 회전 뒤 마크1 을 다시 재기 때문에 회전중심을 몰라도 된다 — 그 주장을 여기서 건다.
            var lim = Lim();

            foreach (var c in new[] { (0.0, 0.0), (12.0, -8.0), (-40.0, 25.0) })
            {
                var g = new VirtualGlass { ChuckCenterXMm = c.Item1, ChuckCenterYMm = c.Item2 };
                var r = RunAlign(g, RotationSense.Clockwise, lim);

                Assert.True(Math.Abs(r.AngleAfterDeg) <= lim.AngleToleranceDeg);
                Assert.True(Math.Abs(r.ShiftXMm) <= lim.ShiftToleranceXMm);
                Assert.True(Math.Abs(r.ShiftYMm) <= lim.ShiftToleranceYMm);
            }
        }

        [Fact]
        public void T_방향을_반대로_잡으면_더_기운다()
        {
            // 설정이 틀렸을 때 조용히 통과하면 안 된다 — 기울기가 두 배가 되어 드러나야 한다.
            var glass = new VirtualGlass { RotationDeg = 0.05 };
            var lim = Lim();

            // 실제 축은 시계(+)인데 설정을 반시계로 잘못 적은 경우를 흉내낸다.
            var cal = Cal();
            double refX = 640, refY = 512, sx = 0, sy = 0, t = 0;
            MarkReading M(int slot) =>
                glass.Mark(slot, sx, sy, t, RotationSense.Clockwise, PitchX, PitchY, cal, refX, refY);

            var m1 = M(1);
            var move = GlassAlign.StageMoveToMark2(PitchX, PitchY);
            sx += move.Dx; sy += move.Dy;
            var before = GlassAlign.SolveAngleFromPitch(m1, M(2), PitchX, PitchY, cal, lim);

            t += GlassAlign.RotationCommand(before.AngleDeg, RotationSense.CounterClockwise);   // 반대로 적음
            sx -= move.Dx; sy -= move.Dy;

            var m1b = M(1);
            sx += move.Dx; sy += move.Dy;
            var after = GlassAlign.SolveAngleFromPitch(m1b, M(2), PitchX, PitchY, cal, lim);

            Assert.True(Math.Abs(after.AngleDeg) > Math.Abs(before.AngleDeg),
                        $"{before.AngleDeg:F3}° → {after.AngleDeg:F3}° — 더 기울지 않았다");
        }



        [Fact]
        public void 마크가_기준_자리_그대로면_아무것도_움직이지_않는다()
        {
            // 가상 운전은 마크 읽기를 건너뛰고 "기준 자리 그대로"라고 답한다.
            // 그때 스테이지가 조금이라도 움직이면 가상에서 엉뚱한 이동을 만드는 셈이다.
            var cal = Cal();
            var at  = new MarkReading(true, 1.0, 640, 512);

            var angle = GlassAlign.SolveAngleFromPitch(at, at, PitchX, PitchY, cal, Lim());
            Assert.True(angle.Ok, angle.Message);
            Assert.Equal(0.0, angle.AngleDeg, 9);
            Assert.False(angle.NeedsRotation);

            var shift = GlassAlign.SolveShift(at, 640, 512, cal, Lim());
            Assert.True(shift.Ok, shift.Message);
            Assert.Equal(0.0, shift.DxMm, 9);
            Assert.Equal(0.0, shift.DyMm, 9);
            Assert.False(shift.NeedsMove);
        }
        [Fact]
        public void 거절선은_카메라_시야에서_나온다()
        {
            // 1.125µm/px · 1280×1024 → 시야 1.44 × 1.15mm. 손으로 적었던 2° 는 기선 160mm 에서
            // 마크2 를 5.6mm(≈5000px) 밀어낸다 — 화면에 있을 수가 없는 값이었다.
            var lim = AlignLimits.ForCamera(Spec, 1280, 1024, 160);

            Assert.InRange(lim.MaxShiftMm, 0.60, 0.80);
            Assert.InRange(lim.MaxAngleDeg, 0.10, 0.20);

            // 한계까지 기울어도 마크2 는 화면 안이어야 한다 — 그러라고 시야에서 뽑았다.
            double markShiftMm = 160 * Math.Sin(lim.MaxAngleDeg * Math.PI / 180);
            Assert.True(markShiftMm <= 1024 * Spec / 2000.0, $"{markShiftMm:F3}mm 는 화면 밖이다");
        }

        [Fact]
        public void 화면에서_찾을_수_있는_어긋남은_거절하지_않는다()
        {
            // 어긋남 한계는 마크를 <b>이미 찾은 뒤</b>에 걸린다. 찾았다는 것은 화면 안에
            // 있다는 뜻이고, 기준으로 되돌리는 이동은 마크를 시야 안쪽으로 끌어오는 것이라
            // 잃어버릴 수가 없다 — 볼 수 있으면 고칠 수 있다.
            //
            // 예전에는 각도와 같은 75% 를 써서, 화면에 멀쩡히 보이는 마크를 두고
            // "너무 많이 벗어났습니다 — 글라스를 다시 놓으세요"로 세웠다(실장 2026-09-01).
            var lim = AlignLimits.ForCamera(Spec, 1280, 1024, 150);

            // 화면 안에서 기준으로부터 가장 멀리 떨어질 수 있는 거리 = 긴 변의 반.
            double farthestInFrameMm = 1280 * Spec / 2000.0;

            Assert.True(lim.MaxShiftMm >= farthestInFrameMm,
                $"화면 안 {farthestInFrameMm:F3}mm 까지 보이는데 한계가 {lim.MaxShiftMm:F3}mm 라 " +
                "보이는 마크를 거절한다");
        }

        /// <summary>
        /// 허용 각까지 돌아 있는 판을 고쳤을 때 딸려 나가는 이동이 <b>어긋남 한계 안</b>이어야 한다.
        ///
        /// <para>아니면 각도를 고칠수록 뒤 단계가 막힌다 — 실제로 그랬다(2026-08-28 11:16,
        /// 0.103° 판이 회전 뒤 0.217mm 밀려 한계 0.192mm 를 넘었다). 두 한계를 절반씩
        /// 쪼개 두면 회전반경이 기선보다 짧아도 반드시 걸린다.</para>
        /// </summary>
        [Theory]
        [InlineData(121.0, 150.0)]   // 10호기 실측 — 회전반경 121mm · 기선 150mm
        [InlineData(121.0, 160.0)]
        [InlineData(149.0, 150.0)]   // 회전중심이 기선만큼 멀어도 아슬아슬하게 든다
        public void 허용_각을_고쳐도_어긋남_한계_안이다(double chuckRadiusMm, double baselineMm)
        {
            var lim = AlignLimits.ForCamera(Spec, 1280, 1024, baselineMm);

            // T 를 한계각만큼 돌리면 마크는 회전반경 × θ 만큼 딸려 나간다.
            double pulledMm = chuckRadiusMm * lim.MaxAngleDeg * Math.PI / 180.0;

            Assert.True(pulledMm <= lim.MaxShiftMm,
                $"한계각 {lim.MaxAngleDeg:F3}° 를 고치면 {pulledMm:F3}mm 밀리는데 " +
                $"어긋남 한계는 {lim.MaxShiftMm:F3}mm 뿐이다 — 12단계가 스스로 막힌다");
        }

        [Fact]
        public void 시야가_넓으면_한계도_넓어진다()
        {
            var tight = AlignLimits.ForCamera(Spec, 1280, 1024, 160);
            var wide  = AlignLimits.ForCamera(Spec * 4, 1280, 1024, 160);

            Assert.True(wide.MaxShiftMm  > tight.MaxShiftMm);
            Assert.True(wide.MaxAngleDeg > tight.MaxAngleDeg);
        }

        [Fact]
        public void 기선이_길수록_허용_각이_좁아진다()
        {
            // 같은 시야라도 마크가 멀리 있으면 조금만 돌아도 화면 밖으로 나간다.
            var near = AlignLimits.ForCamera(Spec, 1280, 1024, 40);
            var far  = AlignLimits.ForCamera(Spec, 1280, 1024, 160);

            Assert.True(far.MaxAngleDeg < near.MaxAngleDeg);
        }

        [Fact]
        public void 사양이_없으면_기본_한계를_그대로_둔다()
        {
            var lim = AlignLimits.ForCamera(0, 0, 0, 160);
            var def = new AlignLimits();

            Assert.Equal(def.MaxShiftMm, lim.MaxShiftMm, 9);
            Assert.Equal(def.MaxAngleDeg, lim.MaxAngleDeg, 9);
        }
        [Fact]
        public void 화면_밖으로_나가면_못_찾은_것으로_본다()
        {
            // 가상에서만 되는 정렬이 되면 안 된다 — 실제로 못 볼 자리는 여기서도 못 본다.
            var glass = new VirtualGlass { OffsetXMm = 50 };

            Assert.False(glass.Mark(1, 0, 0, 0, RotationSense.Clockwise,
                                    PitchX, PitchY, Cal(), 640, 512).Found);
        }

        [Fact]
        public void 교정이_없으면_아무것도_내지_않는다()
        {
            Assert.False(new VirtualGlass().Mark(1, 0, 0, 0, RotationSense.Clockwise,
                                                 PitchX, PitchY, new PixelToStage(), 640, 512).Found);
        }
    }
}
