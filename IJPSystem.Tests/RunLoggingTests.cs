using IJPSystem.Platform.Common.Utilities;
using System;
using System.Threading.Tasks;
using Xunit;

namespace IJPSystem.Tests
{
    /// <summary>
    /// 출하 후 분석용 로그 도구 — 운전 번호와 예외 요약. 둘 다 틀리면 로그는 남는데 쓸 수가 없다
    /// (번호가 겹치면 두 운전이 섞이고, 요약이 겉껍데기만 적으면 원인이 안 보인다).
    /// </summary>
    public class RunLoggingTests
    {
        // RunContext 는 정적이라 테스트끼리 섞이면 안 된다 — 이 클래스 안에서만 쓰고, 끝에는 반드시 비운다.

        [Fact]
        public void 운전_중에만_줄_끝_표지가_붙는다()
        {
            string id = RunContext.Begin();
            try
            {
                Assert.StartsWith("R", id);
                Assert.Equal(id, RunContext.CurrentId);
                Assert.Contains(id, RunContext.Suffix);
            }
            finally { RunContext.End(id); }

            Assert.Null(RunContext.CurrentId);
            Assert.Equal("", RunContext.Suffix);
        }

        [Fact]
        public void 같은_초에_두_번_시작해도_번호가_겹치지_않는다()
        {
            string a = RunContext.Begin();
            string b = RunContext.Begin();
            try
            {
                Assert.NotEqual(a, b);
            }
            finally { RunContext.End(b); RunContext.End(a); }
        }

        /// <summary>늦게 도착한 끝내기가 다음 운전의 번호를 지우면, 그 운전의 뒷줄이 전부 번호 없이 남는다.</summary>
        [Fact]
        public void 끝내기는_자기_번호일_때만_지운다()
        {
            string first  = RunContext.Begin();
            string second = RunContext.Begin();
            try
            {
                RunContext.End(first);
                Assert.Equal(second, RunContext.CurrentId);
            }
            finally { RunContext.End(second); }
        }

        [Fact]
        public void 예외_요약에_종류와_안쪽_예외가_나온다()
        {
            var ex = new InvalidOperationException("바깥", new System.IO.IOException("안쪽"));

            string s = ExceptionText.Summary(ex);

            Assert.Contains("InvalidOperationException: 바깥", s);
            Assert.Contains("IOException: 안쪽", s);
        }

        /// <summary>await 에서 새는 AggregateException 은 메시지가 "One or more errors occurred." 뿐이다.</summary>
        [Fact]
        public void 한_겹짜리_AggregateException_은_벗겨서_적는다()
        {
            var ex = new AggregateException(new TimeoutException("축 X 정지 대기 초과"));

            string s = ExceptionText.Summary(ex);

            Assert.StartsWith("TimeoutException: 축 X 정지 대기 초과", s);
        }

        [Fact]
        public async Task 상세에는_스택이_들어간다()
        {
            Exception? caught = null;
            try { await Throw(); }
            catch (Exception e) { caught = e; }

            string d = ExceptionText.Detail(caught);

            Assert.Contains("NotSupportedException", d);
            Assert.Contains(nameof(Throw), d);   // 어느 함수에서 났는지가 남아야 한다
        }

        private static async Task Throw()
        {
            await Task.Yield();
            throw new NotSupportedException("시험");
        }

        [Fact]
        public void 권한을_모르면_표시를_붙이지_않는다()
        {
            string before = SessionUser.Role;
            try
            {
                SessionUser.Role = "";
                Assert.Equal("", SessionUser.Tag);

                SessionUser.Role = "Engineer";
                Assert.Equal(" (Engineer)", SessionUser.Tag);
            }
            finally { SessionUser.Role = before; }
        }
    }
}
