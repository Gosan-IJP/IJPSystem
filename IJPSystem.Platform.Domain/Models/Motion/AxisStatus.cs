using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Models.Motion;
using System;

// AxisStatus: 축의 실시간 상태 정보 (드라이버 기반)
// - 예: 현재 위치, 동작 상태 등 수시로 변하는 값
// - 흐름: Driver → 업데이트 → UI 반영

////[AxisDeviceInfo]  < ---(매칭)--->  [AxisStatus]
////     (설정)                           (상태)
////       |                                  |
////   AxisNo(ID)---------------------- - AxisNo(ID)
////   Name                                 Name
////   MotionConfig(속도 / 가속도)          CurrentPos (현재위치)
////                                        IsMoving (이동여부)
///
namespace IJPSystem.Platform.Domain.Models.Motion
{
    /// <summary>
    /// 모터 축의 실시간 하드웨어 상태 및 피드백 정보를 담는 모델입니다.
    /// </summary>
    public class AxisStatus : ViewModelBase // ✅ 상속 추가 (SetProperty 사용을 위해 필수)
    {
        // --- 1. 축 식별 정보 ---
        public string AxisNo { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = "mm";

        // --- 2. 실시간 구동 상태 (UI 갱신이 필요한 속성들은 private field + SetProperty 사용) ---
        private double _currentPos;
        public double CurrentPos { get => _currentPos; set => SetProperty(ref _currentPos, value); }

        private double _targetPos;
        public double TargetPos { get => _targetPos; set => SetProperty(ref _targetPos, value); }

        private double _currentVel;
        public double CurrentVel { get => _currentVel; set => SetProperty(ref _currentVel, value); }

        private double _followingError;
        public double FollowingError { get => _followingError; set => SetProperty(ref _followingError, value); }

        private bool _isServoOn;
        public bool IsServoOn { get => _isServoOn; set => SetProperty(ref _isServoOn, value); }

        private bool _isMoving;
        public bool IsMoving { get => _isMoving; set => SetProperty(ref _isMoving, value); }

        private bool _isHomeDone;
        public bool IsHomeDone { get => _isHomeDone; set => SetProperty(ref _isHomeDone, value); }

        // --- 3. 에러 및 안전 상태 ---
        private bool _isAlarm;
        public bool IsAlarm { get => _isAlarm; set => SetProperty(ref _isAlarm, value); }

        public int AlarmCode { get; set; }
        public string AlarmMessage { get; set; } = "Normal";

        private bool _isEmergencyStop;
        public bool IsEmergencyStop { get => _isEmergencyStop; set => SetProperty(ref _isEmergencyStop, value); }

        // --- 4. 센서 상태 (AxisViewModel 및 UI와 이름 일치) ---
        // 기존 IsLimitPos, IsLimitNeg, IsHomeSensor를 아래 3개로 통일합니다.

        private bool _upperLimit;
        public bool UpperLimit   // 상한(+EL) 하드리밋 센서
        {
            get => _upperLimit;
            set => SetProperty(ref _upperLimit, value);
        }

        private bool _lowerLimit;
        public bool LowerLimit   // 하한(-EL) 하드리밋 센서
        {
            get => _lowerLimit;
            set => SetProperty(ref _lowerLimit, value);
        }

        private bool _homeSensor;
        public bool HomeSensor
        {
            get => _homeSensor;
            set => SetProperty(ref _homeSensor, value);
        }

        // --- 5. 공정 완료 신호 ---
        private bool _isInPosition;
        public bool IsInPosition { get => _isInPosition; set => SetProperty(ref _isInPosition, value); }

        private bool _isMotionDone;
        public bool IsMotionDone { get => _isMotionDone; set => SetProperty(ref _isMotionDone, value); }
    }
}