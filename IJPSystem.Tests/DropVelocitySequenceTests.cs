using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 2점 지연 측정 전체 경로 검증 — 카메라·스트로브 없이.
    ///
    /// <b>속도를 미리 정해 둔 합성 프레임</b>을 넣고, 파이프라인이 그 속도를 되돌려주는지 본다.
    /// 검출 → X 로 노즐 짝짓기 → ΔY/Δt → µm/px 환산까지 한 번에 확인되므로,
    /// 하드웨어 없이 잡을 수 있는 버그(부호 뒤집힘, 스케일 누락, 짝짓기 오류)는 대부분 여기서 걸린다.
    /// </summary>
    public class DropVelocitySequenceTests
    {
        // 합성 조건 — 실측 DW 프레임 구조(노즐이 가로로 늘어선 한 장)를 흉내.
        private const int    Width       = 1280;
        private const int    Height      = 512;
        private const int    NozzleCount = 15;
        private const int    PitchPx     = 80;
        private const int    FirstXPx    = 40;
        private const int    RadiusPx    = 8;
        private const double UmPerPx     = 2.0;

        /// <summary>지연을 기록하고 합성기에 넘기는 가짜 스트로브.</summary>
        private sealed class FakeStrobe : IStrobeController
        {
            public double LastDelayMicroseconds { get; private set; } = double.NaN;
            public bool   IsConnected           => true;
            public List<double> AppliedDelays   { get; } = new();

            public void Init() { }
            public void Enable(bool on) { }
            public void SetDelayMicroseconds(double us) { LastDelayMicroseconds = us; AppliedDelays.Add(us); }
            public void Dispose() { }
        }

        /// <summary>
        /// 지연에 비례해 낙하한 액적 프레임을 만든다(등속 낙하).
        /// 중심 Y[px] = 지연[µs] × 속도[µm/µs] / (µm/px)
        /// </summary>
        private static VisionImage SynthFrame(double delayUs, double velocityMps, double xJitterPx = 0,
                                              int skipIndex = -1)
        {
            const byte bg = 205, drop = 35;
            var buf = new byte[Width * Height];
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;

            int cy = (int)Math.Round(delayUs * velocityMps / UmPerPx);
            for (int n = 0; n < NozzleCount; n++)
            {
                if (n == skipIndex) continue;   // 불토출 노즐 시뮬레이션
                int cx = FirstXPx + n * PitchPx + (int)Math.Round(xJitterPx);
                FillDisk(buf, cx, cy, RadiusPx, drop);
            }

            return new VisionImage
            {
                CameraId = "CAM_DW", Width = Width, Height = Height,
                IsValid = true, PixelData = buf, BitsPerPixel = 8,
            };
        }

        private static void FillDisk(byte[] buf, int cx, int cy, int r, byte value)
        {
            int r2 = r * r;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(Height - 1, cy + r); y++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(Width - 1, cx + r); x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r2) buf[y * Width + x] = value;
                }
        }

        private static (DropVelocitySequence seq, FakeStrobe strobe) Build(double velocityMps, double xJitterPx = 0)
        {
            var cfg = new DropWatcherProcessorConfig
            {
                MicronsPerPixel = UmPerPx,
                DropletsAreDark = true,
                MinAreaPx       = 20,
                BackgroundKernel = 81,
            };
            var proc   = new DropWatcherProcessor(cfg);
            var strobe = new FakeStrobe();

            // 촬영 델리게이트가 "그 시점에 스트로브에 걸린 지연"으로 프레임을 만든다 →
            // 실제 장비의 인과(지연 설정 → 그 위상의 프레임)를 그대로 재현한다.
            var seq = new DropVelocitySequence(
                strobe,
                _ => Task.FromResult(SynthFrame(strobe.LastDelayMicroseconds, velocityMps, xJitterPx)),
                proc, cfg)
            {
                SettleMs = 0,   // 테스트는 대기 없이
            };
            return (seq, strobe);
        }

        // ── 핵심: 넣은 속도가 그대로 나오는가 ─────────────────────────────────
        [Theory]
        [InlineData(5.0)]
        [InlineData(4.0)]
        [InlineData(6.5)]
        public async Task MeasuresTheVelocityItWasGiven(double velocityMps)
        {
            var (seq, _) = Build(velocityMps);

            var r = await seq.MeasureVelocityAsync(time1Us: 20, time2Us: 60);

            Assert.True(r.Success, r.Message);
            Assert.Equal(NozzleCount, r.Nozzles.Count);
            // 합성 시 중심 Y 를 정수 픽셀로 반올림하므로 오차 여유를 둔다.
            Assert.Equal(velocityMps, r.VelocityMps, precision: 1);
        }

        [Fact]
        public async Task DetectsAllNozzlesInFrame()
        {
            var (seq, _) = Build(5.0);
            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.Equal(NozzleCount, r.DetectedAt1);
            Assert.Equal(NozzleCount, r.DetectedAt2);
        }

        [Fact]
        public async Task AppliesBothDelaysToStrobeInOrder()
        {
            var (seq, strobe) = Build(5.0);
            await seq.MeasureVelocityAsync(20, 60);

            Assert.Equal(new[] { 20.0, 60.0 }, strobe.AppliedDelays);
        }

        // ── 부호: Time1 > Time2 로 넣어도 속도가 음수가 되면 안 된다 ──────────
        [Fact]
        public async Task ReversedDelayOrder_StillYieldsPositiveVelocity()
        {
            var (seq, _) = Build(5.0);

            var r = await seq.MeasureVelocityAsync(time1Us: 60, time2Us: 20);   // 역순

            Assert.True(r.Success, r.Message);
            Assert.Equal(5.0, r.VelocityMps, precision: 1);
            Assert.True(r.VelocityMps > 0, "역순 입력에서 속도 부호가 뒤집혔다");
        }

        // ── µm/px 스케일이 결과에 비례하는가 ─────────────────────────────────
        [Fact]
        public async Task VelocityScalesWithMicronsPerPixel()
        {
            // 같은 영상이라도 µm/px 를 2배로 잡으면 속도도 2배가 나와야 한다.
            var cfg = new DropWatcherProcessorConfig
            {
                MicronsPerPixel = UmPerPx * 2, DropletsAreDark = true,
                MinAreaPx = 20, BackgroundKernel = 81,
            };
            var strobe = new FakeStrobe();
            var seq = new DropVelocitySequence(
                strobe, _ => Task.FromResult(SynthFrame(strobe.LastDelayMicroseconds, 5.0)),
                new DropWatcherProcessor(cfg), cfg) { SettleMs = 0 };

            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.True(r.Success, r.Message);
            Assert.Equal(10.0, r.VelocityMps, precision: 1);
        }

        // ── 실패 경로 ─────────────────────────────────────────────────────────
        [Fact]
        public async Task EqualDelays_FailsWithReason()
        {
            var (seq, _) = Build(5.0);
            var r = await seq.MeasureVelocityAsync(50, 50);

            Assert.False(r.Success);
            Assert.Contains("Delay", r.Message);
        }

        [Fact]
        public async Task ZeroScale_FailsInsteadOfReturningGarbage()
        {
            var cfg = new DropWatcherProcessorConfig { MicronsPerPixel = 0 };
            var seq = new DropVelocitySequence(
                new FakeStrobe(), _ => Task.FromResult(SynthFrame(20, 5.0)),
                new DropWatcherProcessor(cfg), cfg) { SettleMs = 0 };

            var r = await seq.MeasureVelocityAsync(20, 60);
            Assert.False(r.Success);
        }

        [Fact]
        public async Task NoDropletsDetected_FailsWithReason()
        {
            var cfg = new DropWatcherProcessorConfig { MicronsPerPixel = UmPerPx, MinAreaPx = 20 };
            var blank = new VisionImage
            {
                CameraId = "CAM_DW", Width = Width, Height = Height,
                IsValid = true, PixelData = new byte[Width * Height], BitsPerPixel = 8,
            };
            var seq = new DropVelocitySequence(
                new FakeStrobe(), _ => Task.FromResult(blank),
                new DropWatcherProcessor(cfg), cfg) { SettleMs = 0 };

            var r = await seq.MeasureVelocityAsync(20, 60);
            Assert.False(r.Success);
            Assert.Contains("검출", r.Message);
        }

        // ── 통합: 노즐 매핑 · 품질 · 불일치가 측정 결과까지 실제로 전달되는가 ──────
        // 단위 테스트가 아니라 배선 확인이다. 각 부품이 맞아도 연결이 빠지면 화면엔 안 뜬다.

        /// <summary>합성 프레임 기준 노즐 피치[µm] — 픽셀 피치 × µm/px.</summary>
        private const double SynthPitchUm = PitchPx * UmPerPx;   // 80px × 2 = 160µm

        [Fact]
        public async Task MissingNozzle_PropagatesToResultGridAndWarnings()
        {
            // 노즐 1~15 중 4번(인덱스 3)이 불토출.
            const int skip = 3;
            var cfg = new DropWatcherProcessorConfig
            {
                MicronsPerPixel = UmPerPx, DropletsAreDark = true,
                MinAreaPx = 20, BackgroundKernel = 81,
            };
            var strobe = new FakeStrobe();
            var seq = new DropVelocitySequence(
                strobe,
                _ => Task.FromResult(SynthFrame(strobe.LastDelayMicroseconds, 5.0, skipIndex: skip)),
                new DropWatcherProcessor(cfg), cfg)
            {
                SettleMs        = 0,
                ExpectedNozzles = Enumerable.Range(1, NozzleCount).ToArray(),
                NozzlePitchUm   = SynthPitchUm,
            };

            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.True(r.Success, r.Message);
            Assert.NotNull(r.Grid);
            Assert.Equal(new[] { skip + 1 }, r.Grid!.MissingNozzles);      // 4번
            Assert.True(r.Grid.AbsoluteMappingConfident);
            Assert.Contains(r.Warnings, w => w.Contains("불토출"));

            // 속도는 나머지 노즐로 정상 산출돼야 한다.
            Assert.Equal(NozzleCount - 1, r.Nozzles.Count);
            Assert.Equal(5.0, r.VelocityMps, precision: 1);
        }

        [Fact]
        public async Task NoExpectedNozzles_SkipsGridMappingWithoutFailing()
        {
            var (seq, _) = Build(5.0);   // ExpectedNozzles 미설정

            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.True(r.Success, r.Message);
            Assert.Null(r.Grid);
        }

        [Fact]
        public async Task QualityIsMeasuredForBothFrames()
        {
            var (seq, _) = Build(5.0);
            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.NotNull(r.Quality1);
            Assert.NotNull(r.Quality2);
            Assert.True(r.Quality1!.Sharpness > 0);
            Assert.True(r.Quality2!.Contrast > 0);
        }

        [Fact]
        public async Task CleanMeasurement_HasNoWarnings()
        {
            var (seq, _) = Build(5.0);
            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.True(r.Success, r.Message);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public async Task DetectionCountMismatchBetweenFrames_IsWarned()
        {
            // Delay2 프레임에서만 노즐 하나가 빠지도록 — 촬영 조건이 흔들린 상황을 흉내.
            var cfg = new DropWatcherProcessorConfig
            {
                MicronsPerPixel = UmPerPx, DropletsAreDark = true,
                MinAreaPx = 20, BackgroundKernel = 81,
            };
            var strobe = new FakeStrobe();
            var seq = new DropVelocitySequence(
                strobe,
                _ => Task.FromResult(SynthFrame(strobe.LastDelayMicroseconds, 5.0,
                        skipIndex: strobe.AppliedDelays.Count > 1 ? 5 : -1)),
                new DropWatcherProcessor(cfg), cfg) { SettleMs = 0 };

            var r = await seq.MeasureVelocityAsync(20, 60);

            Assert.True(r.Success, r.Message);
            Assert.NotEqual(r.DetectedAt1, r.DetectedAt2);
            Assert.Contains(r.Warnings, w => w.Contains("불일치"));
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            var (seq, _) = Build(5.0);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => seq.MeasureVelocityAsync(20, 60, cts.Token));
        }
    }
}
