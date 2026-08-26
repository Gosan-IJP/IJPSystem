using IJPSystem.Platform.Application.Printing;
using System;
using System.IO;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 인쇄 원점 관리자 검증 — 모션 하드웨어 없이(IStagePosition 대역).
    /// 캡처·리셋·저장·복원과 로케일 안전 직렬화를 확인한다.
    /// </summary>
    public class PrintOriginTests : IDisposable
    {
        private readonly string _dir;

        public PrintOriginTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "IJP_Origin_" + Guid.NewGuid().ToString("N")[..8]);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private sealed class FakeStage : IStagePosition
        {
            public AxisPoint P;
            public FakeStage(double x, double y, double z) => P = new AxisPoint(x, y, z);
            public AxisPoint GetCurrentPosition() => P;
        }

        [Fact]
        public void SetPrintOrigin_CapturesCurrentStagePosition()
        {
            var stage = new FakeStage(148.584, 281.005, 28.0);
            var mgr = new PrintOriginManager(stage);

            var o = mgr.SetPrintOrigin();

            Assert.Equal(148.584, o.X, 3);
            Assert.Equal(281.005, o.Y, 3);
            Assert.Equal(mgr.PrintOrigin, o);
        }

        [Fact]
        public void MovingStageThenSet_CapturesNewPosition()
        {
            var stage = new FakeStage(10, 20, 30);
            var mgr = new PrintOriginManager(stage);
            mgr.SetPrintOrigin();

            stage.P = new AxisPoint(60.395, 260.503, 29.0);   // 스테이지 이동
            var o = mgr.SetPrintOrigin();

            Assert.Equal(60.395, o.X, 3);
            Assert.Equal(260.503, o.Y, 3);
        }

        [Fact]
        public void ResetToDefault_RestoresDefaultOrigin()
        {
            var mgr = new PrintOriginManager(new FakeStage(100, 200, 30),
                                             defaultOrigin: new AxisPoint(5, 5, 0));
            mgr.SetPrintOrigin();
            Assert.NotEqual(new AxisPoint(5, 5, 0), mgr.PrintOrigin);

            mgr.ResetToDefault();
            Assert.Equal(new AxisPoint(5, 5, 0), mgr.PrintOrigin);
        }

        [Fact]
        public void ChangeEvent_FiresOnSetAndReset()
        {
            var mgr = new PrintOriginManager(new FakeStage(1, 2, 3));
            int fired = 0;
            mgr.PrintOriginChanged += (_, _) => fired++;

            mgr.SetPrintOrigin();
            mgr.ResetToDefault();

            Assert.Equal(2, fired);
        }

        // ── 영속화 ────────────────────────────────────────────────────────────
        [Fact]
        public void Saved_Origin_SurvivesReload()
        {
            var stage = new FakeStage(148.584, 281.005, 28.0);
            new PrintOriginManager(stage, _dir).SetPrintOrigin();

            // 새 관리자 인스턴스로 로드
            var reloaded = new PrintOriginManager(new FakeStage(0, 0, 0), _dir);
            Assert.True(reloaded.Load());
            Assert.Equal(148.584, reloaded.PrintOrigin.X, 3);
            Assert.Equal(281.005, reloaded.PrintOrigin.Y, 3);
        }

        [Fact]
        public void Load_NoFile_ReturnsFalseAndKeepsCurrent()
        {
            var mgr = new PrintOriginManager(new FakeStage(0, 0, 0), _dir);
            Assert.False(mgr.Load());   // 저장된 적 없음
        }

        [Fact]
        public void NullDataDir_DoesNotThrowAndSkipsPersistence()
        {
            var mgr = new PrintOriginManager(new FakeStage(1, 2, 3), dataDir: null);
            mgr.SetPrintOrigin();       // 저장 생략 — 예외 없이 메모리에만
            Assert.Equal(1, mgr.PrintOrigin.X, 3);
            Assert.False(mgr.Load());
        }

        [Fact]
        public void Serialization_IsCultureInvariant()
        {
            // 소수점 로케일(콤마 vs 점) 문제로 저장/복원이 깨지지 않아야 한다.
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");   // 소수점 = 콤마

                new PrintOriginManager(new FakeStage(1.5, 2.25, 3.125), _dir).SetPrintOrigin();
                var reloaded = new PrintOriginManager(new FakeStage(0, 0, 0), _dir);

                Assert.True(reloaded.Load());
                Assert.Equal(1.5, reloaded.PrintOrigin.X, 3);
                Assert.Equal(2.25, reloaded.PrintOrigin.Y, 3);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }
    }

    /// <summary>
    /// 인쇄 원점의 주인이 레시피 PRINT START 라는 것.
    ///
    /// <para>값이 두 군데 있으면 언젠가 갈라진다 — 원점 창에는 옛 값이 뜨는데 인쇄는 새 자리에서
    /// 시작한다. 그 갈라짐은 인쇄물이 어긋나야 드러나므로 여기서 막는다.</para>
    /// </summary>
    public class PrintOriginStoreTests
    {
        private sealed class FakeStage : IStagePosition
        {
            public AxisPoint P;
            public FakeStage(double x, double y, double z) => P = new AxisPoint(x, y, z);
            public AxisPoint GetCurrentPosition() => P;
        }

        /// <summary>PRINT START 티칭값 흉내 — X·Y·Z 를 들고 있다.</summary>
        private sealed class FakePointStore : IPrintOriginStore
        {
            public AxisPoint Point = new(10, 20, 30);
            public bool Exists = true;
            public string? Fail;
            public int Writes;

            public bool TryRead(out AxisPoint origin) { origin = Point; return Exists; }

            public bool Write(AxisPoint origin, out string message)
            {
                message = Fail ?? "";
                if (Fail != null) return false;

                // 실제 저장부와 같은 규칙: X·Y 만 쓰고 Z 는 티칭값을 지킨다.
                Point = new AxisPoint(origin.X, origin.Y, Point.Z);
                Writes++;
                return true;
            }
        }

        [Fact]
        public void 원점을_티칭값에서_읽는다()
        {
            var store = new FakePointStore { Point = new AxisPoint(148.5, 281.0, 28.0) };
            var mgr = new PrintOriginManager(new FakeStage(0, 0, 0), store);

            Assert.True(mgr.Load());
            Assert.Equal(148.5, mgr.PrintOrigin.X, 3);
            Assert.Equal(281.0, mgr.PrintOrigin.Y, 3);
        }

        [Fact]
        public void 티칭값이_없으면_현재값을_지킨다()
        {
            var mgr = new PrintOriginManager(new FakeStage(0, 0, 0),
                                             new FakePointStore { Exists = false },
                                             defaultOrigin: new AxisPoint(1, 2, 3));

            Assert.False(mgr.Load());
            Assert.Equal(new AxisPoint(1, 2, 3), mgr.PrintOrigin);
        }

        [Fact]
        public void Set_하면_티칭값이_바뀐다()
        {
            var store = new FakePointStore();
            var mgr = new PrintOriginManager(new FakeStage(60.395, 260.503, 29.0), store);
            mgr.Load();

            mgr.SetPrintOrigin();

            Assert.Equal(1, store.Writes);
            Assert.Equal(60.395, store.Point.X, 3);
            Assert.Equal(260.503, store.Point.Y, 3);
        }

        [Fact]
        public void Z는_건드리지_않는다()
        {
            // Z 는 헤드 높이라 원점이 아니다 — 여기서 덮어쓰면 티칭한 높이가 현재값으로 밀린다.
            var store = new FakePointStore { Point = new AxisPoint(0, 0, 28.0) };
            var mgr = new PrintOriginManager(new FakeStage(10, 20, 99.0), store);
            mgr.Load();

            mgr.SetPrintOrigin();

            Assert.Equal(28.0, store.Point.Z, 3);
        }

        [Fact]
        public void 저장이_실패하면_이유가_남는다()
        {
            // 화면만 바뀌고 저장이 안 되면 다음 인쇄에서야 드러난다.
            var mgr = new PrintOriginManager(new FakeStage(1, 2, 3),
                                             new FakePointStore { Fail = "레시피가 선택되지 않았습니다." });

            mgr.SetPrintOrigin();

            Assert.Contains("레시피", mgr.LastError);
        }

        [Fact]
        public void 저장이_되면_이유가_비어_있다()
        {
            var mgr = new PrintOriginManager(new FakeStage(1, 2, 3), new FakePointStore());

            mgr.SetPrintOrigin();

            Assert.Equal("", mgr.LastError);
        }

        [Fact]
        public void 파일_저장은_쓰지_않는다()
        {
            // 같은 값을 두 군데 두면 갈라진다 — 티칭값 하나만 주인이어야 한다.
            var store = new FakePointStore();
            var mgr = new PrintOriginManager(new FakeStage(5, 6, 7), store);

            mgr.SetPrintOrigin();
            mgr.ResetToDefault();

            Assert.Equal(2, store.Writes);   // 두 번 다 티칭값으로 갔다
        }
    }
}
