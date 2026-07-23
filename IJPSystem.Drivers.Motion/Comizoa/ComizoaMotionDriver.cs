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

        // 부팅 잔류 폴트 자동 해제(실장: Y축 raw=0x8038 ServoFault).
        // Connect 직후엔 EtherCAT 개별 드라이브가 아직 addressable 하지 않아 리셋이 -20280 로 실패한다.
        // → 상태 폴링에서 첫 GetState 성공(=네트워크 준비) 시점에 축별 1회 리셋하고, 짧은 유예 뒤에도
        //   폴트가 남아 있으면 그때 알람으로 넘긴다. 지속 폴트(STO/배선)는 유예 후 재폴트되어 정상 알람.
        private readonly object _bootSync = new();
        private readonly HashSet<AxisId> _bootClearPending = new();
        private readonly Dictionary<AxisId, long> _bootFaultGraceUntil = new();
        private bool _bootClearErrLogged;
        private const int BootFaultGraceMs = 700;

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

                // 축별 파라미터를 드라이브에 다운로드(LabVIEW Init 과 동일 취지).
                // 콜드부팅 후에도 첫 실행에서 정상 원점복귀/이동하도록, 엔코더 분해능·속도·원점 파라미터를 세팅.
                foreach (var c in _configs)
                {
                    if (!_axisMap.TryGetValue(c.AxisNo, out var ax)) continue;

                    // 1) 엔코더 분해능(unit dist) — 속도/원점 해석의 기준이므로 먼저 설정. config 값 있을 때만.
                    if (c.EncoderPulsePerUnit is double ppu && ppu > 0)
                        TrySetup(() => comi.SetEncoderResolution(ax, ppu), c.AxisNo, "엔코더 분해능");

                    // 2) 기본 이동 속도 프로파일(Move 기준)
                    var mv = c.MotionConfig?.Move;
                    if (mv != null && mv.Velocity > 0)
                        TrySetup(() => comi.SetVelocity(ax, new VelocityProfile
                        {
                            Velocity = mv.Velocity,
                            Acceleration = mv.Acceleration,
                            Deceleration = mv.Deceleration
                        }), c.AxisNo, "이동 속도");

                    // 3) 원점복귀 속도 패턴 — config 에 있을 때만 다운로드(콜드부팅 Y 고속 주행 해결).
                    //    LabVIEW Set Home Parameters.vi 와 동일하게 '속도 패턴만' 설정(모드/방향/오프셋 미변경 → 안전).
                    //    없으면 미설정(현행 = 드라이브 기본값 유지).
                    if (c.Home is Platform.Domain.Models.Motion.HomeConfig h)
                        TrySetup(() => comi.SetHomeSpeedPattern(ax, h.Velocity, h.Acceleration, h.Deceleration, h.SpecVelocity),
                            c.AxisNo, $"원점 속도패턴(vel={h.Velocity}, acc={h.Acceleration}, dec={h.Deceleration}, spec={h.SpecVelocity})");
                }

                // 전원투입 시 드라이브가 래치된 폴트 상태로 부팅되는 경우가 있다(실장: Y축 raw=0x8038, bit3 ServoFault).
                // 이 시점에는 EtherCAT 개별 드라이브가 아직 addressable 하지 않아 즉시 리셋하면 -20280 로 실패한다.
                // → 여기서는 대상만 등록해두고, 실제 리셋은 상태 폴링(네트워크 준비 후)에서 축별 1회 수행한다.
                lock (_bootSync)
                {
                    _bootClearPending.Clear();
                    _bootFaultGraceUntil.Clear();
                    foreach (var ax in _axisMap.Values) _bootClearPending.Add(ax);
                }
                _bootClearErrLogged = false;

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

        /// <summary>Connect 중 파라미터 세팅을 안전 실행 — 실패해도 연결은 유지하고 경고만 로깅.</summary>
        private static void TrySetup(Action setup, string axisNo, string what)
        {
            try
            {
                setup();
                LoggerService.WriteToFile("INFO", $"[Comizoa Motion] {axisNo} {what} 설정 완료");
            }
            catch (Exception ex)
            {
                LoggerService.WriteToFile("WARN",
                    $"[Comizoa Motion] {axisNo} {what} 설정 실패(무시하고 진행): {ex.GetType().Name}: {ex.Message}");
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

                // 부팅 잔류 폴트 자동 해제: 여기까지 왔다는 건 GetState 성공 = 네트워크 준비 완료.
                // 축별 1회 리셋 후 유예 동안 폴트를 보류한다(지속 폴트는 유예 뒤 재폴트되어 정상 알람).
                bool alarm = st.Alarm;
                if (alarm)
                    alarm = HandleBootFaultClear(axisNo, ax);

                // 하드웨어 HomeAttained 는 홈 직후 일반 이동하면 사라지므로 소프트 래치를 신뢰.
                // 알람이 서면 원점 신뢰 불가 → 래치 해제.
                if (alarm)
                {
                    lock (_homedSync) _homedAxes.Remove(ax);
                }
                bool homed;
                lock (_homedSync) homed = _homedAxes.Contains(ax);
                s.IsHomeDone   = homed;
                s.IsAlarm      = alarm;
                s.UpperLimit   = st.UpperLimit;       // 상한 하드리밋(+EL)
                s.LowerLimit   = st.LowerLimit;       // 하한 하드리밋(-EL)
                s.HomeSensor   = st.HomeSensor;       // 원점(HOME) 센서
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

        /// <summary>
        /// 부팅 잔류 폴트를 축별 1회 자동 리셋한다. 반환값은 이번 주기에 알람으로 보고할지 여부.
        /// - 아직 리셋 안 한 축: 리셋 시도(성공 시 유예 시작·이번 주기 보류, 실패 시 다음 폴링 재시도)
        /// - 유예 중: 보류(드라이브가 폴트를 정리할 시간)
        /// - 유예 경과 후에도 폴트: 지속 폴트로 판단 → 알람 보고
        /// </summary>
        private bool HandleBootFaultClear(string axisNo, AxisId ax)
        {
            long now = Environment.TickCount64;
            lock (_bootSync)
            {
                if (_bootClearPending.Contains(ax))
                {
                    try
                    {
                        _comi!.SetAlarmState(ax, reset: true);
                        _bootClearPending.Remove(ax);
                        _bootFaultGraceUntil[ax] = now + BootFaultGraceMs;
                        LoggerService.WriteToFile("INFO",
                            $"[Comizoa Motion] 부팅 잔류 폴트 자동 리셋({axisNo}) — {BootFaultGraceMs}ms 유예 후에도 지속 시 알람");
                        return false;   // 이번 주기 보류
                    }
                    catch (Exception ex)
                    {
                        // 네트워크 미준비 등 → 소진하지 않고 다음 폴링에서 재시도
                        if (!_bootClearErrLogged)
                        {
                            _bootClearErrLogged = true;
                            LoggerService.WriteToFile("WARN",
                                $"[Comizoa Motion] 부팅 폴트 자동 리셋 실패({axisNo}) — 다음 폴링 재시도: {ex.GetType().Name}: {ex.Message}");
                        }
                        return false;   // 아직 판단 보류(네트워크 준비 전)
                    }
                }

                if (_bootFaultGraceUntil.TryGetValue(ax, out long until))
                {
                    if (now < until) return false;      // 유예 중 — 폴트 정리 대기
                    _bootFaultGraceUntil.Remove(ax);    // 유예 종료 — 이후엔 지속 폴트로 정상 알람
                }
            }
            return true;   // 지속 폴트 → 알람 보고
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
                catch
                {
                    // 홈 직후 등 ecmSx busy stuck 으로 -1020 거부되는 경우 대비: Stop 으로 상태 정리 후 1회 재시도.
                    // (시퀀스 이동은 완료대기로 직렬화되어 있어 여기서 busy 는 정상 모션이 아니라 stuck 이다.)
                    try
                    {
                        _comi!.Stop(ax);
                        System.Threading.Thread.Sleep(50);
                        _comi.SetVelocity(ax, new VelocityProfile { Velocity = vel, Acceleration = acc, Deceleration = dec });
                        move(ax);
                        return _comi.WaitForDone(ax, DefaultMoveTimeoutMs);
                    }
                    catch (Exception ex)
                    {
                        // 재시도까지 실패 → 무음 실패 방지 위해 사유·드라이브 상태 로깅.
                        // cmdidx=0(드라이브 거부: 서보 미Enable/원점 미완/busy 등)이면 ex.Message 에 err 코드가 담긴다.
                        string state = "";
                        try
                        {
                            var s = _comi!.GetState(ax);
                            state = $" | 상태(servoOn={s.ServoOn}, moving={s.IsMoving}, homed={s.IsHomed}, " +
                                    $"homeBusy={s.HomeBusy}, alarm={s.Alarm}, pos={s.Position:F3})";
                        }
                        catch { /* 상태 읽기도 실패 시 무시 */ }
                        LoggerService.WriteToFile("ERROR",
                            $"[Comizoa Motion] 이동 명령 실패({axisNo}, vel={vel}): {ex.GetType().Name}: {ex.Message}{state}");
                        return false;
                    }
                }
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
                    var pre = _comi!.GetState(ax);
                    var swHome = System.Diagnostics.Stopwatch.StartNew();
                    _comi.Home(ax);
                    bool done = _comi.WaitForHomeDone(ax, DefaultMoveTimeoutMs);
                    swHome.Stop();
                    if (done)
                    {
                        lock (_homedSync) _homedAxes.Add(ax);   // 완료 래치(이후 일반 이동해도 유지)
                        // ★ 홈(ecmHomeMot) 직후 단축모션(ecmSx) IsBusy 가 stuck(True)으로 남아, 곧바로 절대이동하면
                        //   드라이브가 -1020(busy)로 거부한다(실장: 첫 실행 READY 미이동, 2차엔 정상).
                        //   → Stop 으로 ecmSx 모션상태를 정리해 busy 를 해제한다(축은 이미 원점 정지 상태라 안전).
                        try { _comi.Stop(ax); System.Threading.Thread.Sleep(30); } catch { /* 무시 */ }

                        // ★ 원점복귀 후 위치를 0 으로 재정의. 홈 센서가 원하는 0 에서 떨어져 있어도
                        //   (실장: 홈 후 -25.11 잔류) 항상 원점=0 을 보장한다. 축은 이미 정지 상태라 안전.
                        //   ※ 실장 검증 필요: 재정의 시 축이 튀지 않는지 + 이후 절대이동이 정확한지.
                        try { _comi.SetPosition(ax, 0.0); }
                        catch (Exception ex)
                        {
                            LoggerService.WriteToFile("WARN",
                                $"[Comizoa Motion] Home({axisNo}) 위치 0 재정의 실패: {ex.Message}");
                        }
                    }
                    // 진단: 소요시간·위치변화로 실제 홈 동작이 오래 걸리는지(물리) vs 완료판정 지연인지 구분.
                    var post = _comi.GetState(ax);
                    LoggerService.WriteToFile("INFO",
                        $"[Comizoa Motion] Home({axisNo}) 결과 done={done} 소요={swHome.ElapsedMilliseconds}ms | " +
                        $"전(servoOn={pre.ServoOn}, homed={pre.IsHomed}, pos={pre.Position:F3}) → " +
                        $"후(servoOn={post.ServoOn}, homed={post.IsHomed}, homeBusy={post.HomeBusy}, pos={post.Position:F3})");
                    return done;
                }
                catch (Exception ex)
                {
                    LoggerService.WriteToFile("ERROR",
                        $"[Comizoa Motion] Home 명령 실패({axisNo}): {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
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
