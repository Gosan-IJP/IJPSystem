using IJPSystem.Platform.Domain.Enums;
using IJPSystem.Platform.Domain.Models.Motion;

namespace IJPSystem.Platform.Domain.Interfaces
{
    public interface IMachine
    {
        IIODriver IO { get; }
        IMotionDriver Motion { get; }
        IVisionDriver Vision { get; }
        MotionAxisRoot Config { get; set; }
        string MachineName { get; }

        void Initialize();
        void Terminate();

        // 시스템 상태 (램프)
        void SetSystemStatus(MachineState state);

        // 도어
        void OpenDoor();
        void CloseDoor();
        bool IsDoorLocked();

        // Vacuum
        void VacuumOn();
        void VacuumOff();

        // 센서
        bool IsGlassDetected();
        bool IsEmoActive();
        bool IsPressureOk(int swNo);

        // 온도 알람 (2호기 X008/X009 — 1호기는 미배선이라 항상 false)
        bool IsTempLowAlarm();
        bool IsTempHighAlarm();

        // 드레인 밸브 (2호기 Y011 — 1호기는 미배선이라 no-op)
        void SetDrainValve(bool on);

        // 시뮬레이션 전용 (가상 드라이버에서만 의미 있음)
        void SimulateDoorLockAfter(int delayMs);
    }
}