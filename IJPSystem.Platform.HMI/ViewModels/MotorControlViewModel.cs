using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Application.Sequences;   // PointNames — 티칭 위치 목록
using IJPSystem.Platform.Domain.Interfaces;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.HMI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class MotorControlViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;

        public ObservableCollection<AxisViewModel> AxisList => _mainVM.SharedAxisList;
        private AxisViewModel? _selectedAxis;
        public AxisViewModel? SelectedAxis
        {
            get => _selectedAxis;
            set => SetProperty(ref _selectedAxis, value);
        }

        // XY D-패드 조그 버튼의 IsEnabled 바인딩용. (각 축의 CanJog로 활성화 제어)
        public AxisViewModel? AxisX => ResolveByTag("X");
        public AxisViewModel? AxisY => ResolveByTag("Y");

        // ★AxisNo 정확히 일치를 먼저 본다. 이름 포함(Contains)만으로 찾으면 "DW-X AXIS" 도 "X" 에 걸려,
        //   config 순서가 바뀌면 X 패드가 DW-X 를 움직인다. 포함 검색은 옛 이름 표기를 위한 폴백으로만 남긴다.
        //   ※ 코드비하인드 ResolveAxis 도 같은 규칙이어야 한다(표시와 동작이 갈리면 안 됨).
        private AxisViewModel? ResolveByTag(string tag) =>
            AxisList.FirstOrDefault(a => string.Equals(a.Info?.AxisNo, tag, StringComparison.OrdinalIgnoreCase))
            ?? AxisList.FirstOrDefault(a => a.Info?.Name != null &&
                a.Info.Name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);

        // XY 패드가 X/Y 를 담당하므로 나머지 축(Z/T/DW-X/DW-Y…)만 조그 버튼으로 만든다.
        // AxisList 기반이라 3축 장비면 Z 하나, 9호기(6축)면 4개가 자동으로 나온다.
        public IEnumerable<AxisViewModel> JogAxisList =>
            AxisList.Where(a => a.Info.AxisNo is not ("X" or "Y"));

        // ── 조그 스텝 모드 ───────────────────────────────────────────────────────
        // 예전에는 '선택 축(SelectedAxis)'이 이 상태를 들고, 조그할 때 대상 축으로 복사했다.
        // 화면에 안 보이는 축에 따라 같은 라디오의 의미가 달라지는 구조라 화면(=이 VM) 소유로 올린다.
        // 규칙은 위치 티칭 화면과 공유한다 → Common/JogStep.cs (미세=10µm/0.1°, 거침=100µm/1°)
        private JogStepMode _jogStep = JogStepMode.Continuous;

        public bool IsJogContinuity { get => _jogStep == JogStepMode.Continuous; set { if (value) SetJogStep(JogStepMode.Continuous); } }
        public bool IsStepFine      { get => _jogStep == JogStepMode.Fine;       set { if (value) SetJogStep(JogStepMode.Fine); } }
        public bool IsStepCoarse    { get => _jogStep == JogStepMode.Coarse;     set { if (value) SetJogStep(JogStepMode.Coarse); } }

        private void SetJogStep(JogStepMode mode)
        {
            if (_jogStep == mode) return;
            _jogStep = mode;
            OnPropertyChanged(nameof(IsJogContinuity));
            OnPropertyChanged(nameof(IsStepFine));
            OnPropertyChanged(nameof(IsStepCoarse));
        }

        /// <summary>이 축에 적용할 조그 스텝(축의 논리단위). 0 = 연속(Cont.).</summary>
        public double JogStepFor(AxisViewModel axis) => JogStep.For(_jogStep, axis.Info.Unit);
       
        public ICommand AllServoOnCommand  { get; }
        public ICommand AllServoOffCommand { get; }
        public ICommand AllStopCommand     { get; }

        // ── 티칭 위치 이동 ────────────────────────────────────────────────────
        // 버튼을 하드코딩하지 않고 PointNames.All 을 그대로 돌린다 — 포인트가 늘거나 줄면
        // 화면이 따라온다(티칭 화면도 같은 목록을 쓴다). 호기별 포인트 차이도 이걸로 흡수된다.
        public IReadOnlyList<string> TeachPoints => PointNames.All;

        /// <summary>티칭 위치로 이동. CommandParameter 로 포인트 이름을 받는다.</summary>
        public ICommand MovePointCommand { get; }

        private bool _isPointMoving;
        public bool IsPointMoving
        {
            get => _isPointMoving;
            private set
            {
                if (SetProperty(ref _isPointMoving, value))
                    (MovePointCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private double _jogSpeedScale = 1.0;
        public double JogSpeedScale
        {
            get => _jogSpeedScale;
            set
            {
                if (SetProperty(ref _jogSpeedScale, value))
                {
                    OnPropertyChanged(nameof(IsJogSpeedSlow));
                    OnPropertyChanged(nameof(IsJogSpeedNormal));
                    OnPropertyChanged(nameof(IsJogSpeedFast));
                }
            }
        }
        public bool IsJogSpeedSlow   { get => JogSpeedScale == 0.25; set { if (value) JogSpeedScale = 0.25; } }
        public bool IsJogSpeedNormal { get => JogSpeedScale == 1.0;  set { if (value) JogSpeedScale = 1.0; } }
        public bool IsJogSpeedFast   { get => JogSpeedScale == 2.0;  set { if (value) JogSpeedScale = 2.0; } }

        public MotorControlViewModel(MainViewModel mainViewModel)
        {
            _mainVM = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

            SelectedAxis = AxisList.FirstOrDefault();

            AllServoOnCommand  = new RelayCommand(async _ => await ExecuteAllServoOn());
            AllServoOffCommand = new RelayCommand(async _ => await ExecuteAllServoOff());
            AllStopCommand     = new RelayCommand(async _ => await ExecuteAllStop());

            MovePointCommand   = new RelayCommand(
                async p => await MoveToPointAsync(p as string ?? ""),
                _ => !IsPointMoving);
        }

        // 티칭 위치 이동. 절대좌표 이동이라 원점 미완료 상태에서는 위험하다 —
        // 패턴 인쇄 화면과 같은 기준으로 막는다(알람/레시피/원점).
        private async Task MoveToPointAsync(string pointName)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return;

            if (_mainVM.HasActiveAlarm)
            {
                _mainVM.AddLog($"[MOTION] {pointName} 이동 — 중단 (미해제 알람 존재)", LogLevel.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_mainVM.RecipeVM?.ActiveRecipeName))
            {
                _mainVM.AddLog($"[MOTION] {pointName} 이동 — 중단 (적용된 레시피 없음)", LogLevel.Warning);
                return;
            }

            var allAxes = _mainVM.GetController()?.GetMachine()?.Motion?.GetAllStatus();
            if (allAxes == null || allAxes.Count == 0)
            {
                _mainVM.AddLog($"[MOTION] {pointName} 이동 — 중단 (축 정보 없음 — 모션 드라이버 확인)", LogLevel.Error);
                return;
            }
            var notHomed = allAxes.Where(a => !a.IsHomeDone).Select(a => a.AxisNo).ToList();
            if (notHomed.Count > 0)
            {
                _mainVM.AddLog(
                    $"[MOTION] {pointName} 이동 — 중단 (INITIALIZE 미수행, 미원점 축: {string.Join(", ", notHomed)})",
                    LogLevel.Warning);
                return;
            }

            IsPointMoving = true;
            try
            {
                var motion = new Services.MotionServiceAdapter(_mainVM);
                await motion.MoveToPointAsync(pointName, System.Threading.CancellationToken.None);
                _mainVM.AddLog($"[MOTION] {pointName} 이동 완료", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] {pointName} 이동 실패 — {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsPointMoving = false;
            }
        }

        private async Task ExecuteAllServoOn()
        {
            _mainVM.AddLog("[MOTION] All Axes Servo ON Command.");
            try
            {
                await Task.WhenAll(AxisList.Select(a => a.ForceServoOnAsync()));
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] All Servo ON failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task ExecuteAllServoOff()
        {
            _mainVM.AddLog("[MOTION] All Axes Servo OFF Command.");
            try
            {
                await Task.WhenAll(AxisList.Select(a => a.ForceServoOffAsync()));
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] All Servo OFF failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task ExecuteAllStop()
        {
            _mainVM.AddLog("[MOTION] Stop all axes!");
            try
            {
                await Task.WhenAll(AxisList.Select(a => a.StopAsync()));
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] All Stop failed: {ex.Message}", LogLevel.Error);
            }
        }
        
    }
}