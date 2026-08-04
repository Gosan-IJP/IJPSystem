using Dapper;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.HMI.Common;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class MotorTeachingViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVM;
        private readonly string _connectionString;

        private ObservableCollection<TeachingPoint> _teachingPoints = new();
        private TeachingPoint? _selectedTeachingPoint;
        private readonly RelayCommand _moveToPointCommand;

        #region Properties

        public ObservableCollection<TeachingPoint> TeachingPoints
        {
            get => _teachingPoints;
            set => SetProperty(ref _teachingPoints, value);
        }

        public TeachingPoint? SelectedTeachingPoint
        {
            get => _selectedTeachingPoint;
            set
            {
                SetProperty(ref _selectedTeachingPoint, value);
                _moveToPointCommand.RaiseCanExecuteChanged();
            }
        }

        public ObservableCollection<AxisViewModel> AxisList => _mainVM.SharedAxisList;

        // XY 패드가 X/Y 를 담당하므로, 나머지 축(Z/T/DW-X/DW-Y…)만 버튼으로 만들어 준다.
        // AxisList 기반이라 3축 장비면 2개, 9호기(6축)면 4개가 자동으로 나온다 — 화면은 축 수를 모른다.
        public IEnumerable<AxisViewModel> JogAxisList =>
            AxisList.Where(a => a.Info.AxisNo is not ("X" or "Y"));

        // 현재 편집 중인 레시피 (데이터 로드/저장 기준)
        public string EditingRecipeName => _mainVM.RecipeVM.SelectedRecipeName;

        // 설비에 실제 적용된 레시피
        public string ActiveRecipeName => _mainVM.RecipeVM.ActiveRecipeName;

        // 편집 레시피와 적용 레시피가 다를 때 true
        public bool IsRecipeMismatch => EditingRecipeName != ActiveRecipeName;

        // ── 조그 스텝 모드 ───────────────────────────────────────────────────────
        // 예전에는 SELECT AXIS 콤보의 '선택 축'이 이 상태를 들고 있었다. 화면에 보이지도 않는 축에 따라
        // 같은 라디오의 의미가 달라져서, 콤보를 없애면서 스텝 모드를 화면(=이 VM) 소유로 올렸다.
        //
        // 스텝 규칙(미세=10µm/0.1°, 거침=100µm/1°)은 축 제어 화면과 공유한다 → Common/JogStep.cs
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

        /// <summary>이 축에 적용할 조그 스텝(축의 논리단위). 0 = 연속(Conti).</summary>
        public double JogStepFor(AxisViewModel axis) => JogStep.For(_jogStep, axis.Info.Unit);

        #endregion

        public ICommand SaveTeachingPointsCommand { get; }
        public ICommand ApplyCurrentToPointCommand { get; }
        public ICommand MoveToPointCommand => _moveToPointCommand;

        public MotorTeachingViewModel(MainViewModel mainViewModel)
        {
            _mainVM = mainViewModel;

            // RecipeViewModel과 동일한 DB를 참조 (경로 중복 계산 제거)
            _connectionString = _mainVM.RecipeVM.DbConnectionString;

            // MoveToPointCommand: 티칭 행이 선택되면 활성화.
            // (예전엔 '선택 축'도 조건이었는데, OnMoveToPoint 는 AxisList 전체를 이동시키므로 무관했다)
            _moveToPointCommand = new RelayCommand(
                async _ => await OnMoveToPoint(),
                _ => _selectedTeachingPoint != null);

            // ※ 축 위치 카드는 AxisViewModel.CurrentPos 에 직접 바인딩한다(자체 PropertyChanged).
            //    화면이 축 이름을 알 필요가 없어져서, 축별 중계 핸들러도 필요 없다.

            // 레시피 변경 감지 → 헤더 표시 실시간 갱신
            _mainVM.RecipeVM.PropertyChanged += OnRecipeVmPropertyChanged;

            SaveTeachingPointsCommand = new RelayCommand(_ => SaveToDatabase());
            ApplyCurrentToPointCommand = new RelayCommand(_ => OnApplyCurrentPosition());

            // 데이터 로드는 View.Loaded 에서 수행 (중복 호출 방지)
        }

        private void OnRecipeVmPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RecipeViewModel.SelectedRecipeName))
            {
                OnPropertyChanged(nameof(EditingRecipeName));
                OnPropertyChanged(nameof(IsRecipeMismatch));
            }
            else if (e.PropertyName == nameof(RecipeViewModel.ActiveRecipeName))
            {
                OnPropertyChanged(nameof(ActiveRecipeName));
                OnPropertyChanged(nameof(IsRecipeMismatch));
            }
        }

        public void LoadFromDatabase()
        {
            string recipeName = _mainVM.RecipeVM.SelectedRecipeName;
            if (string.IsNullOrEmpty(recipeName))
            {
                InitializeDefaultPoints();
                return;
            }

            try
            {
                using var db = new SqliteConnection(_connectionString);
                db.Open();
                var sql = @"SELECT p.* FROM RecipeDetails_Position p
                    JOIN Recipes r ON p.RecipeId = r.Id
                    WHERE r.Name = @recipeName";

                var rawData = db.Query<dynamic>(sql, new { recipeName }).ToList();

                if (rawData.Count > 0)
                {
                    var grouped = rawData.GroupBy(d => (string)d.PointName)
                        .Select(g => new TeachingPoint
                        {
                            PointName = g.Key,
                            Positions = g.ToDictionary(x => (string)x.AxisName, x => (double)x.PosValue),
                            AxisUsed  = g.ToDictionary(x => (string)x.AxisName, x => (bool)(Convert.ToInt32(x.IsUsed) != 0))
                        }).ToList();

                    // Pulse 제외 포인트(Blotting/NJI 등) — PointNames.All 에 없는 DB 잔존 행은 표시 제외
                    grouped = grouped.Where(g => PointNames.All.Any(
                        n => string.Equals(n, g.PointName, StringComparison.OrdinalIgnoreCase))).ToList();

                    foreach (var pt in grouped)
                    {
                        foreach (var axis in AxisList)
                        {
                            if (!pt.Positions.ContainsKey(axis.Info.Name))
                                pt.Positions[axis.Info.Name] = 0.0;
                            if (!pt.AxisUsed.ContainsKey(axis.Info.Name))
                                pt.AxisUsed[axis.Info.Name] = true;
                        }
                    }

                    // PointNames에 신규 추가된 포인트(예: BLOTTING)가 DB에 없으면 기본값으로 자동 보강
                    // 다음 저장 시 새 행으로 영구 기록됨
                    foreach (var name in PointNames.All)
                    {
                        if (grouped.Any(g => g.PointName == name)) continue;
                        var tp = new TeachingPoint { PointName = name };
                        foreach (var axis in AxisList)
                        {
                            tp.Positions[axis.Info.Name] = 0.0;
                            tp.AxisUsed[axis.Info.Name]  = true;
                        }
                        grouped.Add(tp);
                    }

                    TeachingPoints = new ObservableCollection<TeachingPoint>(grouped);
                }
                else
                {
                    InitializeDefaultPoints();
                }
            }
            catch (SqliteException ex) when (ex.Message.Contains("no such table"))
            {
                _mainVM.AddLog("[MOTION] Teach 테이블 없음 — 기본 리스트 생성", LogLevel.Warning);
                InitializeDefaultPoints();
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] Teach 로드 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("LOG-TEACH-LOAD-FAIL");
                InitializeDefaultPoints();
            }
        }

        private void InitializeDefaultPoints()
        {
            var list = new ObservableCollection<TeachingPoint>();
            foreach (var n in PointNames.All)
            {
                var tp = new TeachingPoint { PointName = n };
                foreach (var axis in AxisList)
                {
                    tp.Positions[axis.Info.Name] = 0.0;
                    tp.AxisUsed[axis.Info.Name]  = true;   // 기본은 모든 축 사용
                }
                list.Add(tp);
            }
            TeachingPoints = list;
        }

        private void SaveToDatabase()
        {
            string name = _mainVM.RecipeVM.SelectedRecipeName;
            var result = Dialogs.Show(
                Loc.T("Msg_TeachSaveConfirm", name),
                Loc.T("Msg_TeachSaveTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK)
                return;
            try
            {
                using var db = new SqliteConnection(_connectionString);
                db.Open();
                int recipeId = db.QueryFirstOrDefault<int>("SELECT Id FROM Recipes WHERE Name = @name", new { name });
                if (recipeId == 0)
                {
                    Dialogs.Show("레시피를 찾을 수 없습니다.");
                    return;
                }

                using var trans = db.BeginTransaction();
                db.Execute("DELETE FROM RecipeDetails_Position WHERE RecipeId = @recipeId", new { recipeId }, trans);
                foreach (var pt in TeachingPoints)
                {
                    foreach (var pos in pt.Positions)
                    {
                        int isUsed = (pt.AxisUsed.TryGetValue(pos.Key, out var u) ? u : true) ? 1 : 0;
                        db.Execute(
                            "INSERT INTO RecipeDetails_Position (RecipeId, PointName, AxisName, PosValue, IsUsed) VALUES (@recipeId, @pName, @aName, @val, @used)",
                            new { recipeId, pName = pt.PointName, aName = pos.Key, val = pos.Value, used = isUsed }, trans);
                    }
                }
                trans.Commit();

                // 레시피 화면도 같은 RecipeDetails_Position을 별도 메모리 컬렉션으로 들고 있으므로
                // 저장 직후 DB 최신값으로 다시 로드해 두 화면의 티칭 값을 일치시킴
                _mainVM.RecipeVM.ReloadTeachingPoints();

                // 활성 레시피를 티칭 저장한 경우, 시퀀스(오토프린트/Initialize)가 참조하는 활성 스냅샷도 갱신.
                // RecipeView 저장 경로는 갱신하지만 이 화면 저장은 누락돼 있어, 티칭에서 좌표를 바꿔 저장해도
                // 스냅샷이 옛 값으로 남아 오토프린트가 이전 좌표로 이동하던 버그를 수정한다.
                if (string.Equals(name, _mainVM.RecipeVM.ActiveRecipeName, System.StringComparison.Ordinal))
                {
                    _mainVM.RecipeVM.RefreshActivePointsSnapshot();
                    _mainVM.AddLog($"[MOTION] Teach [{name}] — 활성 레시피 스냅샷 갱신됨", LogLevel.Info);
                }

                _mainVM.AddLog($"[MOTION] Teach [{name}] 저장 완료", LogLevel.Success);
                //Dialogs.Show("저장 완료");
            }
            catch (Exception ex)
            {
                _mainVM.AddLog($"[MOTION] Teach 저장 실패: {ex.Message}", LogLevel.Error);
                _mainVM.AlarmVM.RaiseAlarm("LOG-TEACH-SAVE-FAIL");
            }
        }

        private void OnApplyCurrentPosition()
        {
            if (_selectedTeachingPoint == null) return;
            foreach (var axis in AxisList)
                _selectedTeachingPoint.Positions[axis.Info.Name] = axis.Status?.CurrentPos ?? 0.0;

            // Dictionary 변경 후 DataGrid 갱신 (컬렉션 재생성으로 바인딩 갱신)
            var selected = _selectedTeachingPoint;
            TeachingPoints = new ObservableCollection<TeachingPoint>(TeachingPoints);
            SelectedTeachingPoint = selected;
        }

        private async Task OnMoveToPoint()
        {
            var point = _selectedTeachingPoint;
            if (point == null) return;

            // 해당 포인트에서 위치값이 있고 '사용(AxisUsed)' 체크된 모든 축을 동시에 이동
            var moves = new List<Task>();
            var moved = new List<string>();
            foreach (var axis in AxisList)
            {
                string name = axis.Info.Name;
                if (!point.Positions.TryGetValue(name, out double targetPos)) continue;
                if (point.AxisUsed.TryGetValue(name, out bool used) && !used) continue;

                axis.IsAbsMode = true;
                axis.TargetPosition = targetPos;
                moves.Add(axis.MoveAsync());
                moved.Add($"{name}:{targetPos:F3}");
            }

            if (moves.Count == 0)
            {
                _mainVM.AddLog($"[MOTION] Teach Move: {point.PointName} — 이동할 축 없음", LogLevel.Warning);
                return;
            }

            _mainVM.AddLog($"[MOTION] Teach Move: {point.PointName} → {string.Join(", ", moved)}");
            await Task.WhenAll(moves);
        }

        /// <summary>View가 Unloaded될 때 호출 — 이벤트 구독 전체 해제</summary>
        public void Cleanup()
        {
            // 레시피 변경 핸들러 해제
            _mainVM.RecipeVM.PropertyChanged -= OnRecipeVmPropertyChanged;
        }
    }

    public class TeachingPoint : ViewModelBase
    {
        public string PointName { get; set; } = "";
        public Dictionary<string, double> Positions { get; set; } = new();
        public Dictionary<string, bool>   AxisUsed  { get; set; } = new();

        // Dictionary 자체는 indexer 변경을 통지하지 않으므로,
        // CheckBox 클릭 후 외부에서 호출해 같은 행의 다른 바인딩(예: TextBox.IsEnabled)을 즉시 갱신
        public void RefreshAxisUsed() => OnPropertyChanged(nameof(AxisUsed));
    }
}
