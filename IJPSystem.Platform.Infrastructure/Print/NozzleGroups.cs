using System;
using System.Collections.Generic;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>
    /// 노즐 번호를 <b>칩 단위 / 열 단위</b> 덩어리로 나눈다 — 노즐 선택 화면의 빠른 선택 버튼용.
    ///
    /// <para>
    /// 순수 계산만 둔다(WPF 없음). 화면에 두면 헤드 설정을 띄우지 않고는 시험할 수 없는데,
    /// 여기서 한 칸만 어긋나도 "칩3 을 껐는데 칩2 가 꺼지는" 식으로 조용히 틀린다.
    /// </para>
    ///
    /// <para><b>번호 규약 의존</b> — 지금은 <c>ChipRowBlock</c>(칩1 A 전부 → 칩1 B 전부 → 칩2 A …)을
    /// 따른다. 아직 확정 전이라([[ChipHeadLayout.Numbering]]) 규약이 정해지면 여기 한 곳만 고친다.</para>
    /// </summary>
    public static class NozzleGroups
    {
        /// <summary>노즐 덩어리 하나 — 버튼 하나에 대응한다.</summary>
        public readonly struct Group
        {
            public Group(string label, IReadOnlyList<int> nozzles) { Label = label; Nozzles = nozzles; }
            public string Label { get; }
            public IReadOnlyList<int> Nozzles { get; }
        }

        /// <summary>
        /// 칩별 덩어리. 칩이 1개면 <b>빈 목록</b> — 칩이 없는 헤드에서 "칩1" 버튼 하나만 뜨면
        /// [전체] 와 같은 뜻이라 화면만 어지럽다.
        /// </summary>
        public static IReadOnlyList<Group> ByChip(int chipCount, int rows, int nozzlesPerChipRow,
                                                  int firstNozzle, int lastNozzle)
        {
            var groups = new List<Group>();
            if (chipCount <= 1) return groups;

            int perChip = Math.Max(1, rows) * Math.Max(1, nozzlesPerChipRow);
            for (int c = 0; c < chipCount; c++)
                groups.Add(new Group($"칩{c + 1}",
                                     Range(firstNozzle + c * perChip, perChip, firstNozzle, lastNozzle)));
            return groups;
        }

        /// <summary>
        /// 열별 덩어리. <b>한 열은 칩마다 흩어져 있다</b> — S3200 의 A열은 칩1·2·3·4 에 400개씩,
        /// 즉 네 토막이 모여 1,600개다. 한 토막만 잡으면 그 칩만 켜진다.
        /// </summary>
        public static IReadOnlyList<Group> ByRow(int chipCount, int rows, int nozzlesPerChipRow,
                                                 int firstNozzle, int lastNozzle)
        {
            int chips   = Math.Max(1, chipCount);
            int rowCnt  = Math.Max(1, rows);
            int perRow  = Math.Max(1, nozzlesPerChipRow);
            int perChip = rowCnt * perRow;

            var groups = new List<Group>(rowCnt);
            for (int r = 0; r < rowCnt; r++)
            {
                var all = new List<int>(chips * perRow);
                for (int c = 0; c < chips; c++)
                    all.AddRange(Range(firstNozzle + c * perChip + r * perRow, perRow, firstNozzle, lastNozzle));

                groups.Add(new Group($"{RowName(r)}열", all));
            }
            return groups;
        }

        /// <summary>열 이름 — 도면 표기(Row A / Row B)를 따른다. 열이 26개를 넘으면 숫자로.</summary>
        public static string RowName(int rowIndex)
            => rowIndex is >= 0 and < 26 ? ((char)('A' + rowIndex)).ToString() : (rowIndex + 1).ToString();

        /// <summary>
        /// 범위 안의 번호만 담는다. 설정이 헤드보다 크게 잡혀 있으면(칩 수 × 열 수 × 열당 &gt; 총 노즐 수)
        /// 없는 번호가 섞이는데, 그대로 두면 선택은 되고 토출 단계에서 조용히 빠진다.
        /// </summary>
        private static IReadOnlyList<int> Range(int start, int count, int first, int last)
        {
            var list = new List<int>(Math.Max(0, count));
            for (int n = start; n < start + count; n++)
                if (n >= first && n <= last) list.Add(n);
            return list;
        }
    }
}
