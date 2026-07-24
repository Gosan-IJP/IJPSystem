using System.Diagnostics;
using IJPSystem.Platform.Infrastructure.Devices.DropWatcher;   // MeteorBridgeProtocol (링크된 파일)

namespace IJPSystem.MeteorBridge
{
    /// <summary>
    /// 실장 Meteor 없이 브리지·IPC·중단 절차를 검증하기 위한 가상 컨트롤러.
    /// 실제와 같은 순서(Open → Power → Spit → Abort → IsBusy → Close)를 밟고,
    /// <b>Abort 후에도 잠깐 busy 를 유지</b>해 앱 쪽 idle 폴링이 실제로 동작하는지 확인시킨다.
    /// (지연이 0이면 폴링이 늘 즉시 통과해 검증이 되지 않는다 — VirtualSpit 과 같은 이유)
    /// </summary>
    public sealed class MockMeteorController : IMeteorController
    {
        private readonly Action<string>? _log;
        private bool _open;
        private bool _spitting;
        private readonly Stopwatch _sinceAbort = new();
        private static readonly TimeSpan StopDelay = TimeSpan.FromMilliseconds(120);
        private int _abortCalls;

        public MockMeteorController(Action<string>? log = null) => _log = log;

        public (bool ok, string? err) Open(string cfgPath)
        {
            if (!File.Exists(cfgPath))
                _log?.Invoke($"[mock] cfg 파일 없음(무시): {cfgPath}");   // 가상은 파일 없어도 진행
            _open = true;
            _log?.Invoke($"[mock] Open: {cfgPath}");
            return (true, null);
        }

        public (bool ok, string? err) SetPower(int volts)
        {
            if (!_open) return (false, "미연결(Open 먼저)");
            _log?.Invoke($"[mock] SetPower: {volts}V");
            return (true, null);
        }

        public (bool ok, string? err) Spit(int[] nozzles, double freqHz, int greyLevel, int tickleLevel)
        {
            if (!_open) return (false, "미연결(Open 먼저)");
            if (nozzles.Length == 0) return (false, "선택 노즐 없음");
            _spitting = true;
            _sinceAbort.Reset();
            _log?.Invoke($"[mock] Spit: {nozzles.Length}노즐 @ {freqHz}Hz, grey={greyLevel}");
            return (true, null);
        }

        public string Abort()
        {
            // 첫 호출은 BUSY 를 한 번 돌려줘 앱의 BUSY 재시도 경로까지 밟게 한다.
            if (_spitting && ++_abortCalls == 1)
            {
                _log?.Invoke("[mock] Abort → BUSY(재시도 유도)");
                return MeteorBridgeProtocol.AbortBusy;
            }
            _spitting = false;
            _sinceAbort.Restart();   // 이후 잠깐 busy 유지 → idle 폴링 검증
            _log?.Invoke("[mock] Abort → Ok");
            return MeteorBridgeProtocol.AbortOk;
        }

        public bool IsBusy()
        {
            if (_spitting) return true;
            // 중단 명령 후 StopDelay 동안은 아직 정지 중(busy)으로 본다.
            return _sinceAbort.IsRunning && _sinceAbort.Elapsed < StopDelay;
        }

        public void Close()
        {
            _spitting = false;
            _open = false;
            _log?.Invoke("[mock] Close");
        }
    }
}
