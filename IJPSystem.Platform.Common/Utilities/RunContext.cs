using System;

namespace IJPSystem.Platform.Common.Utilities
{
    /// <summary>
    /// 지금 도는 <b>운전 한 번</b>의 번호. 자동 인쇄·시퀀스 화면 실행이 시작할 때 만들고 끝날 때 지운다.
    ///
    /// <para><b>왜 필요한가</b>: 출하 후 "그때 찍은 글라스가 불량" 이라는 신고가 오면, 그 운전에서
    /// 무슨 일이 있었는지를 로그에서 골라내야 한다. 그런데 로그는 시각순으로 섞여 있고, Meteor 에
    /// 넘기는 작업 번호는 늘 1 이라 운전을 구별할 표지가 없었다(2026-09-13).</para>
    ///
    /// <para>운전 중에는 파일·DB 로그 줄 끝에 <see cref="Suffix"/> 가 붙는다 — 번호로 검색하면 그
    /// 운전의 줄만 나온다. 화면 로그에는 붙이지 않는다(짧게 읽혀야 한다).</para>
    ///
    /// <para>줄 <b>끝</b>에 붙이는 이유: 로그 화면의 카테고리 필터가 메시지 안의 <c>[태그]</c> 를 찾는다.
    /// 앞에 붙여도 지금 필터(<c>LIKE %태그%</c>)는 동작하지만, 줄 머리가 태그로 시작한다는 약속을
    /// 깨면 사람이 눈으로 훑기 어렵다.</para>
    ///
    /// <para>자동 인쇄와 시퀀스 화면 실행은 동시에 돌지 않는다(화면 전환이 막힌다). 그래서 번호는
    /// 하나만 들고 있고, 끝낼 때는 <b>자기가 시작한 번호일 때만</b> 지운다 — 늦게 도착한 끝내기가
    /// 다음 운전의 번호를 지우지 않게.</para>
    /// </summary>
    public static class RunContext
    {
        private static readonly object _sync = new();
        private static string? _id;
        private static string _lastStamp = "";
        private static int _sameSecond;

        /// <summary>지금 운전 번호. 운전 중이 아니면 null.</summary>
        public static string? CurrentId
        {
            get { lock (_sync) return _id; }
        }

        /// <summary>로그 줄 끝에 붙일 표지. 운전 중이 아니면 빈 문자열.</summary>
        public static string Suffix
        {
            get
            {
                string? id = CurrentId;
                return id == null ? "" : $"  ⟨{id}⟩";
            }
        }

        /// <summary>새 운전 번호를 만들고 현재 운전으로 잡는다. 예: <c>R260913-142201</c>.</summary>
        public static string Begin()
        {
            lock (_sync)
            {
                string stamp = DateTime.Now.ToString("yyMMdd-HHmmss");

                // 같은 초에 두 번 시작하면(연속 STOP→START) 번호가 겹친다 — 뒤에 순번을 붙인다.
                if (stamp == _lastStamp) _sameSecond++;
                else { _lastStamp = stamp; _sameSecond = 0; }

                _id = _sameSecond == 0 ? $"R{stamp}" : $"R{stamp}-{_sameSecond}";
                return _id;
            }
        }

        /// <summary>운전을 끝낸다. 지금 번호가 <paramref name="id"/> 일 때만 지운다.</summary>
        public static void End(string id)
        {
            lock (_sync)
            {
                if (_id == id) _id = null;
            }
        }
    }
}
