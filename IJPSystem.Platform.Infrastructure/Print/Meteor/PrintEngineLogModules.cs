using System;
using System.Collections.Generic;
using System.Globalization;

namespace IJPSystem.Platform.Infrastructure.Print.Meteor
{
    /// <summary>
    /// 엔진 로그 상세 항목. cfg <c>[Test] LogCtrlBits</c> 의 비트 하나씩에 대응한다.
    ///
    /// <para><b>비트 위치는 확인이 필요하다</b> — 매뉴얼에 배치가 없어서 Meteor Monitor
    /// 화면의 나열 순서대로 0번부터 붙였다. 체크박스를 하나씩 켜면서 엔진 로그의
    /// <c>UpdateDebugSettings() LogCtrlBits:0x…</c> 값과 대조해 확정할 것.
    /// 어긋나 있어도 로그가 더/덜 나올 뿐 장비 동작에는 영향이 없다.</para>
    /// </summary>
    [Flags]
    public enum PrintEngineLogModules : uint
    {
        None                = 0,
        Setup               = 1u << 0,
        ConfigEngine        = 1u << 1,
        Commands            = 1u << 2,
        HeadOffsets         = 1u << 3,
        Waveforms           = 1u << 4,
        WaveformData        = 1u << 5,
        LogEepromData       = 1u << 6,
        LogRawEeprom        = 1u << 7,
        HdcMicro            = 1u << 8,
        LogSegments         = 1u << 9,
        Flash               = 1u << 10,
        LogPrintData        = 1u << 11,
        LogImgBufAllocation = 1u << 12,
        PccConnection       = 1u << 13,
    }

    /// <summary>로그 항목 목록과 cfg 파일 사이의 변환.</summary>
    public static class PrintEngineLogModuleSettings
    {
        public const string Key = "LogCtrlBits";

        /// <summary>화면에 띄울 이름과 설명. 순서가 곧 비트 순서다(위 주의사항 참고).</summary>
        public static IReadOnlyList<(PrintEngineLogModules Module, string Label, string Description)> All { get; } =
            new[]
            {
                (PrintEngineLogModules.Setup,               "Setup",               "초기화·설정 적용 과정"),
                (PrintEngineLogModules.ConfigEngine,        "ConfigEngine",        "cfg 파일 해석 상세"),
                (PrintEngineLogModules.Commands,            "Commands",            "PCMD_xxx 명령 흐름"),
                (PrintEngineLogModules.HeadOffsets,         "HeadOffsets",         "헤드별 X/Y 오프셋 기록"),
                (PrintEngineLogModules.Waveforms,           "Waveforms",           "파형 선택·적용"),
                (PrintEngineLogModules.WaveformData,        "WaveformData",        "파형 데이터 전체 덤프 (방대함)"),
                (PrintEngineLogModules.LogEepromData,       "LogEepromData",       "헤드 EEPROM 해석 결과"),
                (PrintEngineLogModules.LogRawEeprom,        "LogRawEeprom",        "헤드 EEPROM 원본 바이트"),
                (PrintEngineLogModules.HdcMicro,            "HdcMicro",            "HDC 마이크로컨트롤러 통신"),
                (PrintEngineLogModules.LogSegments,         "LogSegments",         "Translator 세그먼트 분할"),
                (PrintEngineLogModules.Flash,               "Flash",               "CF/SD 플래시 접근"),
                (PrintEngineLogModules.LogPrintData,        "LogPrintData",        "인쇄 데이터 상세 (성능 영향 큼)"),
                (PrintEngineLogModules.LogImgBufAllocation, "LogImgBufAllocation", "이미지 버퍼 할당/해제"),
                (PrintEngineLogModules.PccConnection,       "PccConnection",       "PCC 연결·재연결 과정"),
            };

        /// <summary>설치 직후 기본값.</summary>
        public const PrintEngineLogModules Default =
            PrintEngineLogModules.Setup | PrintEngineLogModules.Commands;

        /// <summary>
        /// 가동 중에는 켜면 안 되는 항목. 호스트 PC 부하가 급증해서
        /// FIFO data under-run(= 인쇄 중 데이터 공급 실패)으로 이어질 수 있다.
        /// </summary>
        public const PrintEngineLogModules HeavyModules =
            PrintEngineLogModules.WaveformData |
            PrintEngineLogModules.LogPrintData |
            PrintEngineLogModules.LogRawEeprom |
            PrintEngineLogModules.LogSegments;

        public static bool IsHeavy(PrintEngineLogModules modules) => (modules & HeavyModules) != 0;

        public static PrintEngineLogModules Read(MeteorConfigFile cfg)
            => Parse(cfg.Get(MeteorConfigFile.TestSection, Key)) ?? Default;

        /// <summary>"0x1", "1", "0X0000000A" 를 모두 받는다.</summary>
        public static PrintEngineLogModules? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = raw!.Trim();
            bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            string text = hex ? raw[2..] : raw;

            return uint.TryParse(text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out uint v)
                ? (PrintEngineLogModules)v
                : null;
        }

        public static string Format(PrintEngineLogModules modules)
            => "0x" + ((uint)modules).ToString("X", CultureInfo.InvariantCulture);

        /// <summary>cfg 의 <c>[Test] LogCtrlBits</c> 한 줄만 갈아 끼운다.</summary>
        public static void Save(string configPath, PrintEngineLogModules modules)
            => MeteorConfigFile.SetValue(configPath, MeteorConfigFile.TestSection, Key, Format(modules));
    }
}
