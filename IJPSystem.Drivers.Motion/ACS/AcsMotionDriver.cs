using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IJPSystem.Drivers.Motion.ACS
{
    // ACS Motion Control (SPiiPlus 시리즈) 드라이버 스켈레톤.
    //
    // SDK: ACS.SPiiPlusNET.dll 의 Api 클래스.
    // 모든 메서드 본문은 placeholder — 실제 SDK 호출은 TODO 표시 지점에 채운다.
    //
    // 참고 — SPiiPlus 일반 호출 예 (실제 API 명은 SDK 문서 기준):
    //   _api.OpenCommEthernet("10.0.0.100", 701);
    //   _api.Enable(axisIndex);
    //   _api.SetVelocity(axisIndex, vel);
    //   _api.SetAcceleration(axisIndex, acc);
    //   _api.SetDeceleration(axisIndex, dec);
    //   _api.ToPoint(0, axisIndex, position);
    //   var pos = _api.GetFPosition(axisIndex);
    public class AcsMotionDriver : IMotionDriver
    {
        // private readonly ACS.SPiiPlusNET.Api _api = new();
        private readonly Dictionary<string, AxisStatus> _axisStates = new();

        public bool IsConnected { get; private set; }

        public bool Connect()
        {
            // TODO: _api.OpenCommEthernet(ip, port) 또는 OpenCommSimulator
            IsConnected = true;
            return true;
        }

        public void Disconnect()
        {
            // TODO: 큐/이벤트 핸들러 해제, _api.CloseComm()
            IsConnected = false;
        }

        public void Initialize(List<AxisDeviceInfo> axisConfigs)
        {
            if (axisConfigs == null) return;
            _axisStates.Clear();
            foreach (var cfg in axisConfigs)
            {
                if (string.IsNullOrEmpty(cfg.AxisNo)) continue;
                _axisStates[cfg.AxisNo] = new AxisStatus
                {
                    AxisNo = cfg.AxisNo,
                    Name   = cfg.Name ?? "Unknown Axis",
                    Unit   = cfg.Unit ?? "mm",
                };
            }
            // TODO: 축 매핑 (AxisNo → ACS axis index) 테이블 구축
        }

        public AxisStatus GetStatus(string axisNo)
        {
            // TODO: _api.GetMotorState / GetFault 로 IsServoOn, IsMoving, IsAlarm, IsHomeDone 채우기
            //       _api.GetFPosition 으로 CurrentPos
            return _axisStates.TryGetValue(axisNo, out var s) ? s : new AxisStatus { AxisNo = axisNo };
        }

        public double GetActualPosition(string axisNo)
        {
            // TODO: return _api.GetFPosition(MapAxis(axisNo));
            return GetStatus(axisNo).CurrentPos;
        }

        public List<AxisStatus> GetAllStatus()
            => _axisStates.Values.OrderBy(s => s.AxisNo).ToList();

        public Task<bool> ServoOn(string axisNo, bool isOn)
        {
            // TODO: isOn ? _api.Enable(idx) : _api.Disable(idx);
            if (_axisStates.TryGetValue(axisNo, out var s))
                s.IsServoOn = isOn;
            return Task.FromResult(true);
        }

        public Task<bool> MoveAbs(string axisNo, double pos, double vel, double acc, double dec)
        {
            // TODO:
            //   _api.SetVelocity(idx, vel);
            //   _api.SetAcceleration(idx, acc);
            //   _api.SetDeceleration(idx, dec);
            //   _api.ToPoint(0, idx, pos);
            return Task.FromResult(false);
        }

        public Task<bool> MoveRel(string axisNo, double distance, double vel, double acc, double dec)
        {
            // TODO: ToPoint flags 에 RELATIVE 옵션 또는 GetFPosition + distance 후 ToPoint
            return Task.FromResult(false);
        }

        public Task<bool> MoveJog(string axisNo, bool isForward, double vel, double acc, double dec)
        {
            // TODO: _api.Jog(0, idx, isForward ? +vel : -vel);
            return Task.FromResult(false);
        }

        public Task<bool> Stop(string axisNo)
        {
            // TODO: _api.Halt(idx) — 부드러운 감속 정지. 비상정지는 Kill.
            return Task.FromResult(false);
        }

        public Task<bool> Home(string axisNo)
        {
            // TODO: 미리 다운로드된 홈 buffer 실행 — _api.RunBuffer(homeBufferIndex)
            //       완료 폴링: GetMotorState 의 ENABLED 비트 + IsMoving=false
            return Task.FromResult(false);
        }

        public Task<bool> ResetAlarm(string axisNo)
        {
            // TODO: _api.FaultClear(idx)
            if (_axisStates.TryGetValue(axisNo, out var s))
                s.IsAlarm = false;
            return Task.FromResult(true);
        }
    }
}
