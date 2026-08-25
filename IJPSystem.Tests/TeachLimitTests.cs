using System.Collections.Generic;
using System.Linq;
using IJPSystem.Platform.Domain.Models.Motion;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 티칭 저장 범위 판정. T축을 좁은 범위로 묶는 근거가 이 판정이라, 경계 부호가 뒤집히면
    /// 범위 밖 좌표가 그대로 레시피에 굳는다.
    ///
    /// <para>아래 검사들이 쓰는 0~30 은 <b>판정 로직을 보려고 만든 값</b>이다.
    /// 실제 설정값은 맨 아래 MotorConfig 검사 하나가 지킨다.</para>
    /// </summary>
    public class TeachLimitTests
    {
        private static AxisDeviceInfo T(double? min, double? max) => new()
        {
            AxisNo = "T",
            Name = "T AXIS",
            Unit = "deg",
            TeachLimit = new TeachLimitConfig { Min = min, Max = max },
        };

        [Theory]
        [InlineData(0)]      // 하한 경계는 포함
        [InlineData(15)]
        [InlineData(30)]     // 상한 경계는 포함
        public void 범위_안이면_통과(double pos)
            => Assert.True(T(0, 30).IsWithinTeachLimit(pos));

        [Theory]
        [InlineData(-0.001)]
        [InlineData(-5)]
        [InlineData(30.001)]
        [InlineData(90)]
        public void 범위_밖이면_거부(double pos)
            => Assert.False(T(0, 30).IsWithinTeachLimit(pos));

        [Fact]
        public void 범위_미설정_축은_어디든_통과()
        {
            var axis = new AxisDeviceInfo { AxisNo = "X", Name = "X AXIS", Unit = "mm" };
            Assert.True(axis.IsWithinTeachLimit(-99999));
            Assert.True(axis.IsWithinTeachLimit(99999));
        }

        [Fact]
        public void 한쪽만_설정하면_그쪽만_막는다()
        {
            var onlyMax = T(null, 30);
            Assert.True(onlyMax.IsWithinTeachLimit(-1000));   // 하한 없음
            Assert.False(onlyMax.IsWithinTeachLimit(31));

            var onlyMin = T(0, null);
            Assert.False(onlyMin.IsWithinTeachLimit(-1));
            Assert.True(onlyMin.IsWithinTeachLimit(1000));    // 상한 없음
        }

        [Fact]
        public void 표기는_단위까지_붙여_안내문에_그대로_쓴다()
        {
            Assert.Equal("0~30deg", T(0, 30).TeachLimitText);
            Assert.Equal("-∞~30deg", T(null, 30).TeachLimitText);
            Assert.Equal("0~+∞deg", T(0, null).TeachLimitText);
            Assert.Equal("", new AxisDeviceInfo { Unit = "mm" }.TeachLimitText);
        }

        // ── 저장 직전 검사 ────────────────────────────────────────────────
        private static readonly AxisDeviceInfo X = new() { AxisNo = "X", Name = "X AXIS", Unit = "mm" };

        private static (string, IReadOnlyDictionary<string, double>, IReadOnlyDictionary<string, bool>?)
            Point(string name, double tDeg, bool tUsed = true) =>
            (name,
             new Dictionary<string, double> { ["X AXIS"] = 9999, ["T AXIS"] = tDeg },
             new Dictionary<string, bool>   { ["X AXIS"] = true, ["T AXIS"] = tUsed });

        [Fact]
        public void 범위_안이면_저장을_막지_않는다()
        {
            var bad = TeachLimitCheck.Find(new[] { Point("PRINT", 30) }, new[] { X, T(0, 30) });
            Assert.Empty(bad);
        }

        [Fact]
        public void 범위_밖이면_어느_포인트의_어느_축인지_알려준다()
        {
            var bad = TeachLimitCheck.Find(new[] { Point("PRINT", 45) }, new[] { X, T(0, 30) });

            var v = Assert.Single(bad);
            Assert.Equal("PRINT", v.PointName);
            Assert.Equal("T AXIS", v.Axis.Name);
            Assert.Equal(45, v.Value);
            Assert.Contains("0~30deg", v.ToString());
        }

        [Fact]
        public void 제한_없는_축은_아무리_커도_통과()
        {
            // X 는 9999mm 지만 TeachLimit 이 없다 — 잡히면 안 된다.
            var bad = TeachLimitCheck.Find(new[] { Point("PRINT", 10) }, new[] { X, T(0, 30) });
            Assert.Empty(bad);
        }

        [Fact]
        public void 사용_안_함으로_꺼_둔_축은_검사하지_않는다()
        {
            var bad = TeachLimitCheck.Find(new[] { Point("PRINT", 45, tUsed: false) }, new[] { X, T(0, 30) });
            Assert.Empty(bad);
        }

        [Fact]
        public void 여러_포인트가_걸리면_전부_모은다()
        {
            var bad = TeachLimitCheck.Find(
                new[] { Point("HOME", -1), Point("PRINT", 45), Point("PURGE", 15) },
                new[] { X, T(0, 30) });

            Assert.Equal(2, bad.Count);
            Assert.Equal(new[] { "HOME", "PRINT" }, bad.Select(v => v.PointName));
        }

        [Fact]
        public void 안내문은_넘치면_나머지_건수로_접는다()
        {
            var points = Enumerable.Range(0, 10).Select(i => Point($"P{i}", 45));
            var bad = TeachLimitCheck.Find(points, new[] { T(0, 30) });

            string msg = TeachLimitCheck.Message(bad, english: false, maxLines: 3);
            Assert.Contains("P0", msg);
            Assert.Contains("… 외 7건", msg);
            Assert.DoesNotContain("P9", msg);
        }

        /// <summary>
        /// 실제 MotorConfig.json 이 T축에 ±10 을 들고 있는지 — 설정이 빠지면 제한이 사라진다.
        ///
        /// <para>정렬 보정은 양쪽으로 간다 — 한쪽만 허용하면 티칭 값을 0 에 둔 순간
        /// 음의 보정을 저장할 수 없게 된다(2026-08-25).</para>
        /// </summary>
        [Fact]
        public void MotorConfig_의_T축은_양쪽_10도()
        {
            // bin 깊이를 세어 ".." 를 쌓으면 폴더 구조가 바뀔 때 조용히 건너뛰는 테스트가 된다.
            // 위로 훑어 찾고, 못 찾으면 실패시킨다.
            string? path = null;
            for (var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
                 d != null && path == null; d = d.Parent)
            {
                string c = System.IO.Path.Combine(d.FullName, "Config", "MotorConfig.json");
                if (System.IO.File.Exists(c)) path = c;
            }
            Assert.True(path != null, "Config/MotorConfig.json 을 찾지 못했습니다.");

            var root = System.Text.Json.JsonSerializer.Deserialize<MotionAxisRoot>(
                System.IO.File.ReadAllText(path!),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var t = System.Linq.Enumerable.Single(root!.MotionAxisList, a => a.AxisNo == "T");
            Assert.Equal(-10, t.TeachLimit!.Min);
            Assert.Equal(10, t.TeachLimit!.Max);
        }
    }
}
