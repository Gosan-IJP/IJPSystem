using System;
using System.Collections.Generic;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 드랍와쳐 스핏 설정. (화면: Nozzle select, Spit frequency (Hz))
    /// LabVIEW 5_WIZ_Set Nozzle and WF with DW.vi 의 "Spit DW" 파라미터 묶음.
    /// </summary>
    public sealed class SpitSettings
    {
        /// <summary>토출시킬 노즐 번호 목록. (화면: Nozzle select)</summary>
        public IReadOnlyList<int> Nozzles { get; set; } = Array.Empty<int>();

        /// <summary>스핏 주파수[Hz]. (화면: Spit frequency)</summary>
        public double FrequencyHz { get; set; } = 1000;

        /// <summary>토출 농도. (Meteor: CPEX_SpitGreyLevel)</summary>
        public int SpitGreyLevel { get; set; } = 255;

        /// <summary>틱클 농도 — 미토출 노즐의 메니스커스 유지용 미세 구동. (CPEX_TickleGreyLevel)</summary>
        public int TickleGreyLevel { get; set; } = 0;

        /// <summary>페이지 스핏 간격/픽셀. (CPEX_PageSpitInterval / CPEX_PageSpitPixel)</summary>
        public int PageSpitInterval { get; set; }
        public int PageSpitPixel { get; set; }

        /// <summary>헤드 구동 전압. null 이면 현재 설정 유지. (CPEX_HeadVoltage)</summary>
        public int? HeadVoltage { get; set; }
    }

    /// <summary>
    /// 스핏 패턴 이미지 생성기.
    /// LabVIEW 는 IMAQ ArrayToImage 로 (노즐 선택 × 주파수) → 패턴 이미지를 만든다.
    /// 헤드 기종별 배열 규칙이 다르므로 인터페이스로 분리.
    /// </summary>
    public interface ISpitPatternBuilder
    {
        /// <summary>노즐 선택으로부터 스핏 패턴 비트맵(U8, [row, nozzle])을 생성.</summary>
        byte[,] Build(SpitSettings settings);

        /// <summary>헤드의 전체 노즐 수 — 선택값 유효범위 검증용.</summary>
        int NozzleCount { get; }
    }

    /// <summary>
    /// S800 단일 헤드용 스핏 패턴. (Make DW Spit PAT for S800 Single.vi 대응)
    ///
    /// ※ 노즐 번호 기준(<see cref="FirstNozzleIndex"/>)에 주의할 것.
    ///   화면/레시피는 1번부터 세는데(HMI 의 SelectedNozzleInfo.ToEnableMask 기본값이 1),
    ///   패턴 배열은 0부터다. 이 값이 어긋나면 <b>패턴 전체가 한 칸씩 밀려</b> 엉뚱한 노즐이 토출된다.
    ///   기본값은 화면 규약에 맞춰 1로 둔다. 실장 헤드가 0부터 세면 0으로 바꿀 것.
    ///
    /// ※ 실제 노즐↔픽셀 매핑 규칙(홀짝 배열/로우 분할 등)은 헤드 사양에 맞춰 확정해야 한다.
    ///   현재는 "노즐 n → 컬럼 n" 직선 매핑이다.
    /// </summary>
    public sealed class S800SingleSpitPatternBuilder : ISpitPatternBuilder
    {
        private readonly int _rows;

        public S800SingleSpitPatternBuilder(int nozzleCount, int rows = 1, int firstNozzleIndex = 1)
        {
            if (nozzleCount <= 0) throw new ArgumentOutOfRangeException(nameof(nozzleCount));
            NozzleCount      = nozzleCount;
            _rows            = Math.Max(1, rows);
            FirstNozzleIndex = firstNozzleIndex;
        }

        public int NozzleCount      { get; }
        public int FirstNozzleIndex { get; }

        /// <summary>범위를 벗어나 무시된 노즐 번호(마지막 Build 기준). 조용히 버리지 않도록 노출한다.</summary>
        public IReadOnlyList<int> LastIgnoredNozzles { get; private set; } = Array.Empty<int>();

        public byte[,] Build(SpitSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));

            var pat = new byte[_rows, NozzleCount];
            var ignored = new List<int>();
            if (s.Nozzles == null || s.Nozzles.Count == 0)
            {
                LastIgnoredNozzles = ignored;
                return pat;
            }

            byte level = (byte)Math.Clamp(s.SpitGreyLevel, 0, 255);
            byte tickle = (byte)Math.Clamp(s.TickleGreyLevel, 0, 255);

            // 틱클: 선택되지 않은 노즐도 0이 아닌 미세 구동으로 채운다(메니스커스 유지).
            if (tickle > 0)
                for (int r = 0; r < _rows; r++)
                    for (int c = 0; c < NozzleCount; c++)
                        pat[r, c] = tickle;

            foreach (int nz in s.Nozzles)
            {
                int col = nz - FirstNozzleIndex;
                if (col < 0 || col >= NozzleCount) { ignored.Add(nz); continue; }
                for (int r = 0; r < _rows; r++) pat[r, col] = level;
            }

            LastIgnoredNozzles = ignored;
            return pat;
        }

        /// <summary>패턴에서 실제 토출(= 틱클보다 진한) 컬럼 수. 검증/로그용.</summary>
        public static int CountSpittingColumns(byte[,] pattern, int spitLevel, int tickleLevel)
        {
            if (pattern == null) return 0;
            int rows = pattern.GetLength(0), cols = pattern.GetLength(1);
            int n = 0;
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    if (pattern[r, c] == spitLevel && spitLevel > tickleLevel) { n++; break; }
            return n;
        }
    }
}
