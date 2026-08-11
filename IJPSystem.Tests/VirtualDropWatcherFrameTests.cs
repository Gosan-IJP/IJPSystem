using System;
using System.Collections.Generic;
using System.Linq;
using IJPSystem.Drivers.Vision;
using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 가상 드랍와쳐 합성 프레임이 <b>실제 분석 경로를 통과하는지</b>.
    ///
    /// <para>
    /// 예전 합성 프레임은 액적 반경을 화면 비율(height/40)로 잡아, 2856×2848 에서 지름 142px
    /// = 97µm 가 됐다. 배경억제 커널(81px)보다 커서 닫힘 연산이 그 구멍을 못 메우고, 결과적으로
    /// 배경에도 액적이 남아 <b>차분이 0</b> 이 된다 — 대비 3, 검출은 노이즈, 2점 측정 속도 -2.41m/s
    /// (편차 38)라는 쓰레기 값이 나왔다(2026-08-10).
    /// </para>
    /// <para>
    /// 그래서 여기서는 합성 프레임만 보지 않고 <b>DropWatcherProcessor 로 실제 측정까지</b> 해서
    /// 설정한 속도가 되돌아오는지 확인한다. 가상 모드로 2점 측정을 검증할 수 있다는 뜻이다.
    /// </para>
    /// </summary>
    public class VirtualDropWatcherFrameTests
    {
        // 실장 CAM_DW (GOX-8105M-PGE) + DropWatcherConfig.json 과 같은 값.
        private const int    Width  = 2856;
        private const int    Height = 2848;
        private const double FovXUm = 1956.4;
        private const double Upp    = FovXUm / Width;      // 0.685 µm/px

        private const double Delay1Us = 890.0;
        private const double Delay2Us = 920.0;

        private static VirtualVisionDriver Driver()
        {
            var d = new VirtualVisionDriver();
            d.Initialize(new List<CameraDeviceInfo>
            {
                new() { CameraId = "CAM_DW", Name = "DWC", PixelWidth = Width, PixelHeight = Height },
            });
            return d;
        }

        /// <summary>DropWatcherConfig.json 의 실장 값(측정창·배경커널·피치)을 그대로 옮긴 설정.</summary>
        private static DropWatcherProcessorConfig ProcConfig() => new()
        {
            MicronsPerPixel   = Upp,
            DropletsAreDark   = true,
            BackgroundKernel  = 81,
            MinAreaPx         = 20,
            UseFixedNozzleRoi = true,
            NozzlePitchUm     = 84.7,
            MeasureAreaXUm    = 150,
            NozzleYPixel      = 0,
            MeasureStartUm    = 130,
            MeasureEndUm      = 910,
            FieldOfViewXUm    = FovXUm,
            FieldOfViewYUm    = 1950.9,
            MinContrast       = 20,
        };

        private static VisionImage Grab(VirtualVisionDriver d, double delayUs)
        {
            d.VirtualStrobeDelayUs = delayUs;
            return d.CaptureAsync("CAM_DW", saveToDisk: false).GetAwaiter().GetResult();
        }

        [Fact]
        public void 액적이_배경억제_커널보다_작다()
        {
            var d = Driver();

            // 이 부등식이 깨지면 닫힘 연산이 액적을 배경으로 삼켜 검출이 통째로 죽는다.
            double diameterPx = d.VirtualDropDiameterUm / Upp;
            Assert.True(diameterPx < ProcConfig().BackgroundKernel,
                        $"액적 지름 {diameterPx:F0}px 이 배경커널 {ProcConfig().BackgroundKernel}px 이상입니다.");
        }

        [Fact]
        public void 프레임_품질이_경고_없이_통과한다()
        {
            var q = new DropWatcherProcessor(ProcConfig()).AnalyzeQuality(Grab(Driver(), Delay1Us));

            Assert.True(q.Contrast > ProcConfig().MinContrast,
                        $"대비 {q.Contrast:F0} — 액적이 배경억제에 먹혔습니다.");
            Assert.True(string.IsNullOrEmpty(q.Summary), $"품질 경고: {q.Summary}");
        }

        [Fact]
        public void 액적이_측정창_안에_들어온다()
        {
            var drops = new DropWatcherProcessor(ProcConfig()).DetectDroplets(Grab(Driver(), Delay2Us));

            Assert.NotEmpty(drops);
            Assert.All(drops, x => Assert.False(x.ClippedByWindow));

            // 늦은 쪽 지연이 창 안이면 이른 쪽도 안이다(낙하거리는 지연에 비례).
            var cfg = ProcConfig();
            Assert.All(drops, x => Assert.InRange(x.CentroidYPixel * Upp,
                                                  cfg.MeasureStartUm, cfg.MeasureEndUm));
        }

        [Fact]
        public void 검출_직경이_설정한_액적_크기와_맞는다()
        {
            var d = Driver();
            var drops = new DropWatcherProcessor(ProcConfig()).DetectDroplets(Grab(d, Delay1Us));

            // 이진화·픽셀 격자 때문에 정확히 같을 수는 없다 — 15% 안이면 충분하다.
            double avg = drops.Average(x => x.DiameterMicron);
            Assert.InRange(avg, d.VirtualDropDiameterUm * 0.85, d.VirtualDropDiameterUm * 1.15);
        }

        /// <summary>
        /// 2점 측정의 핵심 — 두 지연의 ΔY 로 낸 속도가 설정한 속도와 같아야 한다.
        /// 이게 맞으면 실장 없이 Time Interval Measure 를 끝까지 검증할 수 있다.
        /// </summary>
        [Fact]
        public void 두_지연의_낙하거리차로_설정한_속도가_나온다()
        {
            var d = Driver();
            var proc = new DropWatcherProcessor(ProcConfig());

            var d1 = proc.DetectDroplets(Grab(d, Delay1Us));
            var d2 = proc.DetectDroplets(Grab(d, Delay2Us));

            Assert.NotEmpty(d1);
            Assert.Equal(d1.Count, d2.Count);          // 지연만 달라졌으니 검출 수는 같아야 한다

            double dtUs = Delay2Us - Delay1Us;
            var vel = d1.Zip(d2, (a, b) => (b.CentroidYPixel - a.CentroidYPixel) * Upp / dtUs).ToArray();

            Assert.All(vel, v => Assert.InRange(v, d.VirtualDropVelocityMps * 0.9,
                                                   d.VirtualDropVelocityMps * 1.1));
        }

        /// <summary>
        /// 토출 전(지연 &lt; <c>VirtualFireDelayUs</c>) 프레임에는 액적을 그리지 않는다.
        ///
        /// <para>
        /// "검출 0개" 를 요구하지는 않는다 — Otsu 는 순수 노이즈에서도 어딘가에서 잘라 얼룩을
        /// 만들고, 그 얼룩은 오히려 진짜 액적보다 크게 뭉친다(실측 137µm). 액적 없는 프레임을
        /// 걸러내는 것은 검출기가 아니라 <b>품질 게이트</b>라, 그쪽이 실제로 막는지를 본다.
        /// </para>
        /// </summary>
        [Fact]
        public void 토출_전_지연은_품질_게이트가_막는다()
        {
            var d = Driver();
            var proc = new DropWatcherProcessor(ProcConfig());

            var q = proc.AnalyzeQuality(Grab(d, d.VirtualFireDelayUs - 10));

            Assert.True(q.Contrast < ProcConfig().MinContrast,
                        $"액적 없는 프레임인데 대비가 {q.Contrast:F0} 입니다.");
            Assert.False(string.IsNullOrEmpty(q.Summary), "품질 경고가 없습니다.");
        }

        /// <summary>해상도를 바꿔도 µm 기준이라 같은 크기·같은 속도가 나온다(비닝/크롭 대비).</summary>
        [Fact]
        public void 해상도가_절반이어도_같은_결과가_나온다()
        {
            int w = Width / 2, h = Height / 2;
            var d = new VirtualVisionDriver();
            d.Initialize(new List<CameraDeviceInfo>
            {
                new() { CameraId = "CAM_DW", Name = "DWC", PixelWidth = w, PixelHeight = h },
            });

            var cfg = ProcConfig();
            cfg.MicronsPerPixel = FovXUm / w;          // 검출기도 같은 규칙으로 스케일을 다시 잡는다
            var proc = new DropWatcherProcessor(cfg);

            var a = proc.DetectDroplets(Grab(d, Delay1Us));
            var b = proc.DetectDroplets(Grab(d, Delay2Us));
            Assert.NotEmpty(a);

            double dtUs = Delay2Us - Delay1Us;
            var vel = a.Zip(b, (p, q) => (q.CentroidYPixel - p.CentroidYPixel) * cfg.MicronsPerPixel / dtUs);
            Assert.All(vel, v => Assert.InRange(v, d.VirtualDropVelocityMps * 0.85,
                                                   d.VirtualDropVelocityMps * 1.15));
        }
    }
}
