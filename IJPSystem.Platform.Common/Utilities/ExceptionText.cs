using System;
using System.Linq;
using System.Text;

namespace IJPSystem.Platform.Common.Utilities
{
    /// <summary>
    /// 예외를 로그에 남길 글로 바꾼다.
    ///
    /// <para><b>왜 필요한가</b>: 로그를 남기는 catch 가 130곳이 넘는데 거의 전부 <c>ex.Message</c> 만
    /// 적었다(2026-09-13). 현장에서 "Object reference not set to an instance of an object." 한 줄만
    /// 받으면 어디서 났는지 알 길이 없다. 종류·안쪽 예외·스택 앞부분이 있어야 원인까지 간다.</para>
    ///
    /// <para>화면에는 <see cref="Summary"/>(한 줄), 파일에는 <see cref="Detail"/>(스택 포함)을 쓴다 —
    /// 화면 로그가 스택으로 뒤덮이면 운전자가 읽을 수 없다.</para>
    /// </summary>
    public static class ExceptionText
    {
        /// <summary>안쪽 예외를 몇 겹까지 따라가나. 끝없이 감긴 예외에서 멈추게 한다.</summary>
        private const int MaxDepth = 5;

        /// <summary>
        /// 한 줄 요약 — <c>InvalidOperationException: 메시지 ← IOException: 안쪽 메시지</c>.
        /// 종류를 붙이는 이유: 메시지만으로는 같은 문구가 전혀 다른 원인에서 나온다.
        /// </summary>
        public static string Summary(Exception? ex)
        {
            if (ex == null) return "";

            var sb = new StringBuilder();
            var cur = Unwrap(ex);
            for (int depth = 0; cur != null && depth < MaxDepth; depth++, cur = cur.InnerException)
            {
                if (depth > 0) sb.Append(" ← ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 파일용 상세 — 요약 줄 다음에 겹마다 스택 앞 <paramref name="maxFrames"/> 줄.
        /// 스택 전체를 쓰지 않는 이유: async 스택은 수십 줄이 프레임워크 내부라 정작 우리 코드가 묻힌다.
        /// </summary>
        public static string Detail(Exception? ex, int maxFrames = 12)
        {
            if (ex == null) return "";

            var sb = new StringBuilder(Summary(ex));
            var cur = Unwrap(ex);
            for (int depth = 0; cur != null && depth < MaxDepth; depth++, cur = cur.InnerException)
            {
                var frames = (cur.StackTrace ?? "")
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Take(maxFrames)
                    .ToList();
                if (frames.Count == 0) continue;

                sb.AppendLine();
                sb.Append("    ── ").Append(cur.GetType().FullName);
                foreach (var f in frames) sb.AppendLine().Append("    ").Append(f);
            }
            return sb.ToString();
        }

        // AggregateException 은 겉껍데기라 메시지가 "One or more errors occurred." 뿐이다 — 한 겹 벗긴다.
        private static Exception Unwrap(Exception ex)
            => ex is AggregateException ae && ae.InnerExceptions.Count == 1 ? ae.InnerExceptions[0] : ex;
    }
}
