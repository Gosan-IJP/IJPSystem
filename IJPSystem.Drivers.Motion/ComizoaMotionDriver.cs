using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Motion
{
    /// <summary>
    /// 코미조아(Comizoa) EtherCAT 모션 드라이버.
    /// 프로젝트 공용 <see cref="IMotionDriver"/> 계약을, Comizoa EtherCAT SDK 래퍼(<see cref="IComiMotion"/>) 위에 얹은 어댑터.
    /// - 문자열 AxisNo ↔ <see cref="AxisId"/>(enum) 매핑(설정 순서 기준)
    /// - 동기 SDK 호출을 Task 로 감싸 IMotionDriver(async) 계약에 맞춤
    /// </summary>
    public class ComizoaMotionDriver : IMotionDriver
    {
        private const int DefaultMoveTimeoutMs = 60000;

        private readonly Dictionary<string, AxisStatus> _axisStates = new();
        private readonly Dictionary<string, AxisId> _axisMap = new();
        private List<AxisDeviceInfo> _configs = new();

        private IComiMotion? _comi;

        /// <summary>ComiEcatLib ini 경로(없으면 기본값 사용).</summary>
        public string IniPath { get; set; } = "ComiEcatLibCfg.ini";

        public bool IsConnected { get; private set; }

        public bool Connect()
        {
            try
            {
                var cfg = ComiEcatConfig.Load(IniPath);
                var comi = new ComiEcatMotion();
                comi.Init(cfg);
                _comi = comi;

                // 축별 기본 속도 프로파일 적용(Move 기준)
                foreach (var c in _configs)
                {
                    if (!_axisMap.TryGetValue(c.AxisNo, out var ax)) continue;
                    var mv = c.MotionConfig?.Move;
                    if (mv != null)
                        comi.SetVelocity(ax, new VelocityProfile
                        {
                            Velocity = mv.Velocity,
                            Acceleration = mv.Acceleration,
                            Deceleration = mv.Deceleration
                        });
                }

                IsConnected = true;
                return true;
            }
            catch
            {
                IsConnected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try { _comi?.Unload(); } catch { /* 해제 오류 무시 */ }
            _comi?.Dispose();
            _comi = null;
            IsConnected = false;
        }

        public void Initialize(List<AxisDeviceInfo> axisConfigs)
        {
            if (axisConfigs == null) return;
            _axisStates.Clear();
            _axisMap.Clear();
            _configs = axisConfigs;

            int idx = 0;
            foreach (var cfg in axisConfigs)
            {
                if (string.IsNullOrEmpty(cfg.AxisNo)) continue;
                _axisStates[cfg.AxisNo] = new AxisStatus
                {
                    AxisNo = cfg.AxisNo,
                    Name   = cfg.Name ?? "Unknown Axis",
                    Unit   = cfg.Unit ?? "mm",
                };
                _axisMap[cfg.AxisNo] = (AxisId)idx++;   // 설정 순서 → AxisId(0=X,1=Y,…)
            }
        }

        // ── 상태 조회 ──
        public AxisStatus GetStatus(string axisNo)
        {
            if (!_axisStates.TryGetValue(axisNo, out var s))
                return new AxisStatus { AxisNo = axisNo };
            RefreshStatus(axisNo, s);
            return s;
        }

        public double GetActualPosition(string axisNo) => GetStatus(axisNo).CurrentPos;

        public List<AxisStatus> GetAllStatus()
        {
            foreach (var kv in _axisStates) RefreshStatus(kv.Key, kv.Value);
            return _axisStates.Values.OrderBy(s => s.AxisNo).ToList();
        }

        /// <summary>하드웨어 상태를 읽어 캐시된 AxisStatus 에 반영.</summary>
        private void RefreshStatus(string axisNo, AxisStatus s)
        {
            if (_comi == null || !_axisMap.TryGetValue(axisNo, out var ax)) return;
            try
            {
                var st = _comi.GetState(ax);
                s.CurrentPos   = st.Position;
                s.IsMoving     = st.IsMoving;
                s.IsServoOn    = st.ServoOn;
                s.IsHomeDone   = st.IsHomed;
                s.IsAlarm      = st.Alarm;
                s.CwLimit      = st.PositiveLimit;
                s.CcwLimit     = st.NegativeLimit;
                s.IsInPosition = !st.IsMoving;
            }
            catch { /* 통신 오류 시 마지막 상태 유지 */ }
        }

        // ── 구동 명령 ──
        public Task<bool> ServoOn(string axisNo, bool isOn)
        {
            if (!TryAxis(axisNo, out var ax)) return Task.FromResult(false);
            try
            {
                _comi!.ServoOn(ax, isOn);
                if (_axisStates.TryGetValue(axisNo, out var s)) s.IsServoOn = isOn;
                return Task.FromResult(true);
            }
            catch { return Task.FromResult(false); }
        }

        public Task<bool> MoveAbs(string axisNo, double pos, double vel, double acc, double dec)
            => RunMove(axisNo, vel, acc, dec, ax => _comi!.MoveAbsolute(ax, pos));

        public Task<bool> MoveRel(string axisNo, double distance, double vel, double acc, double dec)
            => RunMove(axisNo, vel, acc, dec, ax => _comi!.MoveRelative(ax, distance));

        private Task<bool> RunMove(string axisNo, double vel, double acc, double dec, Action<AxisId> move)
        {
            if (!TryAxis(axisNo, out var ax)) return Task.FromResult(false);
            return Task.Run(() =>
            {
                try
                {
                    _comi!.SetVelocity(ax, new VelocityProfile { Velocity = vel, Acceleration = acc, Deceleration = dec });
                    move(ax);
                    return _comi.WaitForDone(ax, DefaultMoveTimeoutMs);
                }
                catch { return false; }
            });
        }

        public Task<bool> MoveJog(string axisNo, bool isForward, double vel, double acc, double dec)
        {
            if (!TryAxis(axisNo, out var ax)) return Task.FromResult(false);
            try
            {
                _comi!.SetVelocity(ax, new VelocityProfile { Velocity = vel, Acceleration = acc, Deceleration = dec });
                _comi.Jog(ax, isForward ? +1 : -1);   // 종료는 Stop 으로
                return Task.FromResult(true);
            }
            catch { return Task.FromResult(false); }
        }

        public Task<bool> Stop(string axisNo)
        {
            if (!TryAxis(axisNo, out var ax)) return Task.FromResult(false);
            try { _comi!.Stop(ax); return Task.FromResult(true); }
            catch { return Task.FromResult(false); }
        }

        public Task<bool> Home(string axisNo)
        {
            if (!TryAxis(axisNo, out var ax)) return Task.FromResult(false);
            return Task.Run(() =>
            {
                try { _comi!.Home(ax); return _comi.WaitForDone(ax, DefaultMoveTimeoutMs); }
                catch { return false; }
            });
        }

        public Task<bool> ResetAlarm(string axisNo)
        {
            if (!TryAxis(axisNo, out var ax)) return Task.FromResult(false);
            try
            {
                _comi!.SetAlarmState(ax, reset: true);
                if (_axisStates.TryGetValue(axisNo, out var s)) s.IsAlarm = false;
                return Task.FromResult(true);
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>연결됨 + 매핑 존재 시에만 AxisId 반환.</summary>
        private bool TryAxis(string axisNo, out AxisId ax)
        {
            ax = default;
            return _comi != null && _axisMap.TryGetValue(axisNo, out ax);
        }
    }
}
