using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Motion.Comizoa
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

        // 원점복귀 완료를 소프트웨어로 래치한다.
        // 이유: CiA-402/EtherCAT 드라이브의 HomeAttained(bit14) 는 드라이브가 Homing 모드에 있을 때만
        //   유효하다. 초기화 시퀀스가 홈 직후 READY 로 일반 이동하면 드라이브가 Homing 모드를 벗어나
        //   bit14 가 사라져, 방금 원점복귀했는데도 IsHomeDone=false 로 오판(오토프린트 사전조건 실패,
        //   원점 LED 꺼짐). → Home 성공 시 여기에 래치하고, 서보 OFF/알람 시 해제.
        private readonly HashSet<AxisId> _homedAxes = new();
        private readonly object _homedSync = new();

        private IComiMotion? _comi;

        // 진단 로그 1회성 플래그(폴링 스팸 방지)
        private bool _statusErrLogged;
        private bool _servoErrLogged;

        /// <summary>ComiEcatLib ini 경로(없으면 기본값 사용).</summary>
        public string IniPath { get; set; } = "ComiEcatLibCfg.ini";

        public bool IsConnected { get; private set; }

        public bool Connect()
        {
            try
            {
                var cfg = ComiEcatConfig.Load(IniPath);
                LoggerService.WriteToFile("INFO",
                    $"[Comizoa Motion] Init 시도 — ini={IniPath}, Embedded={cfg.EmbeddedMode}, Simulation={cfg.SimulationMode}");
                var comi = new ComiEcatMotion();
                comi.Init(cfg);
                _comi = comi;
                LoggerService.WriteToFile("INFO", "[Comizoa Motion] Init 성공 — 실제 하드웨어 연결됨");

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

                // 전원투입 시 드라이브가 래치된 폴트 상태로 부팅되는 경우가 있다(실장: Y축 raw=0x8038, bit3 ServoFault).
                // 수동으로 알람 해제하면 사라지고, 재실행하면 안 나타나는 전형적 잔류 폴트다.
                // → 상태 폴링(알람 감시) 시작 전에 각 축 폴트를 1회 리셋. 실제 지속 폴트(STO/배선 등)는
                //   리셋해도 즉시 재폴트하므로 감시에서 그대로 잡혀, 정상 알람은 놓치지 않는다.
                foreach (var ax in _axisMap.Values)
                {
                    try { comi.SetAlarmState(ax, reset: true); }
                    catch (Exception ex)
                    {
                        LoggerService.WriteToFile("WARN",
                            $"[Comizoa Motion] 기동 폴트 리셋 실패(축 {(int)ax}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                IsConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                // 실장 진단: ecat_Init/SetVelocity 가 실패하면 여기로 온다.
                // EntryPointNotFound=함수명 불일치, DllNotFound=DLL 미존재, BadImageFormat=비트수 불일치.
                LoggerService.WriteToFile("ERROR",
                    $"[Comizoa Motion] 연결 실패 — 모든 축 명령이 무시됩니다(시뮬레이션처럼 보임): {ex.GetType().Name}: {ex.Message}");
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
                // HwAxis 지정 시 그 값(물리 축 번호), 없으면 나열 순서 → AxisId(0=X,1=Y,…).
                // 배선상 X↔Y가 뒤바뀐 경우 MotorConfig.json 의 HwAxis 로 교정.
                int hw = cfg.HwAxis ?? idx;
                _axisMap[cfg.AxisNo] = (AxisId)hw;
                LoggerService.WriteToFile("INFO",
                    $"[Comizoa Motion] 축 매핑: {cfg.AxisNo}({cfg.Name}) → HwAxis {hw}");
                idx++;
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
                // 하드웨어 HomeAttained 는 홈 직후 일반 이동하면 사라지므로 소프트 래치를 신뢰.
                // 알람이 서면 원점 신뢰 불가 → 래치 해제.
                if (st.Alarm)
                {
                    lock (_homedSync) _homedAxes.Remove(ax);
                }
                bool homed;
                lock (_homedSync) homed = _homedAxes.Contains(ax);
                s.IsHomeDone   = homed;
                s.IsAlarm      = st.Alarm;
                s.CwLimit      = st.PositiveLimit;
                s.CcwLimit     = st.NegativeLimit;
                s.IsInPosition = !st.IsMoving;
            }
            catch (Exception ex)
            {
                // 폴링마다 반복되므로 1회만 로깅. ecat_GetStatus/GetPos 실패 시 여기로 온다.
                if (!_statusErrLogged)
                {
                    _statusErrLogged = true;
                    LoggerService.WriteToFile("ERROR",
                        $"[Comizoa Motion] 상태 읽기 실패({axisNo}) — 마지막 상태 유지: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ── 구동 명령 ──
        public Task<bool> ServoOn(string axisNo, bool isOn)
        {
            if (!TryAxis(axisNo, out var ax))
            {
                LoggerService.WriteToFile("WARN",
                    $"[Comizoa Motion] ServoOn 무시({axisNo}, {isOn}) — 미연결 또는 축 매핑 없음(시뮬레이션처럼 보임)");
                return Task.FromResult(false);
            }
            try
            {
                _comi!.ServoOn(ax, isOn);
                if (_axisStates.TryGetValue(axisNo, out var s)) s.IsServoOn = isOn;
                // 서보 OFF 시 원점 신뢰 불가 → 래치 해제(재서보온 후 재홈 필요).
                if (!isOn) lock (_homedSync) _homedAxes.Remove(ax);
                LoggerService.WriteToFile("INFO", $"[Comizoa Motion] ServoOn 명령 전송({axisNo} → {isOn})");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                if (!_servoErrLogged)
                {
                    _servoErrLogged = true;
                    LoggerService.WriteToFile("ERROR",
                        $"[Comizoa Motion] ServoOn 실패({axisNo}, {isOn}): {ex.GetType().Name}: {ex.Message}");
                }
                return Task.FromResult(false);
            }
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
                try
                {
                    // 서보 ON 명령 직후 곧바로 홈하면 드라이브가 아직 Operation-Enabled 전이라
                    // 원점복귀가 실패(HomeError)하고 컨트롤러 폴트가 latched → 알람 오보(실장 로그 재현).
                    // → 서보가 구동가능(Operation-Enabled) 상태가 될 때까지 확인 후 홈 시작.
                    if (!WaitServoEnabled(ax, 3000))
                        LoggerService.WriteToFile("WARN",
                            $"[Comizoa Motion] Home({axisNo}) — 서보 Enable 대기 시간초과, 그대로 진행");
                    // 홈은 단축모션 busy 가 아닌 홈 전용 완료 플래그로 대기.
                    lock (_homedSync) _homedAxes.Remove(ax);   // 새 홈 시작 — 이전 래치 무효화
                    _comi!.Home(ax);
                    bool done = _comi.WaitForHomeDone(ax, DefaultMoveTimeoutMs);
                    if (done)
                        lock (_homedSync) _homedAxes.Add(ax);   // 완료 래치(이후 일반 이동해도 유지)
                    return done;
                }
                catch { return false; }
            });
        }

        /// <summary>서보가 Operation-Enabled(구동 가능) 상태가 될 때까지 대기. 도달=true, 타임아웃=false.</summary>
        private bool WaitServoEnabled(AxisId ax, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { if (_comi != null && _comi.GetState(ax).ServoOn) return true; }
                catch { /* 상태 읽기 실패 시 재시도 */ }
                System.Threading.Thread.Sleep(10);
            }
            return false;
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
