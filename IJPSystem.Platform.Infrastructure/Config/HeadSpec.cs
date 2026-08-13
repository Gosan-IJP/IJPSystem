using System;

namespace IJPSystem.Platform.Infrastructure.Config
{
    /// <summary>
    /// 프린트 헤드의 노즐 사양. 화면·파서·패턴 생성이 <b>모두 여기 하나</b>를 봐야 한다.
    ///
    /// <para>
    /// 예전에는 같은 값이 세 군데 따로 박혀 있었다 — AppConstants 는 128, DXF Rasterizer 는
    /// 400×2=800, 패턴 미리보기는 798. 노즐 선택 화면에서 129번 이상이 조용히 무시되는데
    /// 다른 화면은 800개를 그리고 있었다(2026-08-09). 값이 갈리면 어느 화면이 맞는지 알 수 없다.
    /// </para>
    /// <para>
    /// 값은 장비 설정(<see cref="MachineSettings"/>, MachineData.db)에서 읽고, 없으면 아래
    /// 기본값을 쓴다. 헤드가 바뀌면 레시피 화면의 [노즐 정보]에서 고치면 되고 코드는 그대로다.
    /// </para>
    /// </summary>
    public static class HeadSpec
    {
        // S800 기준 기본값 — 2열 × 400. 실제 헤드 사양이 확인되면 장비 설정에 넣어 덮는다.
        // (사용자 결정 2026-08-09: 우선 800 으로 두고 헤드 정보에 따라 바꿀 수 있게 한다)
        public const int DefaultCount = 800;
        public const int DefaultRows  = 2;

        /// <summary>노즐 번호 시작값. 화면·레시피·파서 모두 1번부터 센다(0번 없음).</summary>
        public const int FirstNozzle = 1;

        // 매번 DB 를 읽으면 화면 갱신마다 질의가 나간다 — 한 번 읽고 들고 있다가
        // 설정이 바뀌면 Reload 로 버린다.
        private static int? _count, _rows;

        /// <summary>헤드 전체 노즐 수.</summary>
        public static int Count => _count ??= ReadInt(MachineSettingsStore.Keys.NozzleCount, DefaultCount);

        /// <summary>노즐 열 수.</summary>
        public static int Rows => _rows ??= ReadInt(MachineSettingsStore.Keys.NozzleRows, DefaultRows);

        /// <summary>마지막 노즐 번호.</summary>
        public static int LastNozzle => FirstNozzle + Count - 1;

        /// <summary>한 열의 노즐 수. 나누어떨어지지 않으면 올림 — 뒤쪽 노즐이 빠지지 않게.</summary>
        public static int NozzlesPerRow => (int)Math.Ceiling(Count / (double)Math.Max(1, Rows));

        // ── 칩이 엇갈린 헤드 (Epson S3200) ────────────────────────────────────
        // 칩 수가 1 이면 지금까지의 헤드(S800)와 완전히 같게 동작한다 — 설정을 넣지 않은
        // 장비는 아무 영향을 받지 않는다. 값을 넣는 순간부터 칩 배치가 쓰인다.

        private static int? _chipCount;

        /// <summary>헤드 안의 칩 수. 1 이면 칩 없는 헤드.</summary>
        public static int ChipCount => _chipCount ??= ReadInt(MachineSettingsStore.Keys.HeadChipCount, 1);

        /// <summary>칩이 엇갈린 헤드인가 — 이게 true 라야 <see cref="ChipLayout"/> 이 의미를 갖는다.</summary>
        public static bool HasChips => ChipCount > 1;

        private static int? _nozzlesPerChipRow;

        /// <summary>
        /// <b>칩 하나의 한 열</b> 노즐 수. 설정이 없으면 전체를 칩·열로 나눠 되돌린다
        /// (칩 없는 헤드에서는 <see cref="NozzlesPerRow"/> 와 같은 값이 된다).
        /// </summary>
        public static int NozzlesPerChipRow =>
            _nozzlesPerChipRow ??= ReadInt(
                MachineSettingsStore.Keys.HeadNozzlesPerRow,
                (int)Math.Ceiling(Count / (double)Math.Max(1, ChipCount * Rows)));

        /// <summary>
        /// 노즐 선택 막대가 그릴 <b>줄 수</b> = 칩 수 × 열 수.
        ///
        /// <para>S3200 은 4칩 × 2열이라 <b>8줄</b>이 된다. 전체를 2줄로만 그리면 한 줄에 1,600개가
        /// 들어가 칩 경계가 안 보이고, 겹치는 60개가 어디인지도 짚을 수 없다. 화면이 헤드 생김새와
        /// 같아야 "칩3 A열만 끄기" 같은 조작이 눈으로 된다.</para>
        /// </summary>
        public static int SelectionRows => Math.Max(1, ChipCount * Rows);

        /// <summary>
        /// 선택 막대 한 줄에 들어가는 노즐 수. 줄 수로 나눈 값이라
        /// <see cref="SelectionRows"/> 와 곱하면 전체를 덮는다.
        /// </summary>
        public static int NozzlesPerSelectionRow =>
            (int)Math.Ceiling(Count / (double)SelectionRows);

        /// <summary>
        /// 칩 배치. <see cref="HasChips"/> 가 false 면 null — 칩 없는 헤드에 억지로 칩 모델을
        /// 씌우면 좌표가 조용히 달라진다(겹침 0, 칩 1개짜리도 "칩 헤드"로 보이게 된다).
        ///
        /// <para>값이 하나라도 빠져 있으면 S3200 도면값으로 채운다 — 지금 도입하는 헤드가
        /// 그것뿐이라, 부분 입력이 엉뚱한 폭으로 조용히 굳는 것보다 낫다.</para>
        /// </summary>
        public static Print.ChipHeadLayout? ChipLayout
        {
            get
            {
                if (!HasChips) return null;
                return _chipLayout ??= BuildChipLayout();
            }
        }

        private static Print.ChipHeadLayout? _chipLayout;

        private static Print.ChipHeadLayout BuildChipLayout()
        {
            var order = ParseNumbering(ReadString(MachineSettingsStore.Keys.HeadNozzleNumbering));

            return new Print.ChipHeadLayout(
                chipCount:        ChipCount,
                nozzlesPerRow:    ReadInt(MachineSettingsStore.Keys.HeadNozzlesPerRow,
                                          Print.ChipHeadLayout.S3200NozzlesPerRow),
                overlapNozzles:   ReadInt(MachineSettingsStore.Keys.HeadOverlapNozzles,
                                          Print.ChipHeadLayout.S3200OverlapNozzles),
                effectivePitchUm: ReadDouble(MachineSettingsStore.Keys.HeadPitchUm,
                                             Print.ChipHeadLayout.S3200EffectivePitchUm),
                rowGapUm:         ReadDouble(MachineSettingsStore.Keys.HeadRowGapUm,
                                             Print.ChipHeadLayout.S3200RowGapUm),
                chipGapUm:        ReadDouble(MachineSettingsStore.Keys.HeadChipGapUm,
                                             Print.ChipHeadLayout.S3200ChipGapUm),
                order:            order,
                firstNozzleNumber: FirstNozzle);
        }

        /// <summary>
        /// 번호 규약 문자열 해석. <b>모르는 값이면 기본값으로 조용히 넘어가지 않는다</b> —
        /// 오타 하나로 패턴이 통째로 재배치되는데 화면에는 아무 표시가 없다.
        /// </summary>
        public static Print.ChipHeadLayout.Numbering ParseNumbering(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Print.ChipHeadLayout.Numbering.ChipRowBlock;
            if (Enum.TryParse<Print.ChipHeadLayout.Numbering>(value.Trim(), ignoreCase: true, out var parsed))
                return parsed;

            throw new ArgumentException(
                $"알 수 없는 노즐 번호 규약 '{value}'. " +
                $"{string.Join(" / ", Enum.GetNames(typeof(Print.ChipHeadLayout.Numbering)))} 중 하나여야 합니다.");
        }

        /// <summary>장비 설정을 고친 뒤 부른다. 다음 접근에서 다시 읽는다.</summary>
        public static void Reload()
        {
            _count = null; _rows = null;
            _chipCount = null; _chipLayout = null; _nozzlesPerChipRow = null;
        }

        private static double ReadDouble(string key, double fallback)
        {
            if (!MachineSettings.IsReady) return fallback;
            try
            {
                double v = MachineSettings.Current.GetDouble(key, fallback);
                return v > 0 ? v : fallback;
            }
            catch { return fallback; }
        }

        private static string ReadString(string key)
        {
            if (!MachineSettings.IsReady) return "";
            try { return MachineSettings.Current.GetString(key, ""); }
            catch { return ""; }
        }

        private static int ReadInt(string key, int fallback)
        {
            if (!MachineSettings.IsReady) return fallback;
            try
            {
                int v = MachineSettings.Current.GetInt(key, fallback);
                return v > 0 ? v : fallback;      // 0·음수가 들어 있으면 기본값 — 화면이 비어 버린다
            }
            catch { return fallback; }
        }
    }
}
