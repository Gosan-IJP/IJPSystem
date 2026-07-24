using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace IJPSystem.Platform.Infrastructure.Devices.DropWatcher
{
    /// <summary>
    /// Meteor 헤드 토출을 <b>x64 브리지 프로세스</b>를 통해 구동하는 ISpit 구현.
    /// SpitBase 의 중단 절차(Abort→BUSY 재시도→idle 폴링)를 그대로 재사용하고,
    /// 실제 명령만 <see cref="MeteorPipeClient"/> 로 브리지에 위임한다.
    ///
    /// 브리지(MeteorBridge.exe)가 실행 중이 아니면 <see cref="_bridgeExePath"/> 로 자동 기동한다.
    /// 미설정/실행 실패 시에는 파이프 연결이 실패하고, 그 사유가 각 명령 결과에 담긴다
    /// (VirtualSpit 처럼 화면은 떠야 하므로 생성만으로는 예외를 던지지 않는다).
    /// </summary>
    public sealed class MeteorBridgeSpit : SpitBase
    {
        private readonly MeteorPipeClient _pipe;
        private readonly string? _bridgeExePath;
        private readonly string _cfgPath;
        private readonly Action<string>? _log;

        private bool _opened;
        private bool _isSpitting;
        private double _frequencyHz;

        /// <param name="cfgPath">Meteor PCCE 설정 파일(.cfg) 경로 — PiOpenPrinter 대상.</param>
        /// <param name="bridgeExePath">MeteorBridge.exe 경로. null 이면 자동 기동을 시도하지 않는다.</param>
        public MeteorBridgeSpit(string cfgPath, string? bridgeExePath = null, Action<string>? log = null)
        {
            _cfgPath       = cfgPath;
            _bridgeExePath = bridgeExePath;
            _log           = log;
            _pipe          = new MeteorPipeClient();
        }

        public override double FrequencyHz => _frequencyHz;
        public override bool   IsSpitting  => _isSpitting;

        // 컨트롤러가 명령 처리 중인지 — 브리지에 조회. 조회 실패는 "busy 아님"으로 간주하지 않고
        // (아직 구동 중일 수 있으므로) true 로 본다: 중단 폴링이 성급히 성공 처리되는 것을 막는다.
        public override bool IsBusy
        {
            get
            {
                var r = _pipe.Send(new BridgeRequest { Cmd = MeteorBridgeProtocol.CmdBusy });
                if (!r.Ok) { _log?.Invoke($"BUSY 조회 실패: {r.Err}"); return true; }
                return r.Busy;
            }
        }

        public override void Start(SpitSettings settings)
        {
            EnsureBridgeProcess();
            EnsureOpened();

            if (settings.HeadVoltage is int volts)
            {
                var pw = _pipe.Send(new BridgeRequest { Cmd = MeteorBridgeProtocol.CmdPower, Volts = volts });
                if (!pw.Ok) _log?.Invoke($"헤드 전압 인가 실패: {pw.Err}");
            }

            var r = _pipe.Send(new BridgeRequest
            {
                Cmd         = MeteorBridgeProtocol.CmdSpit,
                Nozzles     = settings.Nozzles.ToArray(),
                FreqHz      = settings.FrequencyHz,
                GreyLevel   = settings.SpitGreyLevel,
                TickleLevel = settings.TickleGreyLevel,
            });
            if (!r.Ok)
                throw new IOException($"Spit 명령 실패: {r.Err ?? "사유 미상"}");

            CurrentSettings = settings;
            _frequencyHz    = settings.FrequencyHz;
            _isSpitting     = true;
        }

        protected override SpitAbortResult TryAbort()
        {
            var r = _pipe.Send(new BridgeRequest { Cmd = MeteorBridgeProtocol.CmdAbort });
            if (!r.Ok) return SpitAbortResult.Failed;

            var result = r.Result switch
            {
                MeteorBridgeProtocol.AbortOk   => SpitAbortResult.Ok,
                MeteorBridgeProtocol.AbortBusy => SpitAbortResult.Busy,
                _                              => SpitAbortResult.Failed,
            };
            if (result == SpitAbortResult.Ok) _isSpitting = false;
            return result;
        }

        private void EnsureOpened()
        {
            if (_opened) return;
            var r = _pipe.Send(new BridgeRequest { Cmd = MeteorBridgeProtocol.CmdOpen, Cfg = _cfgPath });
            if (!r.Ok)
                throw new IOException($"프린터 열기 실패({_cfgPath}): {r.Err ?? "사유 미상"}");
            _opened = true;
            _log?.Invoke($"프린터 열림: {_cfgPath}");
        }

        /// <summary>브리지 프로세스가 없으면 기동한다. 파이프가 이미 붙으면 아무것도 안 한다.</summary>
        private void EnsureBridgeProcess()
        {
            // 이미 떠 있으면 PING 으로 확인되고 끝.
            if (_pipe.Send(new BridgeRequest { Cmd = MeteorBridgeProtocol.CmdPing }).Ok) return;

            if (string.IsNullOrEmpty(_bridgeExePath) || !File.Exists(_bridgeExePath))
            {
                _log?.Invoke($"브리지 실행파일 없음({_bridgeExePath ?? "미설정"}) — 파이프 연결만 재시도");
                return;   // 외부에서 이미 띄워 두는 운용도 허용
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = _bridgeExePath,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                });
                _log?.Invoke($"브리지 기동: {_bridgeExePath}");
                // 기동 직후엔 파이프 서버가 아직 안 떴을 수 있다 — 첫 명령의 재연결이 흡수한다.
                System.Threading.Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"브리지 기동 실패: {ex.Message}");
            }
        }

        public override void Dispose()
        {
            try { if (_opened) _pipe.Send(new BridgeRequest { Cmd = MeteorBridgeProtocol.CmdClose }); } catch { }
            _pipe.Dispose();
            base.Dispose();
        }
    }
}
