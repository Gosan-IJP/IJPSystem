using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 트리거 표시등 상태.
    ///
    /// <para><b>Warn 과 Fail 을 나누는 이유</b>: "안 온다" 와 "오는데 수가 안 맞는다" 는 볼 곳이
    /// 완전히 다르다. 전자는 배선/설정, 후자는 프레임 누락(펄스 점유·대역폭)이다.
    /// 하나로 합치면 정상에 가까운 상태와 완전 불통이 같은 빨간불이 되어 구분이 사라진다.</para>
    /// </summary>
    public enum TriggerLamp
    {
        /// <summary>미기동 — 판정 대상이 아니다(회색).</summary>
        Idle,
        /// <summary>정상(초록).</summary>
        Ok,
        /// <summary>오긴 오는데 기대와 다르다(노랑).</summary>
        Warn,
        /// <summary>끊겼다(빨강).</summary>
        Fail,
    }

    /// <summary>
    /// 일정 시간 창 안의 발생 횟수로 주파수를 재는 계수기.
    ///
    /// <para>프레임이 "오는가" 만으로는 부족하다 — 분주비대로 오는지까지 봐야 한다. 절반만 오면
    /// 트리거는 살아 있지만 프레임이 누락되고 있는 것이고, 그 상태로 잰 속도값은 믿을 수 없다.</para>
    ///
    /// <para>시계를 밖에서 주입받는다(<paramref name="nowTicks"/>) — 실제 시간을 기다리지 않고
    /// 시험할 수 있어야 이 계산을 장비 없이 확인할 수 있다.</para>
    /// </summary>
    public sealed class RateMeter
    {
        private readonly Queue<long> _marks = new();
        private readonly object _sync = new();
        private readonly double _ticksPerSecond;
        private long _lastMark;
        private bool _hasMark;

        /// <param name="ticksPerSecond">
        /// 틱 단위. 기본은 <see cref="Stopwatch.Frequency"/> — 시험에서는 1000(=ms)처럼 넘긴다.
        /// </param>
        public RateMeter(double ticksPerSecond = 0)
            => _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency;

        /// <summary>주파수를 낼 때 보는 시간 창[초]. 짧으면 흔들리고 길면 변화가 늦게 보인다.</summary>
        public double WindowSeconds { get; set; } = 3.0;

        /// <summary>기동 후 누적 횟수. 창과 무관하다 — "한 장이라도 왔는가" 판정에 쓴다.</summary>
        public long Total { get; private set; }

        /// <summary>한 건 발생.</summary>
        public void Mark(long nowTicks)
        {
            lock (_sync)
            {
                _marks.Enqueue(nowTicks);
                _lastMark = nowTicks;
                _hasMark  = true;
                Total++;
                Trim(nowTicks);
            }
        }

        /// <summary>시간 창 안의 평균 주파수[Hz]. 표본이 2개 미만이면 0.</summary>
        public double RateHz(long nowTicks)
        {
            lock (_sync)
            {
                Trim(nowTicks);
                // 창 전체로 나눈다 — 마지막 두 건의 간격으로 재면 한 번 튄 값이 그대로 표시된다.
                return _marks.Count < 2 ? 0 : _marks.Count / WindowSeconds;
            }
        }

        /// <summary>마지막 발생 이후 경과[초]. 한 번도 없었으면 <see cref="double.PositiveInfinity"/>.</summary>
        public double SecondsSinceLast(long nowTicks)
        {
            lock (_sync)
                return _hasMark ? (nowTicks - _lastMark) / _ticksPerSecond : double.PositiveInfinity;
        }

        public void Reset()
        {
            lock (_sync)
            {
                _marks.Clear();
                Total    = 0;
                _hasMark = false;
            }
        }

        private void Trim(long nowTicks)
        {
            long cutoff = nowTicks - (long)(WindowSeconds * _ticksPerSecond);
            while (_marks.Count > 0 && _marks.Peek() < cutoff) _marks.Dequeue();
        }
    }

    /// <summary>
    /// 트리거 체인 각 구간의 상태 판정 — <b>순수 계산</b>이라 장비 없이 시험할 수 있다.
    ///
    /// <para>화면에 램프를 하나만 두면 꺼졌을 때 어디를 볼지 알 수 없다. 체인이 네 구간이라
    /// 뜯을 곳도 네 군데다:</para>
    /// <code>
    /// PCC2-E ──PFI5──▶ [분주기 ctr1] ──▶ [LED ctr0] ──PFI12──┬─▶ iCore 조명 ─▶ 발광
    ///                                                        └─▶ 카메라 OPTO ─▶ 프레임
    /// </code>
    /// <para>구간별로 나눠 보면 조합이 곧 진단이다. 특히 <b>조명 ✓ + 프레임 ✗</b> 가 중요한데,
    /// 조명이 번쩍이니 트리거가 나가고 있다고 믿고 엉뚱한 데를 뒤지게 되는 경우다
    /// (실제로는 카메라 <c>TriggerSource</c> 이름이 틀렸거나 광절연 입력이 펄스를 놓친 것).</para>
    /// </summary>
    public static class TriggerHealth
    {
        /// <summary>수신 주파수가 기대치에서 이만큼 벗어나면 경고. 노출·전송 지터를 감안한 값.</summary>
        public const double RateToleranceRatio = 0.25;

        /// <summary>이 시간 동안 한 건도 없으면 끊긴 것으로 본다[초].</summary>
        public const double StaleSeconds = 3.0;

        /// <summary>
        /// 프레임 수신 판정.
        /// </summary>
        /// <param name="chainRunning">트리거 체인이 돌고 있는가. 아니면 판정 대상이 아니다.</param>
        /// <param name="receiving">화면이 프레임을 계속 받고 있는가(라이브/측정 중).
        ///   꺼져 있으면 프레임이 안 오는 게 정상이라 <see cref="TriggerLamp.Idle"/> 이다 —
        ///   여기서 빨간불을 켜면 "라이브를 껐다"는 이유로 고장 신고가 올라온다.</param>
        /// <param name="measuredHz">실측 수신 주파수.</param>
        /// <param name="expectedHz">분주 후 기대 주파수.</param>
        /// <param name="secondsSinceLast">마지막 프레임 이후 경과[초].</param>
        public static TriggerLamp Frame(bool chainRunning, bool receiving,
                                        double measuredHz, double expectedHz, double secondsSinceLast)
        {
            if (!chainRunning || !receiving) return TriggerLamp.Idle;
            if (secondsSinceLast > StaleSeconds) return TriggerLamp.Fail;
            if (expectedHz <= 0 || measuredHz <= 0) return TriggerLamp.Warn;

            double off = Math.Abs(measuredHz - expectedHz) / expectedHz;
            return off > RateToleranceRatio ? TriggerLamp.Warn : TriggerLamp.Ok;
        }

        /// <summary>
        /// 조명(iCore Operation 레지스터) 판정.
        /// </summary>
        /// <param name="operation">읽은 운전 모드. 0=OFF, 1=Continuous, 2=Pulse. 읽기 실패면 null.</param>
        /// <param name="expected">이 카메라의 조명이 있어야 할 모드(드랍와처=2 Pulse).</param>
        public static TriggerLamp Light(ushort? operation, ushort expected)
        {
            if (operation == null) return TriggerLamp.Fail;   // 통신이 안 된다 = 조명 상태를 모른다
            if (operation.Value == 0) return TriggerLamp.Fail;

            // 모드가 다르면 불은 들어오지만 동기가 아니다 — Continuous 로 켜 두면 액적이 흐른다.
            return operation.Value == expected ? TriggerLamp.Ok : TriggerLamp.Warn;
        }

        /// <summary>
        /// 램프 조합으로 "어디를 볼지" 한 줄. 화면 아래에 그대로 띄운다.
        /// <para>이 문장이 없으면 램프 세 개를 보고도 매번 같은 추론을 다시 해야 한다.</para>
        /// </summary>
        public static string? Diagnose(TriggerLamp chain, TriggerLamp light, TriggerLamp frame)
        {
            if (chain == TriggerLamp.Idle) return null;                 // 안 돌리는 중 — 할 말 없음
            if (chain == TriggerLamp.Fail)
                return "트리거 체인이 기동하지 못했습니다 — 카운터 배정과 NI-DAQmx 설치를 확인하세요.";

            if (light == TriggerLamp.Fail)
                return "조명이 꺼져 있거나 통신이 안 됩니다 — iCore 전원·COM 포트·sID 를 확인하세요.";
            if (light == TriggerLamp.Warn)
                return "조명이 Pulse 가 아닌 모드로 켜져 있습니다 — 트리거 동기가 아니라 액적이 흐릅니다.";

            if (frame == TriggerLamp.Fail)
                return "조명은 살아 있는데 프레임이 오지 않습니다 — 카메라 TriggerSource 이름, " +
                       "그리고 공유 펄스 폭이 광절연 입력에 충분한지 확인하세요.";
            if (frame == TriggerLamp.Warn)
                return "프레임이 기대 주파수와 어긋납니다 — 펄스 점유가 트리거 주기를 넘거나 " +
                       "전송 대역이 모자라 누락되는 중일 수 있습니다.";

            return null;
        }
    }
}
