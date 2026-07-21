using IJPSystem.Platform.Domain.Models.Vision;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;
using System;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 프레임 품질 검증 — 합성 이미지로 확인.
    /// 핵심 목적: <b>나쁜 이미지가 조용히 그럴듯한 숫자를 만드는 것</b>을 막는다.
    /// 특히 초점 이탈은 직경을 부풀리고, 부피는 직경의 3제곱이라 오차가 증폭된다.
    /// </summary>
    public class FrameQualityTests
    {
        private const int W = 640, H = 480;

        private static VisionImage Frame(byte[] buf) => new()
        {
            CameraId = "CAM_DW", Width = W, Height = H,
            IsValid = true, PixelData = buf, BitsPerPixel = 8,
        };

        /// <summary>또렷한 액적(계단형 경계)이 있는 프레임.</summary>
        private static byte[] Sharp(byte bg = 205, byte drop = 35, int radius = 10)
        {
            var buf = new byte[W * H];
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;
            for (int n = 0; n < 5; n++) Disk(buf, 100 + n * 100, H / 2, radius, drop);
            return buf;
        }

        /// <summary>경계가 번진(초점 나간) 프레임 — 반경 방향으로 밝기를 완만히 변화시킨다.</summary>
        private static byte[] Blurred(byte bg = 205, byte drop = 35, int radius = 10, int blur = 14)
        {
            var buf = new byte[W * H];
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;
            for (int n = 0; n < 5; n++)
            {
                int cx = 100 + n * 100, cy = H / 2;
                for (int y = Math.Max(0, cy - radius - blur); y <= Math.Min(H - 1, cy + radius + blur); y++)
                    for (int x = Math.Max(0, cx - radius - blur); x <= Math.Min(W - 1, cx + radius + blur); x++)
                    {
                        double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        if (d > radius + blur) continue;
                        double t = Math.Clamp((d - radius) / blur, 0, 1);   // 0=중심, 1=바깥
                        buf[y * W + x] = (byte)(drop + (bg - drop) * t);
                    }
            }
            return buf;
        }

        private static void Disk(byte[] buf, int cx, int cy, int r, byte v)
        {
            for (int y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(W - 1, cx + r); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) buf[y * W + x] = v;
        }

        private static DropWatcherProcessorConfig Cfg() => new()
        {
            MicronsPerPixel = 2.0, DropletsAreDark = true, MinAreaPx = 20, BackgroundKernel = 81,
        };

        // ── 선명도 ────────────────────────────────────────────────────────────
        [Fact]
        public void SharpImage_HasHigherSharpnessThanBlurred()
        {
            var proc = new DropWatcherProcessor(Cfg());

            double sharp   = proc.AnalyzeQuality(Frame(Sharp())).Sharpness;
            double blurred = proc.AnalyzeQuality(Frame(Blurred())).Sharpness;

            Assert.True(sharp > blurred,
                $"선명한 이미지의 선명도({sharp:F1})가 흐린 것({blurred:F1})보다 커야 한다");
        }

        [Fact]
        public void NoReferenceSet_DoesNotReportFocusIssue()
        {
            // 기준이 없으면 초점 판정을 하지 않는다(절대 임계값을 쓰지 않는다는 설계).
            var proc = new DropWatcherProcessor(Cfg());
            var q = proc.AnalyzeQuality(Frame(Blurred()));

            Assert.True(double.IsNaN(q.SharpnessRatio));
            Assert.DoesNotContain(q.Issues, i => i.Contains("초점"));
        }

        [Fact]
        public void BlurredImage_FlaggedAfterReferenceCapturedFromSharp()
        {
            var cfg = Cfg();
            var proc = new DropWatcherProcessor(cfg);

            // 초점이 맞은 상태에서 기준을 잡고
            proc.CaptureSharpnessReference(Frame(Sharp()));
            Assert.True(cfg.ReferenceSharpness > 0);

            // 이후 초점이 나가면 걸린다
            var q = proc.AnalyzeQuality(Frame(Blurred()));
            Assert.True(q.SharpnessRatio < cfg.MinSharpnessRatio,
                $"흐린 프레임의 기준 대비 비율({q.SharpnessRatio:F2})이 하한({cfg.MinSharpnessRatio})보다 작아야 한다");
            Assert.Contains(q.Issues, i => i.Contains("초점"));
            Assert.False(q.IsAcceptable);
        }

        [Fact]
        public void SameImageAsReference_IsAcceptable()
        {
            var proc = new DropWatcherProcessor(Cfg());
            var f = Frame(Sharp());
            proc.CaptureSharpnessReference(f);

            var q = proc.AnalyzeQuality(f);
            Assert.Equal(1.0, q.SharpnessRatio, precision: 3);
            Assert.DoesNotContain(q.Issues, i => i.Contains("초점"));
        }

        // ── 포화 ──────────────────────────────────────────────────────────────
        [Fact]
        public void OverexposedImage_IsFlagged()
        {
            var buf = new byte[W * H];
            for (int i = 0; i < buf.Length; i++) buf[i] = 255;      // 전면 포화

            var q = new DropWatcherProcessor(Cfg()).AnalyzeQuality(Frame(buf));

            Assert.True(q.SaturatedHighRatio > 0.9);
            Assert.Contains(q.Issues, i => i.Contains("노출 과다"));
        }

        [Fact]
        public void UnderexposedImage_IsFlagged()
        {
            var buf = new byte[W * H];                               // 전면 0

            var q = new DropWatcherProcessor(Cfg()).AnalyzeQuality(Frame(buf));

            Assert.True(q.SaturatedLowRatio > 0.9);
            Assert.Contains(q.Issues, i => i.Contains("노출 부족"));
        }

        [Fact]
        public void NormalExposure_NotFlagged()
        {
            var q = new DropWatcherProcessor(Cfg()).AnalyzeQuality(Frame(Sharp()));

            Assert.DoesNotContain(q.Issues, i => i.Contains("노출"));
            Assert.True(q.MeanLevel > 100 && q.MeanLevel < 240);
        }

        // ── 대비 ──────────────────────────────────────────────────────────────
        [Fact]
        public void LowContrastImage_IsFlagged()
        {
            // 배경 205, 액적 195 — 명암차 10 (하한 20 미만)
            var q = new DropWatcherProcessor(Cfg()).AnalyzeQuality(Frame(Sharp(bg: 205, drop: 195)));
            Assert.Contains(q.Issues, i => i.Contains("대비"));
        }

        [Fact]
        public void GoodContrast_NotFlagged()
        {
            var q = new DropWatcherProcessor(Cfg()).AnalyzeQuality(Frame(Sharp(bg: 205, drop: 35)));
            Assert.DoesNotContain(q.Issues, i => i.Contains("대비"));
        }

        [Fact]
        public void InvalidImage_ReportsIssueInsteadOfThrowing()
        {
            var q = new DropWatcherProcessor(Cfg())
                .AnalyzeQuality(new VisionImage { CameraId = "CAM_DW", IsValid = false });

            Assert.False(q.IsAcceptable);
            Assert.NotNull(q.Summary);
        }
    }
}
