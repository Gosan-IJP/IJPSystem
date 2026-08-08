using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Print
{
    /// <summary>RIP 결과 — PCC 로 내려보낼 발사 지도.</summary>
    public sealed class PrintPattern
    {
        /// <summary>[스텝, 컬럼] 방울 단계. 가로 한 줄 = 그 순간 각 노즐이 쏠 크기, 세로 = 진행 순서.</summary>
        public byte[,] Levels { get; init; } = new byte[0, 0];

        /// <summary>컬럼이 어느 노즐인지 — <see cref="Levels"/> 의 열 순서와 1:1.</summary>
        public IReadOnlyList<NozzlePosition> Columns { get; init; } = Array.Empty<NozzlePosition>();

        /// <summary>스캔 방향 한 스텝의 이동량 [µm]. 엔코더 한 칸에 해당한다.</summary>
        public double ScanStepUm { get; init; }

        public int Steps   => Levels.GetLength(0);
        public int Nozzles => Levels.GetLength(1);
    }

    /// <summary>RIP 입력 파라미터.</summary>
    public sealed class RipSettings
    {
        /// <summary>방울 단계 수(2 이상). 헤드가 낼 수 있는 크기 단계.</summary>
        public int DropLevels { get; init; } = 2;

        /// <summary>스캔 방향 한 스텝 이동량 [µm]. 보통 크로스스캔 실효 간격과 같게 둬 정사각 격자를 만든다.</summary>
        public double ScanStepUm { get; init; }

        /// <summary>이미지 좌상단이 놓일 크로스스캔 위치 [µm]. 노즐 X 와 같은 기준.</summary>
        public double OriginXUm { get; init; }

        /// <summary>
        /// 헤드 이음새 섞기 사용 여부. (LabVIEW "Amount of Overlap" / 오버랩 디더링)
        ///
        /// <para>
        /// 겹치는 <b>폭</b>은 따로 주지 않는다 — 노즐 X 좌표에서 실제로 겹치는 구간을 그대로
        /// 쓴다. 랩뷰처럼 "겹치는 노즐 수"로 주면 그 값과 실제 배치가 어긋났을 때 이음새가
        /// 이중으로 찍히거나 비는데, 위치에서 구하면 그 불일치 자체가 생기지 않는다.
        /// (겹치는 노즐 수를 바꾸고 싶으면 헤드 간격 <c>HeadPitchUm</c> 을 바꾸면 된다)
        /// </para>
        /// </summary>
        public bool BlendHeadSeams { get; init; } = true;
    }

    /// <summary>
    /// 이미지를 <b>노즐 발사 지도</b>로 바꾼다. (LabVIEW "Print Pattern GEN S800" 대응)
    ///
    /// <para><b>순서</b>: 노즐 위치에서 이미지 샘플링 → 이음새 가중 → 디더링 → 지도.</para>
    ///
    /// <para>
    /// 샘플링을 <b>노즐 위치 기준</b>으로 하는 것이 핵심이다. 이미지를 균일 격자로 리샘플한 뒤
    /// 노즐에 나눠주면, 안 쓰는 노즐이나 헤드 사이 빈틈이 있을 때 그림이 그만큼 밀린다.
    /// 각 노즐이 "자기 X 좌표의 픽셀"을 직접 읽으면 노즐이 빠지든 헤드가 떨어져 있든
    /// 그림은 제자리에 남는다.
    /// </para>
    /// <para>
    /// 디더링은 <b>리샘플한 뒤</b> 해야 한다 — 오차 확산은 실제로 찍히는 격자 위에서 이웃에게
    /// 오차를 넘겨야 의미가 있다. 원본에서 디더링하고 리샘플하면 점 배치가 뭉개져 농도가 틀어진다.
    /// </para>
    /// </summary>
    public static class PrintPatternBuilder
    {
        /// <param name="gray">[y, x] 원본 이미지. 값이 클수록 진하다.</param>
        /// <param name="umPerPixelX">원본 가로 1픽셀의 실제 크기 [µm].</param>
        /// <param name="umPerPixelY">원본 세로 1픽셀의 실제 크기 [µm].</param>
        /// <param name="layout">노즐 배열.</param>
        /// <param name="usedNozzles">쓸 노즐 번호. 나머지 자리는 비운다.</param>
        /// <param name="settings">RIP 파라미터.</param>
        /// <param name="ignoredNozzles">범위를 벗어나 버려진 노즐 번호.</param>
        public static PrintPattern Build(byte[,] gray, double umPerPixelX, double umPerPixelY,
                                         NozzleLayout layout, IEnumerable<int> usedNozzles,
                                         RipSettings settings, out IReadOnlyList<int> ignoredNozzles)
        {
            if (gray     == null) throw new ArgumentNullException(nameof(gray));
            if (layout   == null) throw new ArgumentNullException(nameof(layout));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (umPerPixelX <= 0) throw new ArgumentOutOfRangeException(nameof(umPerPixelX));
            if (umPerPixelY <= 0) throw new ArgumentOutOfRangeException(nameof(umPerPixelY));

            double scanStep = settings.ScanStepUm > 0 ? settings.ScanStepUm : layout.EffectivePitchUm;
            if (scanStep <= 0) throw new ArgumentOutOfRangeException(nameof(settings), "스캔 스텝을 정할 수 없다.");

            // 컬럼은 번호 순이 아니라 X 순 — 지도의 왼쪽이 실제로 왼쪽이어야 한다.
            var columns = layout.SortByX(usedNozzles, out ignoredNozzles);
            int nCol = columns.Count;

            var sampled = SampleToNozzleGrid(gray, umPerPixelX, umPerPixelY, columns,
                                             settings.OriginXUm, scanStep, settings.BlendHeadSeams);

            return new PrintPattern
            {
                Levels     = sampled.Length == 0 ? sampled : Halftone.ErrorDiffuse(sampled, settings.DropLevels),
                Columns    = columns,
                ScanStepUm = scanStep,
            };
        }

        /// <summary>
        /// 이미지를 <b>노즐 격자</b>로 뽑는다 — 각 노즐이 자기 X 좌표의 픽셀을 읽는다.
        /// 디더링 전 단계라 값은 여전히 0~255 농담이다.
        ///
        /// <para>
        /// 따로 낸 이유: 여기서 나온 격자가 "무엇을 찍으려 하는가" 이고, 디더링은 그걸 헤드가
        /// 낼 수 있는 단계로 낮추는 별개의 일이다. 인쇄가 밀렸을 때 매핑이 문제인지 디더링이
        /// 문제인지 이 단계만 떼어 보면 갈린다.
        /// </para>
        /// </summary>
        public static byte[,] SampleToNozzleGrid(byte[,] gray, double umPerPixelX, double umPerPixelY,
                                                 IReadOnlyList<NozzlePosition> columns,
                                                 double originXUm, double scanStepUm, bool blendSeams)
        {
            if (gray    == null) throw new ArgumentNullException(nameof(gray));
            if (columns == null) throw new ArgumentNullException(nameof(columns));
            if (umPerPixelX <= 0) throw new ArgumentOutOfRangeException(nameof(umPerPixelX));
            if (umPerPixelY <= 0) throw new ArgumentOutOfRangeException(nameof(umPerPixelY));
            if (scanStepUm  <= 0) throw new ArgumentOutOfRangeException(nameof(scanStepUm));

            int srcH = gray.GetLength(0), srcW = gray.GetLength(1);
            int nCol = columns.Count;
            int steps = nCol == 0 ? 0 : Math.Max(0, (int)Math.Ceiling(srcH * umPerPixelY / scanStepUm));

            var grid = new byte[steps, nCol];
            if (steps == 0 || nCol == 0) return grid;

            var seam = SeamWeights(columns, blendSeams);

            for (int s = 0; s < steps; s++)
            {
                // 스텝 중앙에서 뽑는다 — 경계에서 뽑으면 반올림 방향에 따라 한 줄이 통째로 밀린다.
                int sy = (int)((s + 0.5) * scanStepUm / umPerPixelY);
                if (sy < 0 || sy >= srcH) continue;

                for (int c = 0; c < nCol; c++)
                {
                    double xUm = columns[c].XUm - originXUm;
                    if (xUm < 0) continue;
                    int sx = (int)(xUm / umPerPixelX);
                    if (sx < 0 || sx >= srcW) continue;      // 이미지 밖 노즐은 안 쏜다

                    grid[s, c] = (byte)Math.Clamp((int)Math.Round(gray[sy, sx] * seam[c]), 0, 255);
                }
            }
            return grid;
        }

        /// <summary>
        /// 헤드 이음새 가중치. (LabVIEW "Amount of Overlap" / 오버랩 디더링)
        ///
        /// <para>
        /// 헤드가 여러 개면 이음새에서 두 헤드의 노즐이 <b>같은 자리</b>를 덮는다. 그대로 두면
        /// 그 띠만 잉크가 두 배로 들어가 진한 줄이 생긴다. 겹치는 구간에서 왼쪽 헤드는
        /// 1→0 으로 빠지고 오른쪽 헤드는 0→1 로 들어와, 어느 X 에서든 <b>합이 1</b> 이 되게 한다.
        /// </para>
        /// <para>
        /// 노즐 <b>개수</b>가 아니라 <b>X 좌표</b>로 계산하는 이유: 두 헤드가 실제로 겹치면
        /// X 순 컬럼에서 두 헤드의 노즐이 번갈아 섞여 나온다. 개수로 세면 "헤드가 바뀌는 지점"
        /// 자체를 잡을 수 없고, 겹치는 폭도 배치와 어긋날 수 있다. 위치로 재면 둘 다 사라진다.
        /// </para>
        /// <para>
        /// 헤드가 1개거나 서로 안 겹치면 전부 1 이다.
        /// </para>
        /// </summary>
        public static double[] SeamWeights(IReadOnlyList<NozzlePosition> columns, bool blend)
        {
            var w = new double[columns.Count];
            for (int i = 0; i < w.Length; i++) w[i] = 1.0;
            if (!blend || columns.Count == 0) return w;

            // 헤드별 X 범위. 이게 서로 물리면 그 구간이 이음새다.
            var span = columns
                .GroupBy(c => c.Head)
                .ToDictionary(g => g.Key, g => (Min: g.Min(c => c.XUm), Max: g.Max(c => c.XUm)));

            var heads = span.Keys.OrderBy(h => span[h].Min).ToList();

            for (int i = 1; i < heads.Count; i++)
            {
                int left = heads[i - 1], right = heads[i];
                double from = span[right].Min, to = span[left].Max;
                if (to <= from) continue;                    // 안 겹친다 — 섞을 것이 없다

                double width = to - from;
                for (int c = 0; c < columns.Count; c++)
                {
                    double x = columns[c].XUm;
                    if (x < from || x > to) continue;

                    if (columns[c].Head == left)  w[c] = Math.Min(w[c], (to - x) / width);
                    if (columns[c].Head == right) w[c] = Math.Min(w[c], (x - from) / width);
                }
            }
            return w;
        }
    }
}
