using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>노즐 하나의 물리 위치.</summary>
    public readonly struct NozzlePosition
    {
        public NozzlePosition(int number, int head, int row, int indexInRow, double xUm,
                              double yUm = 0, int chip = 0, int slot = -1)
        {
            Number = number; Head = head; Row = row; IndexInRow = indexInRow; XUm = xUm;
            YUm = yUm; Chip = chip; Slot = slot < 0 ? indexInRow : slot;
        }

        /// <summary>화면·레시피가 쓰는 노즐 번호(1-based).</summary>
        public int Number { get; }
        /// <summary>몇 번째 헤드인가(0-based).</summary>
        public int Head { get; }
        /// <summary>헤드 안에서 몇 번째 열인가(0-based).</summary>
        public int Row { get; }
        /// <summary>그 열 안에서 몇 번째인가(0-based).</summary>
        public int IndexInRow { get; }
        /// <summary>스캔 직각 방향(크로스스캔) 위치 [µm]. 첫 헤드 첫 노즐이 0.</summary>
        public double XUm { get; }

        /// <summary>
        /// <b>스캔 방향</b> 위치 [µm]. 칩이 엇갈린 헤드(S3200)에서만 0 이 아니다.
        ///
        /// <para>이 값이 필요한 이유: 칩2·칩4 는 칩1 보다 15.24mm 앞서 있어, 글라스의
        /// <b>같은 지점</b>에 찍으려면 그만큼 늦게 발사해야 한다. X 만 보고 동시에 쏘면
        /// 칩마다 15mm 씩 어긋난 그림이 나온다.</para>
        /// </summary>
        public double YUm { get; }

        /// <summary>몇 번째 칩인가(1-based). 칩이 없는 헤드는 0.</summary>
        public int Chip { get; }

        /// <summary>
        /// 크로스스캔 <b>격자 칸 번호</b>(0-based) — X 를 실효 피치로 나눈 정수.
        /// 겹치는 노즐은 <b>이 값이 같다</b>. 부동소수 X 를 비교하지 않고 겹침을 판정하려고 둔다.
        /// </summary>
        public int Slot { get; }
    }

    /// <summary>
    /// S800 노즐 배열의 <b>물리 위치</b> 계산. (LabVIEW "S800_Make Nozzle POS" + "Sorting Using Nozzle" 대응)
    ///
    /// <para>
    /// 인쇄는 "노즐 n번" 이 아니라 "X 좌표 어디" 로 이뤄진다 — 이미지 픽셀을 노즐에 배정하려면
    /// 각 노즐이 크로스스캔 방향 어디에 있는지 알아야 한다. 여기서 그 좌표를 만든다.
    /// </para>
    ///
    /// <para><b>엇갈린 열(staggered rows)</b> — 이 헤드의 핵심 구조.<br/>
    /// 한 열 안의 노즐 간격(<see cref="InRowPitchUm"/>)은 넓고, 열마다 X 를
    /// <see cref="RowOffsetUm"/> 씩 어긋나게 놓아 촘촘한 해상도를 만든다.
    /// 열이 3개면 <c>254.1 = 3 × 84.7</c> 이라 <b>실효 간격은 84.7µm</b> 가 된다.
    /// </para>
    /// <para>
    /// ※ 드랍와처가 재는 값은 <b>한 열 안 간격</b>(254µm)이다 — 스핏은 한 번에 한 열만
    ///   쏘므로 화면에 보이는 액적 사이는 84.7 이 아니라 254 다. 두 숫자가 다른 것이
    ///   정상이며, 이 관계가 어긋나면 둘 중 하나가 틀린 것이다. [[project-dw-live-memory]]
    /// </para>
    ///
    /// <para><b>번호 규약</b>: 번호가 1 늘면 X 로 한 칸(=<see cref="RowOffsetUm"/>) 옆이 되도록
    /// 열을 번갈아 센다(노즐 1→열0, 2→열1, 3→열2, 4→열0의 다음 …).
    /// 실장 헤드가 "한 열을 끝까지 세고 다음 열" 이라면 <see cref="NozzleOrder.RowByRow"/> 로 바꾼다.
    /// 이 규약이 어긋나면 패턴이 통째로 재배치되므로, 실장 전에 스핏 한 줄로 확인할 것.
    /// </para>
    /// </summary>
    public sealed class NozzleLayout
    {
        /// <summary>노즐 번호를 매기는 순서.</summary>
        public enum NozzleOrder
        {
            /// <summary>열을 번갈아 — 번호가 1 늘면 X 로 한 칸 옆(엇갈린 열의 자연스러운 순서).</summary>
            Interleaved,
            /// <summary>한 열을 끝까지 센 뒤 다음 열.</summary>
            RowByRow,
        }

        public NozzleLayout(int rows, int nozzlesPerRow, double inRowPitchUm, double rowOffsetUm,
                            int headCount = 1, double headPitchUm = 0,
                            NozzleOrder order = NozzleOrder.Interleaved, int firstNozzleNumber = 1)
        {
            if (rows <= 0)          throw new ArgumentOutOfRangeException(nameof(rows));
            if (nozzlesPerRow <= 0) throw new ArgumentOutOfRangeException(nameof(nozzlesPerRow));
            if (inRowPitchUm <= 0)  throw new ArgumentOutOfRangeException(nameof(inRowPitchUm));
            if (headCount <= 0)     throw new ArgumentOutOfRangeException(nameof(headCount));

            Rows              = rows;
            NozzlesPerRow     = nozzlesPerRow;
            InRowPitchUm      = inRowPitchUm;
            RowOffsetUm       = rowOffsetUm;
            HeadCount         = headCount;
            Order             = order;
            FirstNozzleNumber = firstNozzleNumber;

            // 헤드 간격을 주지 않으면 "빈틈 없이 이어 붙인" 간격으로 둔다 — 헤드 하나가
            // 덮는 폭 그대로. 실제 장비의 헤드 갭은 측정값이라 반드시 넣어야 하지만,
            // 헤드가 1개인 지금 구성에서는 쓰이지 않으므로 기본값으로 동작하게 한다.
            HeadPitchUm = headPitchUm > 0 ? headPitchUm : nozzlesPerRow * inRowPitchUm;
        }

        public int    Rows              { get; }
        public int    NozzlesPerRow     { get; }
        public double InRowPitchUm      { get; }
        public double RowOffsetUm       { get; }
        public int    HeadCount         { get; }
        public double HeadPitchUm       { get; }
        public NozzleOrder Order        { get; }
        public int    FirstNozzleNumber { get; }

        /// <summary>헤드 하나의 노즐 수.</summary>
        public int NozzlesPerHead => Rows * NozzlesPerRow;

        /// <summary>전체 노즐 수.</summary>
        public int TotalNozzles => NozzlesPerHead * HeadCount;

        /// <summary>
        /// 엇갈린 열까지 합친 <b>실효 간격</b> [µm] — 인쇄 해상도를 정하는 값.
        /// 열이 3개고 한 열 간격이 254.1 이면 84.7 이 된다.
        /// </summary>
        public double EffectivePitchUm => InRowPitchUm / Rows;

        /// <summary>실효 간격에서 나오는 크로스스캔 해상도 [dpi].</summary>
        public double EffectiveDpi => EffectivePitchUm > 0 ? 25400.0 / EffectivePitchUm : 0;

        /// <summary>노즐 번호(<see cref="FirstNozzleNumber"/> 기준)의 위치. 범위를 벗어나면 null.</summary>
        public NozzlePosition? PositionOf(int nozzleNumber)
        {
            int i = nozzleNumber - FirstNozzleNumber;
            if (i < 0 || i >= TotalNozzles) return null;

            int head    = i / NozzlesPerHead;
            int inHead  = i % NozzlesPerHead;

            int row, indexInRow;
            if (Order == NozzleOrder.Interleaved)
            {
                row        = inHead % Rows;
                indexInRow = inHead / Rows;
            }
            else
            {
                row        = inHead / NozzlesPerRow;
                indexInRow = inHead % NozzlesPerRow;
            }

            double x = head * HeadPitchUm + indexInRow * InRowPitchUm + row * RowOffsetUm;
            return new NozzlePosition(nozzleNumber, head, row, indexInRow, x);
        }

        /// <summary>모든 노즐의 위치를 번호 순으로.</summary>
        public IEnumerable<NozzlePosition> All()
        {
            for (int n = 0; n < TotalNozzles; n++)
            {
                var p = PositionOf(FirstNozzleNumber + n);
                if (p.HasValue) yield return p.Value;
            }
        }

        /// <summary>
        /// 사용 노즐을 <b>X 좌표 순</b>으로 정렬. (LabVIEW "Sorting Using Nozzle" 대응)
        ///
        /// <para>
        /// 이미지 컬럼에 노즐을 배정하려면 번호 순이 아니라 위치 순이어야 한다 — 엇갈린 열이나
        /// 헤드가 여러 개면 번호 순과 위치 순이 다르다. 범위를 벗어난 번호와 중복은 버린다
        /// (조용히 버리지 않도록 <paramref name="ignored"/> 로 돌려준다).
        /// </para>
        /// </summary>
        public IReadOnlyList<NozzlePosition> SortByX(IEnumerable<int> nozzleNumbers, out IReadOnlyList<int> ignored)
        {
            if (nozzleNumbers == null) throw new ArgumentNullException(nameof(nozzleNumbers));

            var found = new List<NozzlePosition>();
            var bad   = new List<int>();
            var seen  = new HashSet<int>();

            foreach (int n in nozzleNumbers)
            {
                if (!seen.Add(n)) continue;                 // 중복은 조용히 넘긴다(같은 노즐을 두 번 쏠 수 없다)
                var p = PositionOf(n);
                if (p.HasValue) found.Add(p.Value); else bad.Add(n);
            }

            ignored = bad;
            // 같은 X 면 번호 순으로 — 정렬 결과가 실행마다 달라지지 않게 한다.
            return found.OrderBy(p => p.XUm).ThenBy(p => p.Number).ToList();
        }
    }
}
