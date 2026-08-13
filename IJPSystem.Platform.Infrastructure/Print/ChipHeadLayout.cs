using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>노즐 열. 헤드 도면의 <c>Row A</c> / <c>Row B</c> 표기 그대로.</summary>
    public enum NozzleRow
    {
        A = 0,
        B = 1,
    }

    /// <summary>
    /// <b>칩이 엇갈려 붙은 헤드</b>의 노즐 물리 배치. (Epson S3200 = 400노즐 × 2열 × 4칩 = 3,200)
    ///
    /// <para>
    /// <see cref="NozzleLayout"/> 은 열이 고르게 반복되는 헤드(S800)를 다룬다. S3200 은 그 모델로
    /// 표현되지 않는다 — 칩 4개가 <b>서로 겹치면서</b> 이어 붙고, 짝수 칩은 스캔 방향으로
    /// 15.24mm 앞서 있으며, 칩 안에서 A/B 두 열이 반 피치 어긋나 600npi 를 만든다.
    /// </para>
    ///
    /// <para><b>구조 — 도면에서 읽은 값이 서로 맞물린다</b><br/>
    /// 600npi = 42.3333µm 가 <i>실효</i> 피치이고, 한 열 안 간격은 그 두 배(84.6667µm = 300npi)다.
    /// A 와 B 가 반 피치 어긋나 맞물려 600npi 가 된다.<br/>
    /// 칩끼리는 <b>60노즐</b>씩 겹친다(도면의 Over-Lap, 칩1 A#341~400 ↔ 칩2 A#1~60).
    /// 그래서 칩 하나가 400 이어도 다음 칩은 340 만큼만 나아간다.
    /// </para>
    /// <para>
    /// 검산 — 마지막 노즐(칩4 B#400)의 격자 칸은 <c>3×680 + 799 = 2839</c>,
    /// 폭은 <c>2839 × 42.3333µm = 120.184mm</c>. 도면의 <c>120.184mm (2839/600inch)</c> 와
    /// 정확히 같다. 이 숫자가 안 맞으면 겹침이나 피치 중 하나가 틀린 것이다.
    /// </para>
    ///
    /// <para><b>★번호 규약은 아직 확정이 아니다</b>(2026-08-13). 헤드가 물리적으로 갖는 주소는
    /// <c>(칩, 열, 칩 안 번호)</c> 셋이고, 화면·레시피가 쓰는 1~3200 은 그 위에 얹은 <i>약속</i>일
    /// 뿐이다. 그래서 이 클래스는 셋을 기본으로 두고(<see cref="At"/>), 통짜 번호는
    /// <see cref="Numbering"/> 으로 갈아 끼우게 했다. 확정되면 <b>기본값만 바꾸면 된다</b> —
    /// 좌표 계산은 손댈 곳이 없다.</para>
    /// </summary>
    public sealed class ChipHeadLayout
    {
        /// <summary>
        /// 통짜 노즐 번호(1~3200)를 <c>(칩, 열, 칩 안 번호)</c> 에 대응시키는 방식.
        /// <b>어느 것이 실장과 맞는지는 스핏 한 줄로 확인해야 한다</b> — 틀리면 패턴이 통째로 재배치된다.
        /// </summary>
        public enum Numbering
        {
            /// <summary>칩1 A 전부 → 칩1 B 전부 → 칩2 A … (칩·열 단위로 뭉친 순서).</summary>
            ChipRowBlock,

            /// <summary>칩1 A#1, 칩1 B#1, 칩1 A#2 … (칩 안에서 A/B 를 번갈아 — 번호가 1 늘면 한 칸 옆).</summary>
            ChipInterleaved,

            /// <summary>
            /// 크로스스캔 위치 순. 겹침 구간에서는 <b>번호가 위치를 따라가지만</b> 같은 칸에 노즐이
            /// 둘이라 번호가 물리 주소와 1:1 이 아니게 된다 — 확인용이지 실장 후보는 아니다.
            /// </summary>
            ByPosition,
        }

        // ── 도면값 (Epson S3200 / PrecisionCore micro TFP) ────────────────────
        /// <summary>600npi 실효 피치[µm] = 25400/600.</summary>
        public const double S3200EffectivePitchUm = 25400.0 / 600.0;
        public const int    S3200ChipCount        = 4;
        public const int    S3200NozzlesPerRow    = 400;
        /// <summary>칩끼리 겹치는 노즐 수(열당). 도면 Over-Lap = A#341~A#400.</summary>
        public const int    S3200OverlapNozzles   = 60;
        /// <summary>칩 안 A–B 열 간 스캔방향 거리[µm] = 24/600 inch = 1.016mm.</summary>
        public const double S3200RowGapUm         = 1016.0;
        /// <summary>홀수 칩과 짝수 칩의 스캔방향 거리[µm] = 360/600 inch = 15.24mm.</summary>
        public const double S3200ChipGapUm        = 15240.0;

        /// <summary>도면 그대로의 S3200 배치.</summary>
        public static ChipHeadLayout S3200(Numbering order = Numbering.ChipRowBlock, int firstNozzleNumber = 1)
            => new(S3200ChipCount, S3200NozzlesPerRow, S3200OverlapNozzles,
                   S3200EffectivePitchUm, S3200RowGapUm, S3200ChipGapUm, order, firstNozzleNumber);

        public ChipHeadLayout(int chipCount, int nozzlesPerRow, int overlapNozzles,
                              double effectivePitchUm, double rowGapUm, double chipGapUm,
                              Numbering order = Numbering.ChipRowBlock, int firstNozzleNumber = 1)
        {
            if (chipCount <= 0)         throw new ArgumentOutOfRangeException(nameof(chipCount));
            if (nozzlesPerRow <= 0)     throw new ArgumentOutOfRangeException(nameof(nozzlesPerRow));
            if (effectivePitchUm <= 0)  throw new ArgumentOutOfRangeException(nameof(effectivePitchUm));
            if (overlapNozzles < 0 || overlapNozzles >= nozzlesPerRow)
                // 겹침이 열 전체면 다음 칩이 앞으로 나아가지 못해 폭이 늘지 않는다.
                throw new ArgumentOutOfRangeException(nameof(overlapNozzles),
                    $"겹침({overlapNozzles})은 0 이상 열 노즐 수({nozzlesPerRow}) 미만이어야 합니다.");

            ChipCount         = chipCount;
            NozzlesPerRow     = nozzlesPerRow;
            OverlapNozzles    = overlapNozzles;
            EffectivePitchUm  = effectivePitchUm;
            RowGapUm          = rowGapUm;
            ChipGapUm         = chipGapUm;
            Order             = order;
            FirstNozzleNumber = firstNozzleNumber;
        }

        public int       ChipCount         { get; }
        public int       NozzlesPerRow     { get; }
        public int       OverlapNozzles    { get; }
        public double    EffectivePitchUm  { get; }
        public double    RowGapUm          { get; }
        public double    ChipGapUm         { get; }
        public Numbering Order             { get; }
        public int       FirstNozzleNumber { get; }

        /// <summary>열 수 — A/B 두 줄 고정이다(도면의 2 Colors 도 이 두 열을 말한다).</summary>
        public const int Rows = 2;

        /// <summary>한 열 안 인접 노즐 간격[µm]. A/B 가 맞물려 실효 피치를 만드므로 그 두 배다.</summary>
        public double InRowPitchUm => EffectivePitchUm * Rows;

        /// <summary>칩 하나의 노즐 수(A+B).</summary>
        public int NozzlesPerChip => NozzlesPerRow * Rows;

        /// <summary>전체 노즐 수. S3200 = 3,200.</summary>
        public int TotalNozzles => NozzlesPerChip * ChipCount;

        /// <summary>다음 칩이 나아가는 노즐 수(열당). 겹치는 만큼 400 이 아니라 340 이다.</summary>
        public int ChipAdvanceNozzles => NozzlesPerRow - OverlapNozzles;

        /// <summary>다음 칩이 나아가는 격자 칸 수. A/B 두 열이라 노즐 수의 두 배.</summary>
        public int ChipAdvanceSlots => ChipAdvanceNozzles * Rows;

        /// <summary>
        /// 크로스스캔 격자 칸 수. 겹치는 노즐이 같은 칸을 쓰므로 <b>노즐 수보다 적다</b>
        /// (S3200 = 2,840 &lt; 3,200).
        /// </summary>
        public int SlotCount => (ChipCount - 1) * ChipAdvanceSlots + NozzlesPerChip;

        /// <summary>인쇄 폭[µm] — 첫 칸과 마지막 칸 사이. S3200 = 120,184µm.</summary>
        public double PrintWidthUm => (SlotCount - 1) * EffectivePitchUm;

        /// <summary>크로스스캔 해상도[dpi].</summary>
        public double EffectiveDpi => 25400.0 / EffectivePitchUm;

        // ── 물리 주소 → 위치 ──────────────────────────────────────────────────

        /// <summary>
        /// 칩·열·칩 안 번호로 위치를 구한다. <b>이것이 기본 주소</b>이고 통짜 번호는 그 위의 약속이다.
        /// </summary>
        /// <param name="chip">1-based 칩 번호.</param>
        /// <param name="indexInChip">1-based 칩 안 번호(A#1 의 1).</param>
        public NozzlePosition At(int chip, NozzleRow row, int indexInChip)
        {
            if (chip < 1 || chip > ChipCount)
                throw new ArgumentOutOfRangeException(nameof(chip), $"칩은 1~{ChipCount} 입니다.");
            if (indexInChip < 1 || indexInChip > NozzlesPerRow)
                throw new ArgumentOutOfRangeException(nameof(indexInChip), $"칩 안 번호는 1~{NozzlesPerRow} 입니다.");

            // 크로스스캔: 칩이 겹친 만큼만 나아가고, 칩 안에서는 A/B 가 한 칸씩 맞물린다.
            int slot = (chip - 1) * ChipAdvanceSlots + (indexInChip - 1) * Rows + (int)row;

            return new NozzlePosition(
                number:     NumberOf(chip, row, indexInChip),
                head:       0,
                row:        (int)row,
                indexInRow: indexInChip - 1,
                xUm:        slot * EffectivePitchUm,
                yUm:        ScanOffsetUm(chip, row),
                chip:       chip,
                slot:       slot);
        }

        /// <summary>
        /// 스캔 방향 위치[µm]. 짝수 칩이 <see cref="ChipGapUm"/> 앞서 있고,
        /// <b>짝수 칩은 A/B 좌우가 뒤집혀</b> 있다(도면: 칩1·3 은 A 가 왼쪽, 칩2·4 는 B 가 왼쪽).
        /// </summary>
        public double ScanOffsetUm(int chip, NozzleRow row)
        {
            bool evenChip = chip % 2 == 0;
            double chipBase = evenChip ? ChipGapUm : 0.0;

            // 뒤집힌 칩에서는 앞쪽 열이 B 다.
            bool isFrontRow = evenChip ? row == NozzleRow.B : row == NozzleRow.A;
            return chipBase + (isFrontRow ? 0.0 : RowGapUm);
        }

        // ── 통짜 번호 ↔ 물리 주소 ─────────────────────────────────────────────

        /// <summary>물리 주소 → 통짜 노즐 번호.</summary>
        public int NumberOf(int chip, NozzleRow row, int indexInChip)
        {
            int i = Order switch
            {
                Numbering.ChipRowBlock =>
                    (chip - 1) * NozzlesPerChip + (int)row * NozzlesPerRow + (indexInChip - 1),

                Numbering.ChipInterleaved =>
                    (chip - 1) * NozzlesPerChip + (indexInChip - 1) * Rows + (int)row,

                // 위치 순 — 같은 칸에 노즐이 둘이면 칩이 작은 쪽을 먼저 센다(정렬이 흔들리지 않게).
                Numbering.ByPosition => PositionRank(chip, row, indexInChip),

                _ => throw new NotSupportedException(Order.ToString()),
            };
            return FirstNozzleNumber + i;
        }

        /// <summary>통짜 번호 → 위치. 범위를 벗어나면 null.</summary>
        public NozzlePosition? PositionOf(int nozzleNumber)
        {
            int i = nozzleNumber - FirstNozzleNumber;
            if (i < 0 || i >= TotalNozzles) return null;

            if (Order == Numbering.ByPosition)
            {
                // 위치 순은 역산식이 없다 — 한 번 만들어 두고 찾는다(All 이 이미 순서대로다).
                var byRank = _positionOrder ??= BuildPositionOrder();
                var a = byRank[i];
                return At(a.Chip, a.Row, a.Index);
            }

            int chip = i / NozzlesPerChip + 1;
            int in_  = i % NozzlesPerChip;

            return Order == Numbering.ChipRowBlock
                ? At(chip, (NozzleRow)(in_ / NozzlesPerRow), in_ % NozzlesPerRow + 1)
                : At(chip, (NozzleRow)(in_ % Rows),          in_ / Rows + 1);
        }

        /// <summary>모든 노즐을 <b>번호 순</b>으로.</summary>
        public IEnumerable<NozzlePosition> All()
        {
            for (int n = 0; n < TotalNozzles; n++)
            {
                var p = PositionOf(FirstNozzleNumber + n);
                if (p.HasValue) yield return p.Value;
            }
        }

        /// <summary>모든 노즐을 <b>물리 주소 순</b>(칩 → 열 → 칩 안 번호)으로. 번호 규약과 무관하다.</summary>
        public IEnumerable<NozzlePosition> AllByAddress()
        {
            for (int c = 1; c <= ChipCount; c++)
                foreach (NozzleRow r in new[] { NozzleRow.A, NozzleRow.B })
                    for (int i = 1; i <= NozzlesPerRow; i++)
                        yield return At(c, r, i);
        }

        /// <summary>
        /// 겹치는 노즐 쌍 — 같은 격자 칸에 놓인 앞칩·뒷칩 노즐. <b>둘 다 쏘면 그 줄만 잉크가 두 배</b>가
        /// 되므로, 레시피가 한쪽을 고르거나 번갈아 쓰도록 정해야 한다.
        /// </summary>
        public IReadOnlyList<(NozzlePosition Earlier, NozzlePosition Later)> OverlapPairs()
        {
            var pairs = new List<(NozzlePosition, NozzlePosition)>();
            if (OverlapNozzles <= 0) return pairs;

            for (int c = 1; c < ChipCount; c++)
                foreach (NozzleRow r in new[] { NozzleRow.A, NozzleRow.B })
                    for (int k = 0; k < OverlapNozzles; k++)
                    {
                        // 앞칩의 끝 OverlapNozzles 개 ↔ 뒷칩의 처음 OverlapNozzles 개
                        var earlier = At(c,     r, ChipAdvanceNozzles + k + 1);
                        var later   = At(c + 1, r, k + 1);
                        pairs.Add((earlier, later));
                    }
            return pairs;
        }

        /// <summary>
        /// 사용 노즐을 크로스스캔 위치 순으로. <see cref="NozzleLayout.SortByX"/> 와 같은 규약 —
        /// 범위 밖 번호와 중복은 <paramref name="ignored"/> 로 돌려주고 조용히 버리지 않는다.
        /// </summary>
        public IReadOnlyList<NozzlePosition> SortByX(IEnumerable<int> nozzleNumbers, out IReadOnlyList<int> ignored)
        {
            if (nozzleNumbers == null) throw new ArgumentNullException(nameof(nozzleNumbers));

            var found = new List<NozzlePosition>();
            var bad   = new List<int>();
            var seen  = new HashSet<int>();

            foreach (int n in nozzleNumbers)
            {
                if (!seen.Add(n)) continue;
                var p = PositionOf(n);
                if (p.HasValue) found.Add(p.Value); else bad.Add(n);
            }

            ignored = bad;
            // 같은 칸이면 번호 순으로 — 겹침 구간에서 결과가 실행마다 달라지지 않게 한다.
            return found.OrderBy(p => p.Slot).ThenBy(p => p.Number).ToList();
        }

        // ── 위치 순 번호 매기기 ───────────────────────────────────────────────
        private (int Chip, NozzleRow Row, int Index)[]? _positionOrder;

        private (int Chip, NozzleRow Row, int Index)[] BuildPositionOrder()
            => AllByAddressRaw()
               .OrderBy(a => SlotOf(a.Chip, a.Row, a.Index))
               .ThenBy(a => a.Chip)
               .ThenBy(a => (int)a.Row)
               .ToArray();

        private IEnumerable<(int Chip, NozzleRow Row, int Index)> AllByAddressRaw()
        {
            for (int c = 1; c <= ChipCount; c++)
                foreach (NozzleRow r in new[] { NozzleRow.A, NozzleRow.B })
                    for (int i = 1; i <= NozzlesPerRow; i++)
                        yield return (c, r, i);
        }

        private int SlotOf(int chip, NozzleRow row, int indexInChip)
            => (chip - 1) * ChipAdvanceSlots + (indexInChip - 1) * Rows + (int)row;

        // 주소 → 순위. 선형 탐색이면 전체를 한 번 만드는 데 3200² 번 비교한다.
        private Dictionary<int, int>? _rankByAddress;

        private int PositionRank(int chip, NozzleRow row, int indexInChip)
        {
            if (_rankByAddress == null)
            {
                var order = _positionOrder ??= BuildPositionOrder();
                _rankByAddress = new Dictionary<int, int>(order.Length);
                for (int i = 0; i < order.Length; i++)
                    _rankByAddress[AddressKey(order[i].Chip, order[i].Row, order[i].Index)] = i;
            }
            return _rankByAddress.TryGetValue(AddressKey(chip, row, indexInChip), out int rank) ? rank : 0;
        }

        private int AddressKey(int chip, NozzleRow row, int indexInChip)
            => (chip * Rows + (int)row) * (NozzlesPerRow + 1) + indexInChip;
    }
}
