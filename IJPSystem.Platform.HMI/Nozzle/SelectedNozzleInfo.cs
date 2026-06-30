using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.HMI.Nozzle
{
    /// <summary>
    /// LabVIEW "Selected Nozzle info.ctl" 타입정의 대응.
    /// 선택(사용)할 노즐 정보를 담는다.
    /// </summary>
    public sealed class SelectedNozzleInfo
    {
        /// <summary>사용할 노즐 번호 목록(정렬·중복제거됨). = "Using Nozzle"</summary>
        public IReadOnlyList<int> UsingNozzles { get; }

        /// <summary>사용 노즐 개수.</summary>
        public int Count => UsingNozzles.Count;

        /// <summary>원본 입력 문자열.</summary>
        public string SourceText { get; }

        public SelectedNozzleInfo(IEnumerable<int>? usingNozzles, string sourceText = "")
        {
            UsingNozzles = usingNozzles?.ToList() ?? new List<int>();
            SourceText = sourceText ?? "";
        }

        /// <summary>전체 노즐 수 기준의 사용여부 불리언 배열로 변환(헤드 드라이버 적용용).</summary>
        public bool[] ToEnableMask(int totalNozzleCount, int firstNozzleIndex = 1)
        {
            var mask = new bool[totalNozzleCount];
            foreach (int n in UsingNozzles)
            {
                int idx = n - firstNozzleIndex;
                if (idx >= 0 && idx < totalNozzleCount) mask[idx] = true;
            }
            return mask;
        }

        public override string ToString()
            => $"{Count}개 노즐: {string.Join(",", UsingNozzles)}";
    }
}
