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

        /// <summary>장비 설정을 고친 뒤 부른다. 다음 접근에서 다시 읽는다.</summary>
        public static void Reload() { _count = null; _rows = null; }

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
