using IJPSystem.Platform.Infrastructure.Print;
using System;
using System.Linq;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 노즐 목록 구간 요약. 800개짜리 헤드에서 콤마 목록은 읽을 수 없는 숫자 벽이 된다 —
    /// 이 표기가 화면에서 "무엇을 쓰고 있는지"를 읽는 유일한 수단이라 정확해야 한다.
    /// </summary>
    public class NozzleRangeTextTests
    {
        [Fact]
        public void 연속_번호는_구간으로_접는다()
            => Assert.Equal("1~100", NozzleRangeText.Summarize(Enumerable.Range(1, 100)));

        [Fact]
        public void 끊긴_구간은_쉼표로_나눈다()
            => Assert.Equal("1~5, 10, 20~22",
                            NozzleRangeText.Summarize(new[] { 1, 2, 3, 4, 5, 10, 20, 21, 22 }));

        [Fact]
        public void 하나짜리는_번호만_적는다()
            => Assert.Equal("437", NozzleRangeText.Summarize(new[] { 437 }));

        /// <summary>붙어 있으면 개수와 무관하게 구간 — 읽는 규칙이 하나여야 한다.</summary>
        [Fact]
        public void 두개짜리도_구간으로_적는다()
            => Assert.Equal("5~6", NozzleRangeText.Summarize(new[] { 5, 6 }));

        /// <summary>정렬해서 주지 않아도 맞아야 한다 — 안 그러면 구간이 잘게 쪼개져 조용히 틀린다.</summary>
        [Fact]
        public void 뒤섞인_입력도_정렬해_접는다()
            => Assert.Equal("1~3, 9", NozzleRangeText.Summarize(new[] { 3, 9, 1, 2 }));

        [Fact]
        public void 중복은_한번만_센다()
            => Assert.Equal("7~8", NozzleRangeText.Summarize(new[] { 7, 8, 7, 8, 8 }));

        [Fact]
        public void 비어_있으면_빈_문자열()
        {
            Assert.Equal("", NozzleRangeText.Summarize(Array.Empty<int>()));
            Assert.Equal("", NozzleRangeText.Summarize(null));
        }

        [Fact]
        public void 구간_목록을_그대로_얻을_수_있다()
        {
            var r = NozzleRangeText.Ranges(new[] { 1, 2, 3, 10, 11 });

            Assert.Equal(2, r.Count);
            Assert.Equal((1, 3),  r[0]);
            Assert.Equal((10, 11), r[1]);
        }

        /// <summary>구간이 너무 많으면 상태줄이 넘친다 — 앞부분만 보이고 나머지는 개수로.</summary>
        [Fact]
        public void 구간이_많으면_뒤는_개수로_줄인다()
        {
            var many = Enumerable.Range(0, 10).Select(i => i * 10 + 1);   // 1,11,21,... 구간 10개

            string s = NozzleRangeText.Summarize(many, maxRanges: 3);

            Assert.StartsWith("1, 11, 21", s);
            Assert.Contains("구간 7개 더", s);
        }

        [Fact]
        public void 구간_수가_한도_이하면_그대로_적는다()
            => Assert.Equal("1~3", NozzleRangeText.Summarize(new[] { 1, 2, 3 }, maxRanges: 3));

        /// <summary>표기를 그대로 ADD( ) 안에 붙여 쓸 수 있어야 한다 — 그래서 구간 기호가 ~ 다.</summary>
        [Fact]
        public void 기본_구간기호는_입력문법과_같은_물결표다()
            => Assert.Contains("~", NozzleRangeText.Summarize(new[] { 1, 2, 3 }));

        /// <summary>실사용 규모에서 요약이 한 줄에 들어가는지 — 이게 이 기능의 목적이다.</summary>
        [Fact]
        public void 실사용_규모에서_한줄로_읽힌다()
        {
            var nozzles = Enumerable.Range(1, 100)
                          .Concat(new[] { 150 })
                          .Concat(Enumerable.Range(200, 51));

            string s = NozzleRangeText.Summarize(nozzles);

            Assert.Equal("1~100, 150, 200~250", s);
            Assert.True(s.Length < 30, $"요약이 너무 길다: {s.Length}자");
        }
    }
}
