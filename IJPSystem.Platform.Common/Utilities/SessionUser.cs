namespace IJPSystem.Platform.Common.Utilities
{
    /// <summary>
    /// 지금 로그인한 권한. 레시피 저장·알람 해제·수동 출력처럼 <b>사람이 한 일</b>의 로그에 붙인다.
    ///
    /// <para><b>왜 여기 두나</b>: 권한은 MainViewModel 이 들고 있는데, 레시피·알람·IO 화면은 그걸
    /// 모른다. 화면마다 콜백을 하나씩 더 꽂으면 빠뜨리는 자리가 생긴다 — 로그인이 바뀔 때
    /// 한 곳에서 여기 적고, 로그를 쓰는 쪽은 읽기만 한다.</para>
    ///
    /// <para>레시피 변경 이력 DB 의 사용자 칸이 늘 <c>"Engineer"</c> 로 박혀 있었다(2026-09-13 발견).
    /// "누가 바꿨나" 를 따지는 기록이 거짓이면 없느니만 못하다.</para>
    /// </summary>
    public static class SessionUser
    {
        private static volatile string _role = "";

        /// <summary>권한 이름(Operator/Engineer/Admin). 모르면 빈 문자열.</summary>
        public static string Role
        {
            get => _role;
            set => _role = value ?? "";
        }

        /// <summary>로그 줄에 붙일 표시 — <c>" (Engineer)"</c>. 모르면 빈 문자열.</summary>
        public static string Tag => string.IsNullOrEmpty(_role) ? "" : $" ({_role})";
    }
}
