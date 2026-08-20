using System.Collections.Generic;

namespace IJPSystem.Platform.Infrastructure.Print.Meteor
{
    public enum PccFaultType
    {
        PreloadIntegrityTest,
        FifoCommandSequence,
        FifoDataIntegrityTest,
        FifoDataUnderrun,
        PllSystemClock,
        PllInterfaceClock,
        PllDdramClock,
        PllAny,
    }

    /// <summary>Fault Register 의 켜진 비트 하나.</summary>
    /// <param name="Bit">레지스터 비트 번호. 로그·문서와 맞춰 볼 수 있어야 해서 그대로 남긴다.</param>
    /// <param name="HeadNumber">헤드별 폴트면 헤드 번호(1부터), 아니면 null.</param>
    public sealed record PccFault(int Bit, int? HeadNumber, PccFaultType Type, string Description)
    {
        public string Title => HeadNumber is null
            ? $"bit{Bit}"
            : $"bit{Bit} · Head{HeadNumber}";
    }

    /// <summary>
    /// PCC Fault Register(32비트)를 사람이 읽는 목록으로 푼다.
    ///
    /// <para>구조: bit 0~23 은 헤드 6개 × 4비트,
    /// bit 24~31 은 PLL 계통이다. 정상 가동 중에는 이 레지스터가 0 이어야 한다.</para>
    ///
    /// <para>화면에 0x00040000 같은 숫자만 띄우면 아무도 못 읽는다 — 그래서 화면이 아니라
    /// 여기서 푼다(테스트로 비트 배치를 고정해 둘 수 있다).</para>
    /// </summary>
    public static class PccFaultDecoder
    {
        /// <summary>Fault Register 가 다루는 헤드 수. 나머지 비트는 PLL 계통이다.</summary>
        public const int HeadsInFaultRegister = 6;

        public static IReadOnlyList<PccFault> Decode(uint faultRegister)
        {
            var list = new List<PccFault>();
            if (faultRegister == 0) return list;

            for (int head = 0; head < HeadsInFaultRegister; head++)
            {
                int b = head * 4;

                Add(list, faultRegister, b + 0, head + 1, PccFaultType.PreloadIntegrityTest,
                    "Preload Integrity Test Fault — 테스트 패턴이 검출되지 않았다");
                Add(list, faultRegister, b + 1, head + 1, PccFaultType.FifoCommandSequence,
                    "FIFO Command Sequence Fault — 명령 순서 오류(제품 감지 전에 이미지가 갔다든지)");
                Add(list, faultRegister, b + 2, head + 1, PccFaultType.FifoDataIntegrityTest,
                    "FIFO Data Integrity Test Fault");
                Add(list, faultRegister, b + 3, head + 1, PccFaultType.FifoDataUnderrun,
                    "FIFO Data Under-run — 제품이 데이터보다 먼저 도착했다. 데이터 공급 속도 부족");
            }

            Add(list, faultRegister, 24, null, PccFaultType.PllSystemClock,
                "PLL Fault: 80MHz System Clock — 대개 케이블로 들어온 전기적 잡음");
            Add(list, faultRegister, 26, null, PccFaultType.PllInterfaceClock,
                "PLL Fault: 48MHz Interface Clock");
            Add(list, faultRegister, 27, null, PccFaultType.PllDdramClock,
                "PLL Fault: 128MHz DDRAM Clock");
            Add(list, faultRegister, 31, null, PccFaultType.PllAny,
                "PLL Fault: 전 클럭 중 하나 이상");

            return list;
        }

        private static void Add(List<PccFault> list, uint reg, int bit, int? head,
                                PccFaultType type, string desc)
        {
            if ((reg & (1u << bit)) != 0) list.Add(new PccFault(bit, head, type, desc));
        }

        /// <summary>화면 표기용 — Meteor Monitor 와 같은 "0x22F0 0A00" 형식.</summary>
        public static string FormatStatusBits(uint value)
            => $"0x{value >> 16:X4} {value & 0xFFFF:X4}";
    }
}
