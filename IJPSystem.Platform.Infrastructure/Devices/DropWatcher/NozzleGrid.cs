using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>검출 액적 1개와 그것이 배정된 노즐 번호.</summary>
    public sealed class MappedNozzle
    {
        /// <summary>배정된 노즐 번호(화면/레시피와 같은 1-based 실번호).</summary>
        public int NozzleNumber { get; set; }

        /// <summary>격자상의 0-based 위치. 불토출이 있으면 리스트 인덱스와 어긋난다.</summary>
        public int GridIndex { get; set; }

        public DropletInfo Drop { get; set; } = null!;

        /// <summary>
        /// 이상적인 격자 X 위치에서 벗어난 정도[px]. 부호 유지(+는 오른쪽).
        /// 토출 방향이 틀어지면 여기에 나타나므로 직진성 지표로 쓸 수 있다.
        /// </summary>
        public double GridDeviationPixel { get; set; }
    }

    /// <summary>노즐 격자 매핑 결과.</summary>
    public sealed class NozzleGridResult
    {
        /// <summary>검출되어 노즐 번호가 배정된 액적들(노즐 번호 오름차순).</summary>
        public IReadOnlyList<MappedNozzle> Mapped { get; set; } = Array.Empty<MappedNozzle>();

        /// <summary>토출을 기대했으나 액적이 없는 노즐 번호.</summary>
        public IReadOnlyList<int> MissingNozzles { get; set; } = Array.Empty<int>();

        /// <summary>격자 간격[px] — 설정 피치와 µm/px 로 산출한 기대값.</summary>
        public double PitchPixel { get; set; }

        /// <summary>
        /// 절대 노즐 번호 배정을 신뢰할 수 있는지.
        /// <b>false 면 "몇 번이 빠졌다"가 아니라 "어딘가 빠졌다"까지만 유효하다.</b>
        /// 영상만으로는 기준점(fiducial)이 없어, 양 끝 노즐이 불토출이면 격자 전체가 밀린 것인지
        /// 구분할 수 없기 때문이다.
        /// </summary>
        public bool AbsoluteMappingConfident { get; set; }

        /// <summary>신뢰할 수 없는 경우의 사유(없으면 null).</summary>
        public string? Ambiguity { get; set; }

        /// <summary>격자 이탈이 가장 큰 액적의 편차[px]. 직진성 이상 판단용.</summary>
        public double MaxDeviationPixel { get; set; }
    }

    /// <summary>
    /// 검출된 액적을 <b>실제 노즐 번호</b>에 배정한다.
    ///
    /// <b>왜 필요한가</b>: <see cref="DropWatcherProcessor.DetectDroplets"/> 는 X 오름차순 리스트만
    /// 준다. 이걸 그대로 쓰면 리스트 인덱스 = 노즐 순번이라고 가정하는 셈인데, 중간에 불토출
    /// 노즐이 하나라도 있으면 <b>그 뒤 노즐이 전부 한 칸씩 당겨져 엉뚱한 노즐을 지목</b>하게 된다.
    /// (예: 3번이 안 나오면 4번 액적이 3번으로 보고된다)
    ///
    /// 해결: 노즐 피치로 기대 격자를 만들고 각 액적을 가장 가까운 격자점에 스냅한다.
    /// 비어 있는 격자점이 곧 불토출 노즐이다.
    /// </summary>
    public static class NozzleGrid
    {
        /// <summary>액적을 격자점으로 인정하는 최대 이탈(격자 간격 대비 비율).</summary>
        private const double SnapToleranceRatio = 0.4;

        /// <summary>
        /// 액적을 노즐 번호에 매핑한다.
        /// </summary>
        /// <param name="drops">DetectDroplets 결과(X 오름차순).</param>
        /// <param name="expectedNozzles">토출을 지시한 노즐 번호 목록(Nozzle Select 결과). 오름차순 가정.</param>
        /// <param name="nozzlePitchUm">인접 노즐 실제 간격[µm].</param>
        /// <param name="micronsPerPixel">교정된 µm/px.</param>
        public static NozzleGridResult Map(
            IReadOnlyList<DropletInfo> drops,
            IReadOnlyList<int> expectedNozzles,
            double nozzlePitchUm,
            double micronsPerPixel)
        {
            var expected = (expectedNozzles ?? Array.Empty<int>()).OrderBy(n => n).ToList();

            if (nozzlePitchUm <= 0 || micronsPerPixel <= 0)
                return new NozzleGridResult
                {
                    MissingNozzles = expected,
                    Ambiguity = "노즐 피치 또는 µm/px 가 설정되지 않아 격자를 만들 수 없습니다.",
                };

            double pitchPx = nozzlePitchUm / micronsPerPixel;

            if (drops == null || drops.Count == 0)
                return new NozzleGridResult
                {
                    PitchPixel = pitchPx,
                    MissingNozzles = expected,
                    Ambiguity = expected.Count > 0 ? "액적이 하나도 검출되지 않았습니다." : null,
                };

            var sorted = drops.OrderBy(d => d.CentroidXPixel).ToList();
            double x0 = sorted[0].CentroidXPixel;

            // 첫 액적을 격자 0번으로 두고 상대 인덱스를 매긴다.
            // 내부 불토출은 인덱스에 구멍으로 남으므로 그대로 검출된다.
            var slots = new List<(int idx, DropletInfo drop, double devPx)>();
            foreach (var d in sorted)
            {
                double offset = (d.CentroidXPixel - x0) / pitchPx;
                int idx = (int)Math.Round(offset);
                double devPx = (offset - idx) * pitchPx;
                slots.Add((idx, d, devPx));
            }

            // 같은 격자점에 둘 이상이 몰리면(위성 오검출 등) 이탈이 작은 쪽만 남긴다.
            var byIdx = slots
                .GroupBy(s => s.idx)
                .Select(g => g.OrderBy(s => Math.Abs(s.devPx)).First())
                .OrderBy(s => s.idx)
                .ToList();

            int span = byIdx[^1].idx + 1;   // 점유한 격자 슬롯 수

            // 절대 번호 확신 조건: 점유 폭이 기대 노즐 수와 정확히 같을 것.
            // 폭이 더 좁으면 양 끝 중 어느 쪽이 빠진 것인지 영상만으로는 알 수 없다.
            bool confident = expected.Count > 0 && span == expected.Count;
            string? ambiguity = null;
            if (expected.Count == 0)
                ambiguity = "기대 노즐 목록이 없어 절대 번호를 배정할 수 없습니다.";
            else if (span > expected.Count)
                ambiguity = $"격자 점유 폭({span})이 기대 노즐 수({expected.Count})보다 큽니다. " +
                            "노즐 피치·µm/px 교정값 또는 오검출을 확인하세요.";
            else if (span < expected.Count)
                ambiguity = $"격자 점유 폭({span})이 기대 노즐 수({expected.Count})보다 작습니다. " +
                            "양 끝 노즐이 불토출이면 번호가 통째로 밀릴 수 있어 절대 번호는 참고값입니다.";

            var mapped = new List<MappedNozzle>();
            double maxDev = 0;
            foreach (var (idx, drop, devPx) in byIdx)
            {
                // 격자에서 지나치게 벗어난 것은 노즐 액적으로 보지 않는다(반사/위성 등).
                if (Math.Abs(devPx) > pitchPx * SnapToleranceRatio) continue;

                int nozzle = idx >= 0 && idx < expected.Count
                    ? expected[idx]
                    : (expected.Count > 0 ? expected[0] + idx : idx);   // 목록 밖이면 번호 외삽

                mapped.Add(new MappedNozzle
                {
                    NozzleNumber       = nozzle,
                    GridIndex          = idx,
                    Drop               = drop,
                    GridDeviationPixel = devPx,
                });
                maxDev = Math.Max(maxDev, Math.Abs(devPx));
            }

            var hit = mapped.Select(m => m.NozzleNumber).ToHashSet();
            var missing = expected.Where(n => !hit.Contains(n)).ToList();

            return new NozzleGridResult
            {
                Mapped                   = mapped,
                MissingNozzles           = missing,
                PitchPixel               = pitchPx,
                AbsoluteMappingConfident = confident,
                Ambiguity                = ambiguity,
                MaxDeviationPixel        = maxDev,
            };
        }
    }
}
