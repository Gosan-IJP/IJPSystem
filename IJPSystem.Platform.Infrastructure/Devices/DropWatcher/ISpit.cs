using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>중단 명령 1회의 결과. (Meteor: PiAbort 의 eRET 을 3분류로 축약)</summary>
    public enum SpitAbortResult
    {
        /// <summary>명령 수락(RVAL_OK). 실제 정지 여부는 IsBusy 로 다시 확인해야 한다.</summary>
        Ok,
        /// <summary>컨트롤러가 다른 명령 처리 중(RVAL_BUSY). 재시도하면 통할 수 있다.</summary>
        Busy,
        /// <summary>재시도해도 무의미한 실패.</summary>
        Failed,
    }

    /// <summary>
    /// 헤드 토출(스핏). Meteor PCC / DPN16 으로 선택 노즐을 주파수 F로 토출.
    /// 화면의 "Spit DW" 토글이 <see cref="Start"/>/<see cref="StopAsync"/> 에 대응한다.
    /// </summary>
    public interface ISpit : IDisposable
    {
        /// <summary>토출 개시. (Spit DW = ON) 패턴 생성 → 파라미터 설정 → 잡 전송 → 연속 토출.</summary>
        void Start(SpitSettings settings);

        /// <summary>토출 중단 + 실제 정지 확인. (Spit DW = OFF) 내부적으로 중단 명령 후 idle 을 폴링한다.</summary>
        Task<bool> StopAsync(int timeoutMs = 3000, CancellationToken ct = default);

        /// <summary>마지막 Start 에 쓰인 설정. 미시작이면 null.</summary>
        SpitSettings? CurrentSettings { get; }

        double FrequencyHz { get; }

        /// <summary>토출 중으로 <b>간주</b>되는 상태(마지막 Start/Stop 기준).</summary>
        bool IsSpitting { get; }

        /// <summary>컨트롤러가 명령을 처리 중인지. (Meteor: PiIsBusy)</summary>
        bool IsBusy { get; }

        /// <summary>
        /// 마지막 <see cref="Start"/> 에서 헤드 범위를 벗어나 버려진 노즐 번호.
        ///
        /// <para>
        /// 인터페이스에 둔 이유: 조용히 버려지면 안 되는 정보인데, 화면이 구현 타입을 캐스팅해서
        /// 꺼내면 다른 구현으로 바꾸는 순간 경고가 사라진다. 대개 노즐 번호 기준(0/1 시작)이
        /// 어긋난 것이라 놓치면 원인을 찾기 어렵다.
        /// </para>
        /// </summary>
        IReadOnlyList<int> IgnoredNozzles { get; }
    }

    /// <summary>
    /// 토출 중단 절차의 공통 구현.
    /// (LabVIEW METEOR PCC Func — PiAbort → RVAL_BUSY 재시도 → PiIsBusy 폴링)
    ///
    /// <b>중단 계층을 혼동하지 말 것</b> — 서로 다른 층이다:
    ///   Spit DW   : 화면 버튼   — 드랍와쳐 토출 개시/중단  (= 이 클래스의 Start/StopAsync)
    ///   PiAbort   : Meteor 명령 — 헤드 구동 중단          (= TryAbort, StopAsync 내부의 하위 명령)
    /// ※ PiAbort 는 <b>소프트웨어 명령</b>이며 안전 규격 비상정지가 아니다.
    ///   모션·펌프·밸브는 멈추지 않는다. 설비 비상정지는 EMO 회로가 담당한다.
    /// </summary>
    public abstract class SpitBase : ISpit
    {
        public abstract double FrequencyHz { get; }
        public abstract bool   IsSpitting  { get; }
        public abstract bool   IsBusy      { get; }

        public SpitSettings? CurrentSettings { get; protected set; }

        /// <summary>기본은 "버린 노즐 없음". 범위 검증을 하는 구현이 덮어쓴다.</summary>
        public virtual IReadOnlyList<int> IgnoredNozzles => Array.Empty<int>();

        public abstract void Start(SpitSettings settings);

        /// <summary>중단 명령 1회 전송. (Meteor: PiAbort)</summary>
        protected abstract SpitAbortResult TryAbort();

        /// <summary>
        /// 토출 중단. 중단 명령 반환값만 믿으면 아직 토출 중인데 다음 단계로 넘어갈 수 있으므로
        /// idle 을 폴링해 <b>실제 정지까지 확인</b>한다.
        /// </summary>
        /// <returns>제한 시간 안에 idle 이 되면 true.</returns>
        public Task<bool> StopAsync(int timeoutMs = 3000, CancellationToken ct = default)
            => AbortAndWaitIdleAsync(timeoutMs, retryOnBusy: 3, ct);

        protected async Task<bool> AbortAndWaitIdleAsync(
            int timeoutMs = 3000, int retryOnBusy = 3, CancellationToken ct = default)
        {
            // ① 중단 명령 — BUSY 면 잠깐 뒤 재시도, 그 외 실패는 재시도해도 소용없다.
            for (int attempt = 0; ; attempt++)
            {
                var r = TryAbort();
                if (r == SpitAbortResult.Ok) break;
                if (r == SpitAbortResult.Failed) return false;
                if (attempt >= retryOnBusy) return false;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            // ② 실제 idle 확인 — 명령 수락 ≠ 정지 완료.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsBusy) return true;
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            return false;   // 타임아웃 — 여전히 구동 중
        }

        public virtual void Dispose() { }
    }

    /// <summary>
    /// 실장 헤드 미연결용 토출 대역.
    /// 중단 절차(BUSY 재시도 → idle 폴링)를 실제로 밟도록 <b>짧은 정지 지연</b>을 흉내 낸다
    /// — 지연이 0이면 대기 로직이 늘 통과해 검증이 되지 않는다.
    ///
    /// 패턴 생성기를 주입하면 <b>스핏 패턴을 실제로 만들어</b> 노즐 매핑·범위 검증까지 밟는다
    /// — 실장 전에 잡히는 오류(번호 기준 어긋남 등)는 대부분 여기서 난다.
    ///
    /// ※ 실장 연결 시 <see cref="SpitBase"/> 를 상속한 Meteor 어댑터로 교체할 것.
    ///   Start() → SetParam(CPEX_*) + PiSendCommand(패턴), TryAbort() → PiAbort,
    ///   IsBusy → PiIsBusy 로 매핑하면 중단 절차는 그대로 재사용된다.
    ///   (MeteorCLS/PrinterInterfaceCLS 어셈블리가 솔루션에 추가돼야 작성 가능)
    /// </summary>
    public sealed class VirtualSpit : SpitBase
    {
        private const int StopLatencyMs = 120;   // 컨트롤러가 토출을 실제로 멈추기까지 걸리는 시간

        private readonly ISpitPatternBuilder? _patternBuilder;
        private DateTime _busyUntilUtc = DateTime.MinValue;
        private bool _isSpitting;
        private double _frequencyHz;

        public VirtualSpit(ISpitPatternBuilder? patternBuilder = null) => _patternBuilder = patternBuilder;

        public override double FrequencyHz => _frequencyHz;
        public override bool   IsSpitting  => _isSpitting;
        public override bool   IsBusy      => DateTime.UtcNow < _busyUntilUtc;

        /// <summary>마지막 Start 로 지정된 노즐 수(표시/로그용).</summary>
        public int NozzleCount { get; private set; }

        private IReadOnlyList<int> _ignored = Array.Empty<int>();

        /// <summary>패턴 생성 시 범위를 벗어나 무시된 노즐 번호. 비어 있지 않으면 노즐 번호 기준을 의심할 것.</summary>
        public override IReadOnlyList<int> IgnoredNozzles => _ignored;

        public override void Start(SpitSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _frequencyHz = settings.FrequencyHz;
            NozzleCount  = settings.Nozzles?.Count ?? 0;

            // 패턴을 실제로 만들어 본다 — 노즐 번호가 헤드 범위를 벗어나면 여기서 드러난다.
            if (_patternBuilder != null)
            {
                _patternBuilder.Build(settings);
                _ignored = (_patternBuilder as S800SingleSpitPatternBuilder)?.LastIgnoredNozzles
                           ?? Array.Empty<int>();
            }

            CurrentSettings = settings;
            _isSpitting     = true;
        }

        protected override SpitAbortResult TryAbort()
        {
            if (!_isSpitting) return SpitAbortResult.Ok;   // 이미 정지 — 중단은 멱등
            _isSpitting   = false;
            _busyUntilUtc = DateTime.UtcNow.AddMilliseconds(StopLatencyMs);
            return SpitAbortResult.Ok;
        }
    }
}
