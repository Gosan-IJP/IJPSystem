using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using System;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 고정 측정창(노즐 피치 기반) 검출과 프레임 검증.
    ///
    /// <b>왜 필요한가</b>: 이미지 전체에서 자유 검출하면 액적이 아닌 사진에서도 얼룩을 액적으로 세어
    /// "노즐 62개 · 부피 2544pL" 같은 그럴듯한 쓰레기 값이 나온다(실장 2026-08-06). 잘못된 숫자는
    /// 값이 안 나오는 것보다 나쁘다 — 작업자가 그걸 믿고 판단하기 때문이다.
    /// 여기서는 "노즐 자리에 없는 것은 세지 않는다"와 "다른 카메라 이미지는 거부한다"를 고정한다.
    /// </summary>
    public class DropWatcherFixedRoiTests
    {
        private const int    Width       = 1280;
        private const int    Height      = 512;
        private const int    NozzleCount = 15;
        private const int    PitchPx     = 80;
        private const int    FirstXPx    = 40;
        private const int    RadiusPx    = 8;
        private const double UmPerPx     = 2.0;
        private const double PitchUm     = PitchPx * UmPerPx;   // 160µm
        private const int    DropCenterY = 200;

        private static DropWatcherProcessorConfig FixedRoiConfig() => new()
        {
            MicronsPerPixel   = UmPerPx,
            DropletsAreDark   = true,
            UseFixedNozzleRoi = true,
            NozzlePitchUm     = PitchUm,
            NozzleOriginXPx   = FirstXPx,
            MeasureAreaXUm    = 60,          // 창 폭 30px → 중심 ±15px
            NozzleYPixel      = 0,
            MeasureStartUm    = 100,         // 50px
            MeasureEndUm      = 800,         // 400px — 액적(200px)이 안에 든다
            MinAreaPx         = 20,
        };

        /// <summary>
        /// 노즐이 가로로 늘어선 프레임.
        /// <paramref name="xShiftPx"/> 로 격자에서 어긋나게, <paramref name="centerY"/> 로 세로 위치를 옮길 수 있다.
        /// </summary>
        private static VisionImage SynthFrame(int xShiftPx = 0, int? centerY = null)
        {
            const byte bg = 205, drop = 35;
            var buf = new byte[Width * Height];
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;

            for (int n = 0; n < NozzleCount; n++)
                FillDisk(buf, FirstXPx + n * PitchPx + xShiftPx, centerY ?? DropCenterY, RadiusPx, drop);

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

        [Fact]
        public void FixedRoi_FindsOneDropletPerNozzle()
        {
            var proc = new DropWatcherProcessor(FixedRoiConfig());

            var drops = proc.DetectDroplets(SynthFrame());

            Assert.Equal(NozzleCount, drops.Count);
            for (int i = 0; i < drops.Count; i++)
                Assert.Equal(FirstXPx + i * PitchPx, drops[i].CentroidXPixel, 1.0);
        }

        /// <summary>
        /// 격자에서 반 피치 어긋난 액적은 세지 않는다 — 이게 쓰레기 값을 막는 핵심이다.
        /// 같은 프레임을 자유 검출로 보면 전부 잡히므로, 차이는 순전히 고정창 때문이다.
        /// </summary>
        [Fact]
        public void FixedRoi_IgnoresBlobsOffTheNozzleGrid()
        {
            var offGrid = SynthFrame(xShiftPx: PitchPx / 2);

            var fixedCfg = FixedRoiConfig();
            var freeCfg  = FixedRoiConfig();
            freeCfg.UseFixedNozzleRoi = false;

            Assert.Empty(new DropWatcherProcessor(fixedCfg).DetectDroplets(offGrid));
            Assert.Equal(NozzleCount, new DropWatcherProcessor(freeCfg).DetectDroplets(offGrid).Count);
        }

        /// <summary>
        /// 스케일(µm/px)이 틀려도 픽셀로 지정한 격자는 제자리에 선다.
        /// 실장에서 스케일 0.685 · 피치 254µm 로 창 간격이 371px 로 잡혀 실제 113px 와
        /// 3배 어긋난 적이 있다(2026-08-06). 픽셀 지정은 그 두 값과 무관해야 한다.
        /// </summary>
        [Fact]
        public void PitchInPixels_WinsOverWrongScale()
        {
            var cfg = FixedRoiConfig();
            cfg.MicronsPerPixel = 0.1;      // 스케일을 20배 틀리게
            cfg.NozzlePitchUm   = 9999;     // 피치도 엉터리로
            cfg.NozzlePitchPx   = PitchPx;  // ← 픽셀로 고정
            cfg.MeasureAreaXPx  = 30;
            cfg.MeasureTopPx    = DropCenterY - 60;
            cfg.MeasureBottomPx = DropCenterY + 60;

            var drops = new DropWatcherProcessor(cfg).DetectDroplets(SynthFrame());

            Assert.Equal(NozzleCount, drops.Count);
        }

        /// <summary>격자 추정은 이웃 간격의 중앙값 — 미토출로 한 칸 빈 곳이 있어도 흔들리지 않아야 한다.</summary>
        [Fact]
        public void EstimateGrid_ReportsActualPitchInPixels()
        {
            var proc = new DropWatcherProcessor(FixedRoiConfig());

            var grid = proc.EstimateNozzleGrid(SynthFrame());

            Assert.NotNull(grid);
            Assert.Equal(PitchPx, grid!.Value.PitchPx, 1.0);
            Assert.Equal(FirstXPx, grid.Value.OriginXPx, 1.0);
        }

        [Fact]
        public void Validate_RejectsFrameFromAnotherCamera()
        {
            var cfg = FixedRoiConfig();
            cfg.ExpectedImageWidth  = 2856;      // 실제 DWC 카메라
            cfg.ExpectedImageHeight = 2848;

            string? reason = new DropWatcherProcessor(cfg).ValidateFrame(SynthFrame());

            Assert.NotNull(reason);
            Assert.Contains("1280", reason);     // 사유에 실제 크기가 드러나야 조치가 가능하다
        }

        /// <summary>시야보다 피치가 크면 노즐이 한 개도 안 들어온다 — 설정이 틀렸다는 뜻.</summary>
        [Fact]
        public void Validate_RejectsPitchWiderThanFieldOfView()
        {
            var cfg = FixedRoiConfig();
            cfg.NozzlePitchUm = Width * UmPerPx * 2;   // 시야의 2배

            Assert.NotNull(new DropWatcherProcessor(cfg).ValidateFrame(SynthFrame()));
        }

        /// <summary>
        /// 설정을 저장해도 JSON 의 _comment 메모가 살아남아야 한다.
        /// 화면의 [교정값 저장]이 이 객체를 통째로 직렬화하므로, 보존 장치가 없으면
        /// 저장 한 번에 스케일 산출 근거·미검증 경고가 전부 사라진다.
        /// </summary>
        [Fact]
        public void SaveConfig_KeepsCommentKeys()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                 $"dw_cfg_{System.Guid.NewGuid():N}.json");
            System.IO.File.WriteAllText(path,
                "{ \"_comment1\": \"스케일 근거: 2.74µm ÷ 4.0X\", \"MicronsPerPixel\": 0.685 }");
            try
            {
                var loader = new IJPSystem.Platform.Infrastructure.Config.ConfigLoader();
                var cfg = loader.LoadDropWatcherConfig(path);
                cfg.NozzlePitchPx = 113.2;                 // 교정 후 저장을 흉내
                loader.SaveDropWatcherConfig(path, cfg);

                string saved = System.IO.File.ReadAllText(path);
                Assert.Contains("_comment1", saved);
                Assert.Contains("2.74µm", saved);
                Assert.Contains("113.2", saved);
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }

        /// <summary>
        /// 시야는 광학계가 정하는 고정값 — 해상도가 달라져도 화면에 보이는 실제 크기는 그대로다.
        /// 그래서 µm/px 는 프레임마다 시야 ÷ 픽셀수 로 다시 나와야 한다.
        /// (µm/px 를 고정해 두면 1624px 프레임에서 눈금이 1112µm 라고 말했다 — 실제는 1956µm.)
        /// </summary>
        [Fact]
        public void ScaleFromFov_TracksFrameResolution()
        {
            var cfg = FixedRoiConfig();
            cfg.FieldOfViewXUm = 1956.4;
            cfg.FieldOfViewYUm = 1950.9;

            Assert.Equal(0.685, cfg.ScaleFromFov(2856)!.Value, 3);

            // 해상도가 달라도 눈금이 말하는 <b>전체 크기</b>는 사양 그대로여야 한다.
            foreach (int w in new[] { 2856, 1624, 1280 })
                Assert.Equal(1956.4, cfg.ScaleFromFov(w)!.Value * w, 1);

            foreach (int h in new[] { 2848, 1240, 512 })
                Assert.Equal(1950.9, cfg.ScaleYFromFov(h)!.Value * h, 1);
        }

        /// <summary>FOV 를 안 적어 뒀으면 자동 적용도 없다 — 기존 교정값이 그대로 유지되어야 한다.</summary>
        [Fact]
        public void ScaleFromFov_IsOptOut()
        {
            var cfg = FixedRoiConfig();          // FieldOfView* 미설정

            Assert.Null(cfg.ScaleFromFov(Width));
            Assert.Null(cfg.ScaleYFromFov(Height));
        }

        /// <summary>세로 FOV 를 안 주면 null — 눈금자가 가로 스케일을 쓰도록 하는 신호다.</summary>
        [Fact]
        public void ScaleYFromFov_FallsBackWhenOnlyWidthGiven()
        {
            var cfg = FixedRoiConfig();
            cfg.FieldOfViewXUm = 1956.4;

            Assert.NotNull(cfg.ScaleFromFov(Width));
            Assert.Null(cfg.ScaleYFromFov(Height));
        }

        /// <summary>
        /// 실장 해상도(2856×2848)에서 배경억제가 현실적인 시간 안에 끝나야 한다.
        /// <para>
        /// 형태학 비용은 O(폭×높이×커널²) 라, 이 크기에 커널 81 을 그대로 돌리면 5×10¹⁰ 회가 되어
        /// 앱이 응답 없음으로 멈추고 32비트 프로세스에서는 네이티브 예외로 죽는다
        /// (실장 2026-08-07: [격자 자동 맞춤] → "External component has thrown an exception").
        /// 배경은 축소본에서 구하도록 바꿨고, 이 테스트가 그 경로를 고정한다.
        /// 시간 상한은 넉넉히 잡는다 — 여기서 잡으려는 것은 '수십 배 느려짐'이지 미세한 성능차가 아니다.
        /// </para>
        /// </summary>
        [Fact]
        public void FullResolutionFrame_SegmentsWithoutStalling()
        {
            const int w = 2856, h = 2848;
            var cfg = FixedRoiConfig();
            cfg.UseFixedNozzleRoi = false;      // 자유 검출 = 격자 자동 맞춤이 쓰는 경로
            cfg.BackgroundKernel  = 81;         // 실장 config 값
            cfg.MinAreaPx         = 20;

            var buf = new byte[w * h];
            for (int i = 0; i < buf.Length; i++) buf[i] = 205;
            for (int n = 0; n < 8; n++)
                FillDiskOn(buf, w, h, 190 + n * 371, h / 2, 12, 35);

            var frame = new VisionImage
            {
                CameraId = "CAM_DW", Width = w, Height = h,
                IsValid = true, PixelData = buf, BitsPerPixel = 8,
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var grid = new DropWatcherProcessor(cfg).EstimateNozzleGrid(frame);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
                        $"배경억제가 {sw.Elapsed.TotalSeconds:F1}초 걸렸다 — 축소본 경로가 깨졌다");
            Assert.NotNull(grid);
            Assert.Equal(371, grid!.Value.PitchPx, 2.0);   // 축소본을 써도 격자는 원본 해상도로 나와야 한다
        }

        /// <summary>임의 크기 버퍼에 원을 그린다(<see cref="FillDisk"/> 는 클래스 상수 크기 전용).</summary>
        private static void FillDiskOn(byte[] buf, int w, int h, int cx, int cy, int r, byte value)
        {
            int r2 = r * r;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(h - 1, cy + r); y++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(w - 1, cx + r); x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r2) buf[y * w + x] = value;
                }
        }

        [Fact]
        public void Validate_AcceptsMatchingFrame()
        {
            var cfg = FixedRoiConfig();
            cfg.ExpectedImageWidth  = Width;
            cfg.ExpectedImageHeight = Height;

            Assert.Null(new DropWatcherProcessor(cfg).ValidateFrame(SynthFrame()));
        }

        // ── 측정창 경계 걸침 ──────────────────────────────────────────────────
        // 검출은 측정창으로 이미지를 잘라낸 뒤 하므로, 경계에 걸친 액적은 면적이 잘린다.
        // 직경은 √면적, 부피는 직경³ 이라 오차가 증폭된다 — 실장에서 직경 26.9µm(실제 35.1) ·
        // 부피 10.4pL(실제 22.6)로 나왔다(2026-08-10). 조용히 넘어가면 안 되는 값이다.

        /// <summary>창 한가운데 있는 액적은 걸림으로 표시되지 않는다(오경보 방지).</summary>
        [Fact]
        public void Clipping_NotFlagged_WhenDropletIsInsideWindow()
        {
            var drops = new DropWatcherProcessor(FixedRoiConfig()).DetectDroplets(SynthFrame());

            Assert.Equal(NozzleCount, drops.Count);
            Assert.All(drops, d => Assert.False(d.ClippedByWindow));
            Assert.Null(DropWatcherProcessor.ClippedWarning(drops));
        }

        /// <summary>창 아래 경계에 걸친 액적은 걸림으로 표시된다.</summary>
        [Fact]
        public void Clipping_Flagged_WhenDropletStraddlesWindowBottom()
        {
            var cfg = FixedRoiConfig();
            int yBot = (int)(cfg.MeasureEndUm / UmPerPx);        // 400px
            var frame = SynthFrame(centerY: yBot);               // 반은 창 밖

            var drops = new DropWatcherProcessor(cfg).DetectDroplets(frame);

            Assert.Equal(NozzleCount, drops.Count);
            Assert.All(drops, d => Assert.True(d.ClippedByWindow));

            string? warn = DropWatcherProcessor.ClippedWarning(drops);
            Assert.NotNull(warn);
            Assert.Contains($"{NozzleCount}개", warn);
        }

        /// <summary>걸린 액적은 실제보다 작게 나온다 — 경고가 필요한 이유 자체를 고정한다.</summary>
        [Fact]
        public void Clipping_UnderreportsDiameterAndVolume()
        {
            var cfg = FixedRoiConfig();
            var inside  = new DropWatcherProcessor(cfg).DetectDroplets(SynthFrame());
            var clipped = new DropWatcherProcessor(cfg)
                          .DetectDroplets(SynthFrame(centerY: (int)(cfg.MeasureEndUm / UmPerPx)));

            // 위 절반만 남으므로 면적도 대략 절반, 직경은 √2 배 작아진다.
            Assert.InRange(clipped[0].AreaPx / inside[0].AreaPx, 0.35, 0.65);
            Assert.True(clipped[0].DiameterMicron  < inside[0].DiameterMicron);
            Assert.True(clipped[0].VolumePicoLiter < inside[0].VolumePicoLiter * 0.6);
        }
    }
}
