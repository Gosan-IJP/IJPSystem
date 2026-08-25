using IJPSystem.Platform.Common.Enums;
using IJPSystem.Platform.Domain.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using IJPSystem.Platform.Application.Sequences;

namespace IJPSystem.Platform.HMI.ViewModels
{
    /// <summary>
    /// 조그만 하는 작은 뷰모델 — 어느 화면에서든 팝업으로 띄워 쓴다.
    ///
    /// <para>
    /// 드랍와처처럼 <b>화면을 보면서 축을 조금씩 움직여야</b> 하는 작업이 있다. 그때마다
    /// 모터 화면으로 나갔다 오면 보던 것을 놓친다. 축은 <see cref="MainViewModel.SharedAxisList"/>
    /// 하나뿐이라, 팝업으로 조작해도 모터 화면과 같은 축을 같은 규칙으로 움직인다.
    /// </para>
    /// <para>
    /// <b>액적 측정에 쓰는 네 축만 낸다</b> — X(스테이지), Z(헤드 승강), DW-X·DW-Y(카메라).
    /// Y·T 는 이 작업에서 만질 일이 없고, 조그판에 있으면 잘못 눌러 글라스가 움직인다.
    /// </para>
    /// </summary>
    public sealed class MotorJogViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;

        public MotorJogViewModel(MainViewModel mainVM)
        {
            _mainVM = mainVM ?? throw new ArgumentNullException(nameof(mainVM));
            MoveToDropCommand = new RelayCommand(async _ => await MoveToDropAsync(), _ => !IsMoving);
        }

        /// <summary>이 창이 다루는 축 번호. 구성에 없으면 그 버튼은 자동으로 비활성이 된다.</summary>
        private static readonly string[] Used = { "X", "Z", "DW-X", "DW-Y" };

        public AxisViewModel? AxisX   => FindAxis("X");
        public AxisViewModel? AxisZ   => FindAxis("Z");
        public AxisViewModel? AxisDwX => FindAxis("DW-X");
        public AxisViewModel? AxisDwY => FindAxis("DW-Y");

        /// <summary>
        /// 축 번호로 <b>정확히</b> 찾는다.
        ///
        /// <para>이름 앞글자로 찾으면 안 된다 — 조그는 축이 잘못 잡히면 그대로 모터가 나간다.
        /// 여기서는 "X" 가 "DW-X" 를 물지 않는 것이 특히 중요하다.</para>
        /// </summary>
        private AxisViewModel? FindAxis(string axisNo) =>
            _mainVM.SharedAxisList.FirstOrDefault(a =>
                string.Equals(a.Info?.AxisNo, axisNo, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 이 창이 움직일 수 있는 축. 창을 닫을 때 이것만 세운다 —
        /// 전 축을 세우면 다른 화면에서 돌던 이동까지 끊는다.
        /// </summary>
        public IEnumerable<AxisViewModel> Axes =>
            Used.Select(FindAxis).Where(a => a != null).Select(a => a!);

        // 조그 단위 — 0=연속(누르는 동안 이동), 0.01=10µm, 0.1=100µm, 1=1000µm (AxisViewModel.JogUnit 규약).
        private int _jogUnitIndex;

        /// <summary>0=Continuous, 1=10µm, 2=100µm, 3=1000µm. 회전축은 같은 수치가 deg 단위다.</summary>
        public int JogUnitIndex
        {
            get => _jogUnitIndex;
            set
            {
                if (!SetProperty(ref _jogUnitIndex, value)) return;

                // 이 창이 쓰는 축만 맞춘다. 축마다 단위가 다르면 어느 버튼이 얼마나
                // 움직이는지 알 수 없고, 안 쓰는 축까지 건드릴 이유는 없다.
                double unit = value switch { 1 => 0.01, 2 => 0.1, 3 => 1.0, _ => 0.0 };
                foreach (var ax in Axes) ax.JogUnit = unit;
            }
        }

        // ── 드랍 위치로 이동 ────────────────────────────────────────────
        //
        // 조그로 찾아가지 않고 티칭해 둔 자리(DROP WATCHER)로 한 번에 간다.
        // 패턴인쇄 화면의 [READY]·[PRINT START] 이동과 같은 경로·같은 안전 조건이다.

        private bool _isMoving;
        /// <summary>이동 중. 버튼을 잠가 이동 중 재요청을 막는다.</summary>
        public bool IsMoving
        {
            get => _isMoving;
            private set
            {
                if (SetProperty(ref _isMoving, value))
                    (MoveToDropCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ICommand MoveToDropCommand { get; }

        private async Task MoveToDropAsync()
        {
            const string point = PointNames.DropWatcher;

            if (_mainVM.HasActiveAlarm)
            {
                Log($"[JOG] {point} 이동 — 중단 (미해제 알람 존재)", LogLevel.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_mainVM.RecipeVM?.ActiveRecipeName))
            {
                Log($"[JOG] {point} 이동 — 중단 (적용된 레시피 없음)", LogLevel.Warning);
                return;
            }

            // 미원점 상태의 절대좌표 이동은 위험 — 인쇄 시작과 같은 기준으로 막는다.
            var allAxes = _mainVM.GetController()?.GetMachine()?.Motion?.GetAllStatus();
            if (allAxes == null || allAxes.Count == 0)
            {
                Log($"[JOG] {point} 이동 — 중단 (축 정보 없음 — 모션 드라이버 확인)", LogLevel.Error);
                return;
            }
            var notHomed = allAxes.Where(a => !a.IsHomeDone).Select(a => a.AxisNo).ToList();
            if (notHomed.Count > 0)
            {
                Log($"[JOG] {point} 이동 — 중단 (INITIALIZE 미수행, 미원점 축: {string.Join(", ", notHomed)})",
                    LogLevel.Warning);
                return;
            }

            IsMoving = true;
            try
            {
                var motion = new Services.MotionServiceAdapter(_mainVM);
                await motion.MoveToPointAsync(point, CancellationToken.None);
                Log($"[JOG] {point} 이동 완료");
            }
            catch (Exception ex)
            {
                Log($"[JOG] {point} 이동 실패 — {ex.Message}", LogLevel.Error);
            }
            finally { IsMoving = false; }
        }

        public void Log(string message, LogLevel level = LogLevel.Info) => _mainVM.AddLog(message, level);
    }
}
