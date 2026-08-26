using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Infrastructure.Vision;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 글라스 자동 정렬 시퀀스 — 순서와 멈추는 조건.
    ///
    /// <para>실제 카메라·모터는 <see cref="IGlassAlignService"/> 뒤에 있어 여기서는 가짜를 끼운다.
    /// 확인하려는 것은 "움직였나"가 아니라 <b>못 믿을 때 멈추는가</b>와 <b>순서가 맞는가</b>다 —
    /// 반쯤 움직인 뒤 멈추면 글라스를 다시 놔야 하고, 순서가 틀리면 엉뚱한 자리에서 찍는다.</para>
    /// </summary>
    public class GlassAlignSequenceTests
    {
        /// <summary>무엇이 어떤 순서로 불렸는지만 적는 가짜.</summary>
        private sealed class FakeAlign : IGlassAlignService
        {
            public List<string> Calls { get; } = new();
            public string? NotReadyReason { get; set; }
            public int MaxPasses { get; set; } = 3;
            public bool IsEnabled { get; set; } = true;

            /// <summary>몇 번째 검증부터 통과시킬지. 1 이면 첫 번째에 통과.</summary>
            public int VerifyOkFrom { get; set; } = 1;
            public int VerifyCount { get; private set; }

            public Task<string> MoveToMark1Async(CancellationToken ct) => Say("MoveMark1");
            public Task<string> MoveToMark2Async(CancellationToken ct) => Say("MoveMark2");
            public Task<string> MeasureAsync(int slot, CancellationToken ct) => Say("Measure" + slot);
            public Task<string> CorrectRotationAsync(CancellationToken ct) => Say("Rotate");
            public Task<string> CorrectShiftAsync(CancellationToken ct) => Say("Shift");

            /// <summary>몇 번째 회전 검증부터 통과시킬지.</summary>
            public int AngleOkFrom { get; set; } = 1;
            public int AngleVerifyCount { get; private set; }

            public Task<(bool Ok, string Message)> VerifyAngleAsync(CancellationToken ct)
            {
                Calls.Add("VerifyAngle");
                AngleVerifyCount++;
                bool ok = AngleVerifyCount >= AngleOkFrom;
                return Task.FromResult((ok, ok ? "회전 확인" : "회전 보정 뒤 더 기울었습니다"));
            }

            public Task<(bool Ok, string Message)> VerifyAsync(CancellationToken ct)
            {
                Calls.Add("Verify");
                VerifyCount++;
                bool ok = VerifyCount >= VerifyOkFrom;
                return Task.FromResult((ok, ok ? "정렬 완료" : "아직 벗어나 있습니다"));
            }

            private Task<string> Say(string what) { Calls.Add(what); return Task.FromResult(what); }
        }

        private static IReadOnlyList<SequenceStepDef> Steps(IGlassAlignService? align)
            => GlassAlignSequence.Build(null!, null!, align);

        /// <summary>모션 대기 단계는 장비가 필요하다 — 여기서는 정렬 서비스가 하는 단계만 돌린다.</summary>
        private static async Task RunAlignStepsAsync(IReadOnlyList<SequenceStepDef> steps, params int[] numbers)
        {
            foreach (int n in numbers)
                await steps.Single(s => s.Number == n).Action(CancellationToken.None);
        }

        // ── 순서 ─────────────────────────────────────────────────────────

        [Fact]
        public void 단계는_번호가_비지도_겹치지도_않는다()
        {
            var steps = Steps(new FakeAlign());

            Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(s => s.Number));
            Assert.Equal(steps.Count, steps.Select(s => s.Name).Distinct().Count());
        }

        [Fact]
        public void 마크는_이동한_뒤에_찍는다()
        {
            // 찍고 나서 옮기면 두 장이 같은 자리가 되어 각도가 0 으로 나온다.
            var names = Steps(new FakeAlign()).Select(s => s.Name).ToList();

            Assert.True(names.IndexOf("Step_GlassAlign_Mark1MoveDone") < names.IndexOf("Step_GlassAlign_Mark1Find"));
            Assert.True(names.IndexOf("Step_GlassAlign_Mark1Find")    < names.IndexOf("Step_GlassAlign_Mark2Move"));
            Assert.True(names.IndexOf("Step_GlassAlign_Mark2MoveDone") < names.IndexOf("Step_GlassAlign_Mark2Find"));
        }

        [Fact]
        public void 회전을_고친_뒤에_마크1로_돌아가_평행이동을_고친다()
        {
            // 이 순서라야 회전 때문에 딸려 나간 이동까지 함께 잡힌다 — 척 회전중심 교정이 필요 없어진다.
            var names = Steps(new FakeAlign()).Select(s => s.Name).ToList();

            Assert.True(names.IndexOf("Step_GlassAlign_Mark2Find")   < names.IndexOf("Step_GlassAlign_Rotate"));
            Assert.True(names.IndexOf("Step_GlassAlign_Rotate")      < names.IndexOf("Step_GlassAlign_Mark1Return"));
            Assert.True(names.IndexOf("Step_GlassAlign_Mark1Return") < names.IndexOf("Step_GlassAlign_Shift"));
            Assert.True(names.IndexOf("Step_GlassAlign_Shift")       < names.IndexOf("Step_GlassAlign_Verify"));
        }

        [Fact]
        public async Task 측정_순서가_마크1_마크2다()
        {
            var fake = new FakeAlign();
            var steps = Steps(fake);

            await RunAlignStepsAsync(steps, 1, 2, 4, 5, 7);

            Assert.Equal(new[] { "MoveMark1", "Measure1", "MoveMark2", "Measure2" }, fake.Calls);
        }

        // ── 멈추는 조건 ──────────────────────────────────────────────────

        [Fact]
        public async Task 준비가_안_됐으면_움직이기_전에_멈춘다()
        {
            // 반쯤 움직인 뒤 멈추면 글라스를 다시 놔야 한다.
            var fake = new FakeAlign { NotReadyReason = "µm/px 교정이 없습니다" };
            var steps = Steps(fake);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => steps[0].Action(CancellationToken.None));

            Assert.Contains("교정", ex.Message);
            Assert.Empty(fake.Calls);          // 아무것도 움직이지 않았다
        }

        [Fact]
        public async Task 서비스가_안_꽂혀_있으면_그_사실을_말한다()
        {
            // 조용히 아무것도 안 하면 "정렬했다"로 읽힌다.
            var steps = Steps(null);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => steps[0].Action(CancellationToken.None));

            Assert.Contains("연결되지 않았습니다", ex.Message);
        }

        [Fact]
        public async Task 허용_오차_안이면_검증이_통과한다()
        {
            var fake = new FakeAlign { VerifyOkFrom = 1 };
            var steps = Steps(fake);

            await steps.Single(s => s.Name == "Step_GlassAlign_Verify").Action(CancellationToken.None);

            Assert.Equal(1, fake.VerifyCount);
        }

        [Fact]
        public async Task 끝내_못_맞추면_실패로_세운다()
        {
            // 못 맞춘 글라스를 맞춘 것으로 알고 인쇄하면 안 된다.
            var fake = new FakeAlign { VerifyOkFrom = 99 };
            var steps = Steps(fake);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => steps.Single(s => s.Name == "Step_GlassAlign_Verify").Action(CancellationToken.None));

            Assert.Contains("벗어나", ex.Message);
        }


        [Fact]
        public void 마지막에_마크2로_다시_가서_각도를_잰다()
        {
            // X·Y 는 기울어진 채로도 맞출 수 있다 — 마크1 만 보고 끝내면
            // T 를 반대로 돌려 두 배로 기울어진 글라스가 "완료"로 나간다.
            var names = Steps(new FakeAlign()).Select(s => s.Name).ToList();

            Assert.True(names.IndexOf("Step_GlassAlign_Verify")           < names.IndexOf("Step_GlassAlign_Mark2Recheck"));
            Assert.True(names.IndexOf("Step_GlassAlign_Mark2Recheck")     < names.IndexOf("Step_GlassAlign_Mark2RecheckDone"));
            Assert.True(names.IndexOf("Step_GlassAlign_Mark2RecheckDone") < names.IndexOf("Step_GlassAlign_VerifyAngle"));

            // 회전 검증이 마지막이다 — 뒤에 이동이 더 있으면 잰 값이 의미를 잃는다.
            Assert.Equal("Step_GlassAlign_VerifyAngle", names[^1]);
        }

        [Fact]
        public async Task 회전이_안_펴졌으면_실패로_세운다()
        {
            var fake = new FakeAlign { AngleOkFrom = 99 };
            var steps = Steps(fake);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => steps.Single(s => s.Name == "Step_GlassAlign_VerifyAngle").Action(CancellationToken.None));

            Assert.Contains("기울", ex.Message);
        }

        [Fact]
        public async Task 회전_검증은_찍기_전에_마크2로_옮긴다()
        {
            var fake = new FakeAlign();
            var steps = Steps(fake);

            await RunAlignStepsAsync(steps, 14, 16);

            Assert.Equal(new[] { "MoveMark2", "VerifyAngle" }, fake.Calls);
        }

        // ── 인쇄 시퀀스에 끼워 넣기 ──────────────────────────────────────

        [Fact]
        public void 미사용이면_인쇄_시퀀스에_정렬_단계가_생기지_않는다()
        {
            // 단계를 내 놓고 안에서 건너뛰면 화면에는 정렬하는 것처럼 보인다.
            var fake = new FakeAlign { IsEnabled = false };

            Assert.Empty(GlassAlignSequence.Embedded(null!, fake, 4));
        }

        [Fact]
        public void 서비스가_없으면_정렬_단계를_만들지_않는다()
        {
            // 레시피 설정을 읽을 길이 없어 켜져 있는지조차 말할 수 없다.
            Assert.Empty(GlassAlignSequence.Embedded(null!, null, 4));
        }

        [Fact]
        public void 사용이면_번호를_이어받아_단계가_붙는다()
        {
            var fake = new FakeAlign { IsEnabled = true };

            var embedded = GlassAlignSequence.Embedded(null!, fake, 4);
            var whole = Steps(fake);

            Assert.Equal(whole.Count, embedded.Count);
            Assert.Equal(whole.Select(s => s.Name), embedded.Select(s => s.Name));
            // 번호가 끊기면 화면의 진행 표시가 어긋난다.
            Assert.Equal(Enumerable.Range(4, embedded.Count), embedded.Select(s => s.Number));
        }

        [Fact]
        public void 오토프린트는_진공_안정화_다음에_정렬한다()
        {
            // 진공으로 붙들기 전에 맞추면 글라스가 밀린다.
            GlassAlignServices.Current = new FakeAlign { IsEnabled = true };
            try
            {
                var names = AutoPrintSequence.Build(null!, null!).Select(s => s.Name).ToList();

                int vac   = names.IndexOf("Step_AutoPrint_VacuumStabilize");
                int align = names.IndexOf("Step_GlassAlign_Ready");
                int start = names.IndexOf("Step_AutoPrint_MoveStart");

                Assert.True(vac >= 0 && align >= 0 && start >= 0);
                Assert.Equal(vac + 1, align);       // 바로 다음 단계
                Assert.True(names.IndexOf("Step_GlassAlign_VerifyAngle") < start);
            }
            finally { GlassAlignServices.Current = null; }
        }

        [Fact]
        public void 패턴프린트는_인쇄_시작_위치_이동_앞에서_정렬한다()
        {
            // 정렬이 스테이지를 옮기므로 뒤에 두면 맞춘 자리를 인쇄 이동이 덮어쓴다.
            GlassAlignServices.Current = new FakeAlign { IsEnabled = true };
            try
            {
                var names = PatternPrintSequence.Build(null!, null!).Select(s => s.Name).ToList();

                Assert.True(names.IndexOf("Step_GlassAlign_VerifyAngle")
                          < names.IndexOf("Step_PatternPrint_MoveStart"));
                Assert.True(names.IndexOf("Step_PatternPrint_DownloadImage")
                          < names.IndexOf("Step_GlassAlign_Ready"));
            }
            finally { GlassAlignServices.Current = null; }
        }

        [Fact]
        public void 미사용이면_인쇄_시퀀스_번호가_비지_않는다()
        {
            GlassAlignServices.Current = new FakeAlign { IsEnabled = false };
            try
            {
                foreach (var steps in new[]
                {
                    AutoPrintSequence.Build(null!, null!),
                    PatternPrintSequence.Build(null!, null!),
                })
                {
                    Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(s => s.Number));
                    Assert.DoesNotContain(steps, s => s.Name.StartsWith("Step_GlassAlign_"));
                }
            }
            finally { GlassAlignServices.Current = null; }
        }

        [Fact]
        public void 사용이어도_인쇄_시퀀스_번호가_이어진다()
        {
            GlassAlignServices.Current = new FakeAlign { IsEnabled = true };
            try
            {
                foreach (var steps in new[]
                {
                    AutoPrintSequence.Build(null!, null!),
                    PatternPrintSequence.Build(null!, null!),
                })
                    Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(s => s.Number));
            }
            finally { GlassAlignServices.Current = null; }
        }

        [Fact]
        public void 애니메이션_기준_스텝은_이름으로_찾을_수_있다()
        {
            // 대시보드는 "8번이 인쇄" 같은 상수로 돌아갔다. 정렬 16단계를 앞에 넣자 통째로 밀렸다.
            // 이름으로 찾으면 어떤 구성에서도 맞는다 — 그 이름들이 실제로 있는지 여기서 건다.
            string[] anchors =
            {
                "Step_AutoPrint_MoveStart",
                "Step_AutoPrint_HeadDown",
                "Step_AutoPrint_Print",
                "Step_AutoPrint_HeadUpAndMoveReady",
            };

            foreach (bool aligning in new[] { false, true })
            {
                GlassAlignServices.Current = new FakeAlign { IsEnabled = aligning };
                try
                {
                    foreach (int swath in new[] { 1, 2, 3 })
                    foreach (bool bidi in new[] { true, false })
                    {
                        var steps = AutoPrintSequence.Build(null!, null!, swath, 120.0, bidi);
                        var names = steps.Select(s => s.Name).ToList();

                        foreach (string a in anchors)
                            Assert.True(names.Contains(a), $"{a} 없음 (swath={swath}, 양방향={bidi})");

                        // 헤드업은 패스 루프 뒤에 하나뿐이어야 한다 — 여러 개면 이름으로 못 고른다.
                        Assert.Equal(1, names.Count(n => n == "Step_AutoPrint_HeadUpAndMoveReady"));
                        Assert.Equal(1, names.Count(n => n == "Step_AutoPrint_MoveStart"));
                        Assert.Equal(1, names.Count(n => n == "Step_AutoPrint_HeadDown"));

                        // 순서: 시작이동 → 헤드다운 → 인쇄 → 헤드업.
                        Assert.True(names.IndexOf("Step_AutoPrint_MoveStart")
                                  < names.IndexOf("Step_AutoPrint_HeadDown"));
                        Assert.True(names.IndexOf("Step_AutoPrint_HeadDown")
                                  < names.IndexOf("Step_AutoPrint_Print"));
                        Assert.True(names.LastIndexOf("Step_AutoPrint_Print")
                                  < names.IndexOf("Step_AutoPrint_HeadUpAndMoveReady"));
                    }
                }
                finally { GlassAlignServices.Current = null; }
            }
        }

        [Fact]
        public void 정렬을_켜면_인쇄_스텝_번호가_밀린다()
        {
            // 밀리는 것 자체가 정상이다 — 그래서 화면이 번호를 코드에 박으면 안 된다.
            int Print(bool aligning)
            {
                GlassAlignServices.Current = new FakeAlign { IsEnabled = aligning };
                try
                {
                    return AutoPrintSequence.Build(null!, null!, 1, 0, true)
                        .Single(s => s.Name == "Step_AutoPrint_Print").Number;
                }
                finally { GlassAlignServices.Current = null; }
            }

            Assert.True(Print(true) > Print(false));
        }

        [Fact]
        public async Task 스테이지가_Y로_움직이는_단계는_넷이다()
        {
            // 대시보드 애니메이션이 실측 Y 를 그리는 구간이다. 여기 없는 단계에서 그림이 움직이면
            // 거짓말이고, 여기 있는 단계에서 안 움직이면 멈춘 것처럼 보인다.
            var fake = new FakeAlign();
            var steps = Steps(fake);

            var movers = new[]
            {
                "Step_GlassAlign_Mark1Move",     // 정렬 자리로
                "Step_GlassAlign_Mark2Move",     // +피듀셜 간격
                "Step_GlassAlign_Mark1Return",   // -피듀셜 간격
                "Step_GlassAlign_Mark2Recheck",  // +피듀셜 간격(재확인)
            };

            foreach (var name in movers)
            {
                fake.Calls.Clear();
                await steps.Single(s => s.Name == name).Action(CancellationToken.None);
                Assert.True(fake.Calls.Count == 1 && fake.Calls[0].StartsWith("MoveMark"),
                            $"{name} 가 스테이지를 움직이지 않는다");
            }

            // 나머지 정렬 단계는 찍거나 돌리거나 판정만 한다 — Y 는 가만히 있다.
            foreach (var s in steps.Where(x => x.Name.StartsWith("Step_GlassAlign_")
                                            && !movers.Contains(x.Name)))
            {
                fake.Calls.Clear();
                try { await s.Action(CancellationToken.None); } catch { /* 판정 실패는 여기 관심 밖 */ }
                Assert.DoesNotContain(fake.Calls, c => c.StartsWith("MoveMark"));
            }
        }

        [Fact]
        public void 자동_인쇄에서_Y_이동_단계의_자리()
        {
            // 번호는 참고용이다(구성이 바뀌면 바뀐다) — 확인하려는 것은 <b>순서</b>다:
            // 정렬 자리 → 마크2 → 마크1 복귀 → 마크2 재확인 → 인쇄 시작 위치.
            GlassAlignServices.Current = new FakeAlign { IsEnabled = true };
            try
            {
                var names = AutoPrintSequence.Build(null!, null!, 1, 0, true)
                    .Select(s => s.Name).ToList();

                var order = new[]
                {
                    "Step_GlassAlign_Mark1Move",
                    "Step_GlassAlign_Mark2Move",
                    "Step_GlassAlign_Mark1Return",
                    "Step_GlassAlign_Mark2Recheck",
                    "Step_AutoPrint_MoveStart",
                };

                var idx = order.Select(n => names.IndexOf(n)).ToList();
                Assert.DoesNotContain(-1, idx);
                Assert.Equal(idx.OrderBy(i => i), idx);   // 적힌 순서 그대로여야 한다
            }
            finally { GlassAlignServices.Current = null; }
        }

        [Fact]
        public void 대시보드_기본_스텝번호는_정렬_미사용_구성과_같다()
        {
            // 화면은 스텝 이름으로 번호를 찾지만, 못 찾으면 코드에 적힌 기본값으로 떨어진다.
            // 그 기본값이 실제 구성과 어긋나면 애니메이션이 조용히 멈춘다 — 실제로 그랬다.
            string view = File.ReadAllText(FindUp(@"IJPSystem.Platform.HMI\Views\Main\MainDashboardView.xaml.cs"));

            int Fallback(string field)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    view, @"private int " + field + @"\s*=\s*(\d+)\s*;");
                Assert.True(m.Success, $"{field} 기본값을 못 찾았다");
                return int.Parse(m.Groups[1].Value);
            }

            GlassAlignServices.Current = new FakeAlign { IsEnabled = false };
            try
            {
                var steps = AutoPrintSequence.Build(null!, null!, 1, 0, true);
                int Number(string name) => steps.Single(s => s.Name == name).Number;

                Assert.Equal(Number("Step_AutoPrint_MoveStart"),          Fallback("_moveStartStepNo"));
                Assert.Equal(Number("Step_AutoPrint_HeadDown"),           Fallback("_headDownStepNo"));
                Assert.Equal(Number("Step_AutoPrint_Print"),              Fallback("_printScanStepNo"));
                Assert.Equal(Number("Step_AutoPrint_HeadUpAndMoveReady"), Fallback("_headUpStepNo"));
            }
            finally { GlassAlignServices.Current = null; }
        }
        // ── 시퀀스 목록 ──────────────────────────────────────────────────

        [Fact]
        public void 시퀀스_목록에_올라가_있다()
        {
            var def = SequenceRegistry.GetAll().SingleOrDefault(d => d.Id == "GLASS_ALIGN");

            Assert.NotNull(def);
            Assert.Equal("Seq_GlassAlign_Name", def!.NameKey);
            Assert.NotEmpty(def.BuildSteps(null!, null!));
        }

        [Fact]
        public void 모든_단계_이름이_번역_파일에_있다()
        {
            // 키가 빠지면 화면에 키 이름이 그대로 뜬다.
            string ko = File.ReadAllText(FindUp(@"IJPSystem.Platform.HMI\Common\Resources\Languages\ko-KR.xaml"));

            foreach (var s in Steps(new FakeAlign()))
                Assert.Contains($"x:Key=\"{s.Name}\"", ko);

            Assert.Contains("x:Key=\"Seq_GlassAlign_Name\"", ko);
            Assert.Contains("x:Key=\"Seq_GlassAlign_Desc\"", ko);
        }

        private static string FindUp(string relative)
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, relative);
                if (File.Exists(p)) return p;
            }
            throw new FileNotFoundException(relative);
        }
    }

    /// <summary>µm/px 교정값 보관 — 없으면 정렬이 아무것도 움직이면 안 된다.</summary>
    public class AlignCalibrationStoreTests : IDisposable
    {
        private readonly string _path =
            Path.Combine(Path.GetTempPath(), "ijp_align_cal_" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose() { try { File.Delete(_path); } catch { } }

        private static PixelToStage Cam1125() =>
            PixelToStage.FromMoves(10.0, 10000.0 / 1.125, 0, 10.0, 0, 10000.0 / 1.125)!;

        [Fact]
        public void 저장한_것을_그대로_읽는다()
        {
            var k = Cam1125();
            AlignCalibrationStore.Save(
                AlignCalibration.From(k, 10, 10, new DateTime(2026, 8, 25, 17, 0, 0)), _path);

            var back = AlignCalibrationStore.Load(_path)!;

            Assert.Equal(1.125, back.ToMatrix().MicronPerPxX, 6);
            Assert.Equal(1.125, back.ToMatrix().MicronPerPxY, 6);
            Assert.Equal(new DateTime(2026, 8, 25, 17, 0, 0), back.MeasuredAt);
            Assert.Contains("µm/px", back.Note);
        }

        [Fact]
        public void 파일이_없으면_교정_전이다()
        {
            // null 이어야 정렬이 "교정 없음"으로 막힌다. 빈 값을 돌려주면 0 배율로 계산해 버린다.
            Assert.Null(AlignCalibrationStore.Load(_path));
        }

        [Fact]
        public void 깨진_파일도_교정_전으로_본다()
        {
            File.WriteAllText(_path, "{ 이건 json 이 아니다");

            Assert.Null(AlignCalibrationStore.Load(_path));
        }

        [Fact]
        public void 값이_0이면_교정_전으로_본다()
        {
            File.WriteAllText(_path, "{\"Kxu\":0,\"Kxv\":0,\"Kyu\":0,\"Kyv\":0}");

            Assert.Null(AlignCalibrationStore.Load(_path));
        }

        [Fact]
        public void 임시파일을_남기지_않는다()
        {
            AlignCalibrationStore.Save(AlignCalibration.From(Cam1125(), 10, 10, DateTime.Now), _path);

            Assert.False(File.Exists(_path + ".tmp"));
        }
    }
}
