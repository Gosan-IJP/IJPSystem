using System;
using System.Threading;
using System.Threading.Tasks;
using IJPSystem.Platform.Common.Constants;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Infrastructure.Config;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// 헤드 토출(스핏)을 <b>장비에 하나만</b> 두는 곳.
    ///
    /// <para>
    /// <b>왜 필요한가</b>: 스핏 버튼이 네 화면에 있었는데 동작이 제각각이었다 — 드랍와쳐만
    /// 실제 <see cref="ISpit"/> 을 돌리고, 패턴 인쇄·P&amp;ID·웨이브폼은 플래그를 뒤집고 로그만
    /// 남겼다. 헤드는 하나인데 화면마다 "지금 토출 중"의 의미가 달라, 한 화면에서 켜고 다른
    /// 화면에서 껐다고 착각할 수 있었다. 여기 한 곳을 거치게 해 상태를 실제로 공유한다.
    /// </para>
    /// <para>
    /// 실물 여부는 <c>AppConfig.json</c> 의 <c>DriverMode.Head</c> 가 정한다 —
    /// "Meteor" 면 <see cref="MeteorSpit"/>, 그 외("None")면 <see cref="VirtualSpit"/>.
    /// 다른 디바이스(IO/Motion/Vision/Meniscus)와 같은 규약이다.
    /// </para>
    /// <para>
    /// 정적인 이유: 화면들이 서로를 모른 채 각자 생성되고, 헤드는 프로세스에 하나뿐이다.
    /// AppSettingsService / MachineSettings 와 같은 방식.
    /// </para>
    /// </summary>
    public static class SpitService
    {
        private static readonly object _sync = new();
        private static ISpit? _spit;

        /// <summary>토출 상태가 바뀌었다(시작/정지). 화면들이 자기 토글을 맞추는 데 쓴다.</summary>
        public static event Action? StateChanged;

        /// <summary>진단 로그. 어느 화면에서 눌렀든 같은 형식으로 남기려고 여기서 낸다.</summary>
        public static event Action<string>? Log;

        /// <summary>현재 어댑터. 처음 쓸 때 설정을 보고 만든다.</summary>
        public static ISpit Current
        {
            get
            {
                lock (_sync) return _spit ??= Create();
            }
        }

        /// <summary>실물 헤드 어댑터인가(= DriverMode.Head 가 Meteor).</summary>
        public static bool IsRealHead => Current is MeteorSpit;

        public static bool IsSpitting => Current.IsSpitting;

        /// <summary>
        /// 토출 시작. 노즐 목록이 비었거나 주파수가 0 이하면 <b>시작하지 않고</b> false 를 준다 —
        /// 화면마다 이 검사를 되풀이하면 한 곳이 빠진다.
        /// </summary>
        /// <param name="reason">실패 사유(성공 시 null). 화면이 그대로 로그에 쓴다.</param>
        public static bool TryStart(SpitSettings settings, out string? reason)
        {
            reason = null;
            if (settings == null) { reason = "설정이 없습니다."; return false; }
            if (settings.Nozzles == null || settings.Nozzles.Count == 0)
            {
                reason = "선택된 노즐이 없습니다. Nozzle Select 로 먼저 지정하세요.";
                return false;
            }
            if (settings.FrequencyHz <= 0) { reason = "Frequency 는 0보다 커야 합니다."; return false; }

            try
            {
                Current.Start(settings);
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }

            Log?.Invoke($"Spit 시작 — 노즐 {settings.Nozzles.Count}개 @ {settings.FrequencyHz}Hz" +
                        (IsRealHead ? "" : " (가상 헤드)"));

            // 범위 밖 노즐은 조용히 버려지면 안 된다 — 대개 번호 기준(0/1 시작)이 어긋난 것이다.
            var ignored = Current.IgnoredNozzles;
            if (ignored.Count > 0)
                Log?.Invoke($"Spit — 헤드 노즐 범위({HeadSpec.FirstNozzle}~{HeadSpec.LastNozzle}) 밖이라 " +
                            $"무시된 번호: {string.Join(",", ignored)}");

            StateChanged?.Invoke();
            return true;
        }

        /// <summary>토출 중단 + 실제 정지 확인.</summary>
        public static async Task<bool> StopAsync(int timeoutMs = 3000, CancellationToken ct = default)
        {
            bool ok;
            try
            {
                ok = await Current.StopAsync(timeoutMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Spit 정지 실패: {ex.Message}");
                StateChanged?.Invoke();
                return false;
            }

            Log?.Invoke(ok ? "Spit 정지" : $"Spit 정지 확인 실패({timeoutMs}ms) — 컨트롤러가 아직 구동 중일 수 있습니다.");
            StateChanged?.Invoke();
            return ok;
        }

        /// <summary>
        /// 설정이 바뀌었을 때 어댑터를 다시 만든다(헤드 모드 변경·테스트). 돌고 있으면 먼저 멈춘다.
        /// </summary>
        public static void Reset()
        {
            ISpit? old;
            lock (_sync) { old = _spit; _spit = null; }
            try { old?.Dispose(); } catch { /* 정리 실패는 무시 — 어차피 버릴 객체다 */ }
            StateChanged?.Invoke();
        }

        /// <summary>테스트/특수 상황용 주입. 이후 <see cref="Reset"/> 하면 설정 기반으로 되돌아간다.</summary>
        public static void Override(ISpit spit)
        {
            lock (_sync) _spit = spit ?? throw new ArgumentNullException(nameof(spit));
            StateChanged?.Invoke();
        }

        private static ISpit Create()
        {
            // 노즐 수는 HeadSpec 하나만 본다 — 여기서 AppConstants 를 직접 보면 레시피의 노즐 정보와
            // 갈라져, 화면에서는 선택되는데 패턴에서는 버려지는 번호가 생긴다.
            int count = HeadSpec.Count;
            int first = HeadSpec.FirstNozzle;

            string mode = AppSettingsService.Current?.DriverMode?.Head ?? "None";
            if (mode.Trim().Equals("Meteor", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string cfg = PathUtils.ResolveConfigPath(
                        AppSettingsService.Current?.MeteorConfigPath, AppConstants.MeteorConfigFile);
                    var meteor = new MeteorSpit(cfg, count, msg => Log?.Invoke(msg), firstNozzle: first);
                    Log?.Invoke($"헤드 토출 = Meteor ({cfg}, 노즐 {first}~{first + count - 1})");
                    return meteor;
                }
                catch (Exception ex)
                {
                    // 헤드가 안 붙었다고 화면이 죽으면 안 된다 — 가상으로 내려가고 이유를 남긴다.
                    Log?.Invoke($"Meteor 헤드 준비 실패({ex.Message}) — 가상 토출로 내려갑니다.");
                }
            }

            Log?.Invoke($"헤드 토출 = 가상 (노즐 {first}~{first + count - 1})");
            return new VirtualSpit(new S800SingleSpitPatternBuilder(count, first));
        }
    }
}
