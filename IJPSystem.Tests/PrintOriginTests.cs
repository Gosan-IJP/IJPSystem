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
}
