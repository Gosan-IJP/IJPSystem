using Dapper;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.HMI;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Views;
using static IJPSystem.Platform.HMI.Common.Loc;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace IJPSystem.Platform.HMI.ViewModels
{
    
    public enum RecipeDataType
    {
        Motor, Teach, Other
    }

    public class RecipeViewModel : ViewModelBase
    {
        private readonly string _dbPath;
        public string DbConnectionString => _dbPath;
        private readonly Action<string, LogLevel> _addLogAction;
        private readonly Action<string>? _raiseAlarm;
        private bool _isLoading = false;

        
        private ObservableCollection<TeachingPoint> _teachingPoints = new();
        public ObservableCollection<TeachingPoint> TeachingPoints
        {
            get => _teachingPoints;
            private set => SetProperty(ref _teachingPoints, value);
        }

        private string _activeRecipeName = string.Empty;
        public string ActiveRecipeName
        {
            get => _activeRecipeName;
            set
            {
                if (SetProperty(ref _activeRecipeName, value))
                    RaiseDeleteCanExecute();
            }
        }

        // ── 적용된 레시피 snapshot ──
        // APPLY 시점에 DB → 메모리로 복사. 시퀀스는 이 snapshot만 참조하므로
        // 편집 중인 레시피(저장만 된 것)는 시퀀스에 영향 주지 않음.
        // 1) 포인트: PointName → (AxisName → PosValue) (IsUsed=1 만)
        // 2) 모션 프로파일: AxisNo → MotionDetailConfig (Move/Jog/Printing 속도·가감속)
        private readonly Dictionary<string, Dictionary<string, double>> _activePointsSnapshot
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MotionDetailConfig> _activeMotionConfigSnapshot
            = new(StringComparer.OrdinalIgnoreCase);

        // 활성(APPLY된) 레시피의 프린팅수(Swath)/헤드길이 — 오토프린트 시퀀스 생성에 사용
        public int ActiveSwath { get; private set; } = 1;
        public double ActiveHeadLength { get; private set; } = 0;

        public IReadOnlyDictionary<string, double>? GetActivePoint(string pointName) =>
            _activePointsSnapshot.TryGetValue(pointName, out var dict) ? dict : null;

        public MotionDetailConfig? GetActiveMotionConfig(string axisNo) =>
            _activeMotionConfigSnapshot.TryGetValue(axisNo, out var cfg) ? cfg : null;

        // 활성 레시피의 모든 사용 포인트(IsUsed=1) + 모션 프로파일을 DB에서 한 번에 읽어 snapshot 갱신
        public void RefreshActivePointsSnapshot()
        {
            _activePointsSnapshot.Clear();
            _activeMotionConfigSnapshot.Clear();
            ActiveSwath = 1;
            ActiveHeadLength = 0;
            if (string.IsNullOrEmpty(_activeRecipeName)) return;

            try
            {
                using var db = new SqliteConnection(_dbPath);
                db.Open();

                // 프린팅수(Swath) / 헤드길이 — 활성 레시피 기준
                ActiveSwath = db.QueryFirstOrDefault<int?>(
                    "SELECT Swath FROM Recipes WHERE Name=@recipe", new { recipe = _activeRecipeName }) ?? 1;
                ActiveHeadLength = db.QueryFirstOrDefault<double?>(
                    "SELECT HeadLength FROM Recipes WHERE Name=@recipe", new { recipe = _activeRecipeName }) ?? 0;

                // 1) 포인트
                const string sqlPoints = @"
                    SELECT p.PointName, p.AxisName, p.PosValue FROM RecipeDetails_Position p
                    JOIN Recipes r ON p.RecipeId = r.Id
                    WHERE r.Name = @recipe AND p.IsUsed = 1";
                var pointRows = db.Query(sqlPoints, new { recipe = _activeRecipeName }).ToList();
                foreach (var r in pointRows)
                {
                    string pn = (string)r.PointName;
                    string an = (string)r.AxisName;
                    double pv = (double)r.PosValue;
                    if (!_activePointsSnapshot.TryGetValue(pn, out var dict))
                    {
                        dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        _activePointsSnapshot[pn] = dict;
                    }
                    dict[an] = pv;
                }

                // 2) 모션 프로파일 (AxisNo 기준)
                const string sqlMotor = @"
                    SELECT d.* FROM RecipeDetails_Motor d
                    JOIN Recipes r ON d.RecipeId = r.Id
                    WHERE r.Name = @recipe";
                var motorRows = db.Query<dynamic>(sqlMotor, new { recipe = _activeRecipeName }).ToList();
                foreach (var d in motorRows)
                {
                    string axisNo = (string)d.AxisNo;
                    _activeMotionConfigSnapshot[axisNo] = new MotionDetailConfig
                    {
                        Move = new Profile
                        {
                            Velocity     = Convert.ToDouble(d.MoveVel ?? 0),
                            Acceleration = Convert.ToDouble(d.MoveAcc ?? 0),
                            Deceleration = Convert.ToDouble(d.MoveDec ?? 0),
                        },
                        Jog = new Profile
                        {
                            Velocity     = Convert.ToDouble(d.JogVel ?? 0),
                            Acceleration = Convert.ToDouble(d.JogAcc ?? 0),
                            Deceleration = Convert.ToDouble(d.JogDec ?? 0),
                        },
                        Printing = new Profile
                        {
                            Velocity     = Convert.ToDouble(d.PrintVel ?? 0),
                            Acceleration = Convert.ToDouble(d.PrintAcc ?? 0),
                            Deceleration = Convert.ToDouble(d.PrintDec ?? 0),
                        },
                    };
                }

                _addLogAction?.Invoke(
                    $"[RECIPE] '{_activeRecipeName}' snapshot 갱신 — {_activePointsSnapshot.Count} points / {_activeMotionConfigSnapshot.Count} motors",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                _addLogAction?.Invoke($"[RECIPE] snapshot 로드 실패: {ex.Message}", LogLevel.Error);
            }
        }
        private string _currentLanguage = "KO";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set => SetProperty(ref _currentLanguage, value);
        }
        #region Properties
        private ObservableCollection<string> _recipeNames = new();
        public ObservableCollection<string> RecipeNames
        {
            get => _recipeNames;
            set => SetProperty(ref _recipeNames, value);
        }

        private string _selectedRecipeName = string.Empty;
        public string SelectedRecipeName
        {
            get => _selectedRecipeName;
            set
            {
                // 1. 같은 이름을 클릭했으면 무시
                if (_selectedRecipeName == value) return;

                // 2. 수정 중(IsDirty)이라면 사용자에게 물어보기
                if (IsDirty)
                {
                    var result = Dialogs.Show(
                        T("Msg_RecipeDirtyConfirm", _selectedRecipeName),
                        T("Msg_RecipeDirtyTitle"),
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // 사용자가 '예'를 누르면 현재 데이터를 저장함
                        ExecuteSaveRecipe();
                    }
                    else if (result == MessageBoxResult.Cancel)
                    {
                        // '취소'를 누르면 리스트 선택이 바뀌지 않도록 UI에 알림 (이전 값 유지)
                        OnPropertyChanged(nameof(SelectedRecipeName));
                        return;
                    }
                    // '아니오'를 누르면 저장하지 않고 그냥 다음 레시피로 넘어감
                }

                // 3. 실제 값 변경 및 데이터 로드
                _selectedRecipeName = value;
                OnPropertyChanged(nameof(SelectedRecipeName));
                RaiseDeleteCanExecute();

                if (!string.IsNullOrEmpty(value))
                {
                    LoadAllRecipeData(value); // 기존 데이터 로드 메서드 호출
                    IsDirty = false;          // 새로운 레시피를 불러왔으므로 초기화
                }
            }
        }
        private bool _isDirty;
        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (_isLoading && value == true) return;
                if (_isDirty != value)
                {
                    _isDirty = value;
                    OnPropertyChanged(nameof(IsDirty)); // UI 갱신 신호
                }
            }
        }

        private RecipeDataType _currentDataType = RecipeDataType.Motor;
        public RecipeDataType CurrentDataType
        {
            get => _currentDataType;
            set => SetProperty(ref _currentDataType, value);
        }

        private int _purgeTime;
        public int PurgeTime
        {
            get => _purgeTime;
            set
            {
                int clamped = Math.Max(0, Math.Min(60, value));
                if (SetProperty(ref _purgeTime, clamped) && !_isLoading)
                    IsDirty = true;
            }
        }

        // 프린팅수(Swath) — 기타정보 화면 콤보박스(1~5)에 바인딩
        public int[] SwathOptions { get; } = { 1, 2, 3, 4, 5 };

        private int _swathCount = 1;
        public int SwathCount
        {
            get => _swathCount;
            set
            {
                int clamped = Math.Max(1, Math.Min(5, value));
                if (SetProperty(ref _swathCount, clamped) && !_isLoading)
                    IsDirty = true;
            }
        }

        // Head 길이(mm) — 기타정보 화면 텍스트박스에 바인딩
        private double _headLength;
        public double HeadLength
        {
            get => _headLength;
            set
            {
                double clamped = Math.Max(0, value);
                if (SetProperty(ref _headLength, clamped) && !_isLoading)
                    IsDirty = true;
            }
        }

        // 도어 사용 유무 — 기타정보 화면 콤보박스에 바인딩
        // Why: 현장 설치 환경에 따라 안전키 미연결 시 운전 시작 차단을 해제할 수 있어야 함
        public bool IsDoorCheckEnabled
        {
            get => IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Current.IsDoorCheckEnabled;
            set
            {
                if (IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Current.IsDoorCheckEnabled == value) return;
                IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Current.IsDoorCheckEnabled = value;
                try
                {
                    IJPSystem.Platform.Infrastructure.Config.AppSettingsService.Save();
                    _addLogAction?.Invoke(
                        value ? "[CONFIG] 도어 사용 ON" : "[CONFIG] 도어 사용 OFF (가동 전 도어 잠금 체크 우회)",
                        value ? LogLevel.Info : LogLevel.Warning);
                }
                catch (Exception ex)
                {
                    _addLogAction?.Invoke($"[CONFIG] 설정 저장 실패: {ex.Message}", LogLevel.Error);
                }
                OnPropertyChanged(nameof(IsDoorCheckEnabled));
            }
        }
        private ObservableCollection<AxisViewModel> _axisList = new ObservableCollection<AxisViewModel>();
        public ObservableCollection<AxisViewModel> AxisList
        {
            get => _axisList;
            set => SetProperty(ref _axisList, value);
        }
        
        #endregion

        #region Commands
        public ICommand CreateRecipeCommand   { get; }
        public ICommand DeleteRecipeCommand   { get; }
        public ICommand SaveRecipeCommand     { get; }
        public ICommand ApplyRecipeCommand    { get; }
        public ICommand RenameRecipeCommand   { get; }
        public ICommand CopyRecipeCommand     { get; }
        public ICommand CancelEditCommand     { get; }
        public ICommand MoveRecipeUpCommand   { get; }
        public ICommand MoveRecipeDownCommand { get; }
        public ICommand OpenDiffCommand       { get; }
        #endregion

        public RecipeViewModel(ObservableCollection<AxisViewModel> sharedAxes,
                               Action<string, LogLevel> addLogAction,
                               Action<string>? raiseAlarm = null)
        {
            _dbPath = $"Data Source={GetDbPath("RecipeData.db")}";
            AxisList = sharedAxes;
            _addLogAction = addLogAction;
            _raiseAlarm = raiseAlarm;

            InitDatabase();

            CreateRecipeCommand   = new RelayCommand(_ => ExecuteCreateRecipe());
            DeleteRecipeCommand   = new RelayCommand(_ => ExecuteDeleteRecipe(), _ => !string.IsNullOrEmpty(SelectedRecipeName) && SelectedRecipeName != ActiveRecipeName);
            SaveRecipeCommand     = new RelayCommand(_ => ExecuteSaveRecipe());
            ApplyRecipeCommand    = new RelayCommand(_ => ExecuteApplyRecipe());
            RenameRecipeCommand   = new RelayCommand(_ => ExecuteRenameRecipe());
            CopyRecipeCommand     = new RelayCommand(_ => ExecuteCopyRecipe());
            CancelEditCommand     = new RelayCommand(_ => ExecuteCancelEdit());
            MoveRecipeUpCommand   = new RelayCommand(_ => ExecuteMoveRecipe(-1), _ => CanMoveRecipe(-1));
            MoveRecipeDownCommand = new RelayCommand(_ => ExecuteMoveRecipe(+1), _ => CanMoveRecipe(+1));
            OpenDiffCommand       = new RelayCommand(_ => ExecuteOpenDiff());

            LoadActiveRecipeOnStartup();
            RefreshRecipeList(); 
            //RefreshChangeLogs();
        
                IsDirty = false;
        }

        private void InitDatabase()
        {
            using (var db = new SqliteConnection(_dbPath))
            {
                db.Open();
                string sql = @"
                CREATE TABLE IF NOT EXISTS Recipes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT UNIQUE NOT NULL
                );

                CREATE TABLE IF NOT EXISTS RecipeDetails_Motor (
                    RecipeId INTEGER,
                    AxisNo TEXT,
                    MoveVel REAL, MoveAcc REAL, MoveDec REAL,
                    JogVel REAL, JogAcc REAL, JogDec REAL,
                    PrintVel REAL DEFAULT 0, PrintAcc REAL DEFAULT 0, PrintDec REAL DEFAULT 0,
                    FOREIGN KEY(RecipeId) REFERENCES Recipes(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS SystemSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT
                );

                CREATE TABLE IF NOT EXISTS RecipeChangeLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    LogTime TEXT NOT NULL,
                    RecipeName TEXT NOT NULL,
                    ActionType TEXT NOT NULL,  -- SAVE, CREATE, DELETE, RENAME 등
                    Details TEXT,              -- 변경 상세 정보
                    User TEXT                  -- 변경 수행자
                );

                CREATE TABLE IF NOT EXISTS RecipeDetails_Position (
                    RecipeId INTEGER,
                    PointName TEXT,
                    AxisName TEXT,
                    PosValue REAL,
                    IsUsed INTEGER DEFAULT 1,
                    FOREIGN KEY(RecipeId) REFERENCES Recipes(Id) ON DELETE CASCADE,

                    UNIQUE(RecipeId, PointName, AxisName)
                );
                INSERT OR IGNORE INTO SystemSettings (Key, Value) VALUES ('ActiveRecipe', 'Default');";

                db.Execute(sql);

                // 기존 DB에 PRINT 컬럼이 없을 경우 추가 (마이그레이션)
                foreach (var col in new[] { "PrintVel", "PrintAcc", "PrintDec" })
                {
                    try { db.Execute($"ALTER TABLE RecipeDetails_Motor ADD COLUMN {col} REAL DEFAULT 0"); }
                    catch { /* 이미 존재하면 무시 */ }
                }

                // 기존 DB에 IsUsed 컬럼이 없을 경우 추가 (마이그레이션)
                try { db.Execute("ALTER TABLE RecipeDetails_Position ADD COLUMN IsUsed INTEGER DEFAULT 1"); }
                catch { /* 이미 존재하면 무시 */ }

                // AxisNo 이름 변경 마이그레이션 (JSON에서 AxisNo가 바뀐 경우 DB 동기화)
                var axisRenames = new[] { ("GY1", "Y") };
                foreach (var (oldNo, newNo) in axisRenames)
                {
                    try { db.Execute("UPDATE RecipeDetails_Motor SET AxisNo=@newNo WHERE AxisNo=@oldNo", new { newNo, oldNo }); }
                    catch { /* 무시 */ }
                }

                // 웨이브폼 경로 컬럼 마이그레이션
                try { db.Execute("ALTER TABLE Recipes ADD COLUMN WaveformBasePath TEXT"); }
                catch { /* 이미 존재하면 무시 */ }

                // SortOrder 컬럼 마이그레이션
                try { db.Execute("ALTER TABLE Recipes ADD COLUMN SortOrder INTEGER"); }
                catch { /* 이미 존재하면 무시 */ }
                db.Execute("UPDATE Recipes SET SortOrder = Id WHERE SortOrder IS NULL");

                // PurgeTime 컬럼 마이그레이션
                try { db.Execute("ALTER TABLE Recipes ADD COLUMN PurgeTime INTEGER DEFAULT 0"); }
                catch { /* 이미 존재하면 무시 */ }

                // Swath(프린팅수) / HeadLength(head 길이 mm) 컬럼 마이그레이션
                try { db.Execute("ALTER TABLE Recipes ADD COLUMN Swath INTEGER DEFAULT 1"); }
                catch { /* 이미 존재하면 무시 */ }
                try { db.Execute("ALTER TABLE Recipes ADD COLUMN HeadLength REAL DEFAULT 0"); }
                catch { /* 이미 존재하면 무시 */ }
            }
        }

        public string? GetWaveformPath(string recipeName)
        {
            try
            {
                using var db = new SqliteConnection(_dbPath);
                db.Open();
                return db.QueryFirstOrDefault<string>(
                    "SELECT WaveformBasePath FROM Recipes WHERE Name = @recipeName",
                    new { recipeName });
            }
            catch { return null; }
        }

        public void SetWaveformPath(string recipeName, string fullBasePath)
        {
            try
            {
                using var db = new SqliteConnection(_dbPath);
                db.Open();
                db.Execute(
                    "UPDATE Recipes SET WaveformBasePath = @path WHERE Name = @recipeName",
                    new { path = fullBasePath, recipeName });
                _addLogAction?.Invoke($"[RECIPE] {recipeName} — 웨이브폼 경로 저장: {System.IO.Path.GetFileName(fullBasePath)}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _addLogAction?.Invoke($"[RECIPE] 웨이브폼 경로 저장 실패: {ex.Message}", LogLevel.Error);
            }
        }

        private void LoadActiveRecipeOnStartup()
        {
            try
            {
                using (var db = new SqliteConnection(_dbPath))
                {
                    db.Open();
                    var activeName = db.QueryFirstOrDefault<string>("SELECT Value FROM SystemSettings WHERE Key = 'ActiveRecipe'");

                    if (!string.IsNullOrEmpty(activeName))
                    {
                        ActiveRecipeName = activeName;
                        SelectedRecipeName = activeName;
                        // 시퀀스가 참조할 snapshot 즉시 캡처
                        RefreshActivePointsSnapshot();
                    }
                }
            }
            catch (Exception ex)
            {
                _addLogAction?.Invoke($"[RECIPE] 초기 로드 실패: {ex.Message}", LogLevel.Error);
            }
        }

        private void LoadAllRecipeData(string recipeName)
        {
            if (string.IsNullOrEmpty(recipeName)) return;

            try
            {
                _isLoading = true; // 🌟 1. 로딩 시작 (이제부터 발생하는 모든 변경 이벤트는 무시됨)

                using (var db = new SqliteConnection(_dbPath))
                {
                    db.Open();
                    LoadMotorData(db, recipeName);
                    PurgeTime = db.QueryFirstOrDefault<int?>(
                        "SELECT PurgeTime FROM Recipes WHERE Name=@recipeName",
                        new { recipeName }) ?? 0;
                    SwathCount = db.QueryFirstOrDefault<int?>(
                        "SELECT Swath FROM Recipes WHERE Name=@recipeName",
                        new { recipeName }) ?? 1;
                    HeadLength = db.QueryFirstOrDefault<double?>(
                        "SELECT HeadLength FROM Recipes WHERE Name=@recipeName",
                        new { recipeName }) ?? 0;
                }

                LoadTeachingPoints(recipeName);

                foreach (var axis in AxisList)
                {
                    axis.PropertyChanged -= OnAxisParameterChanged;
                    axis.PropertyChanged += OnAxisParameterChanged;
                }
            }
            finally
            {
                // 🌟 2. 모든 데이터 로드 및 이벤트 등록이 끝난 후 플래그 해제
                // Dispatcher를 이용해 UI가 다 그려진 직후에 끄는 것이 가장 확실합니다.
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isLoading = false;
                    IsDirty = false;
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }

            _addLogAction?.Invoke($"[RECIPE] {recipeName} — 데이터 로드 완료", LogLevel.Info);
        }

        // 별도의 메서드로 분리하면 관리가 더 쉽습니다.
        private void OnAxisParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // IsDirty 트리거에서 제외할 프로퍼티들
            // - 하드웨어 신호 (실시간 갱신)
            // - 시퀀스/모터제어 화면에서 변경되는 임시 상태값 (레시피 데이터 아님)
            string[] ignoredProperties = {
                    // 하드웨어 신호
                    "CurrentPos",      // UpdateMotorStatus에서 업데이트함
                    "IsServoOn",       // UpdateMotorStatus에서 업데이트함
                    "Status",          // OnPropertyChanged(nameof(Status)) 호출됨
                    "IsAlarm",
                    "IsMoving",
                    "IsInPosition",
                    "IsHomeDone",
                    "CwLimit",
                    "CcwLimit",
                    "HomeSensor",

                    // 시퀀스/모터제어 임시값 (레시피와 무관)
                    "TargetPosition",      // 시퀀스 MoveToPointAsync에서 설정
                    "IsAbsMode",           // 시퀀스/MotorControl에서 설정
                    "IsIncMode",           // IsAbsMode 토글 시 함께 발동
                    "JogUnit",             // Jog 단위 (사용자 화면 조작)
                    "IsJogContinuity",
                    "IsUnitContinuity",
                    "IsUnit10um",
                    "IsUnit100um",

                    // 축제어 화면 파생 상태 (레시피 데이터 아님 — 버튼 활성/이동 진행률용)
                    "CanMove",
                    "CanJog",
                    "IsMoveActive",
                    "DistanceToGo",
                    "MoveProgress",
                };

            // 무시 대상이 아닐 때만 IsDirty를 true로 만듭니다.
            if (!ignoredProperties.Contains(e.PropertyName))
            {
                // 로딩 중이 아닐 때만 Dirty 플래그를 켬 (이전 가이드와 결합)
                if (!_isLoading)
                {
                    IsDirty = true;
                }
            }
        }

        /// <summary>위치 티칭 화면에서 포인트를 저장한 직후 호출 — 레시피 화면의 티칭 그리드를 DB 최신값으로 동기화</summary>
        public void ReloadTeachingPoints() => LoadTeachingPoints(SelectedRecipeName);

        private void LoadTeachingPoints(string recipeName)
        {
            if (string.IsNullOrEmpty(recipeName))
            {
                TeachingPoints = new ObservableCollection<TeachingPoint>();
                return;
            }
            try
            {
                using var db = new SqliteConnection(_dbPath);
                db.Open();
                var rawData = db.Query<dynamic>(@"
                    SELECT p.* FROM RecipeDetails_Position p
                    JOIN Recipes r ON p.RecipeId = r.Id
                    WHERE r.Name = @recipeName", new { recipeName }).ToList();

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

                // 기존 포인트 행에 누락된 축을 0/사용으로 보강
                foreach (var pt in grouped)
                    foreach (var axis in AxisList)
                    {
                        if (!pt.Positions.ContainsKey(axis.Info.Name))
                            pt.Positions[axis.Info.Name] = 0.0;
                        if (!pt.AxisUsed.ContainsKey(axis.Info.Name))
                            pt.AxisUsed[axis.Info.Name] = true;
                    }

                // PointNames.All 중 DB에 없는 포인트는 기본값 행으로 자동 추가
                // → 신규 레시피 / 레거시 레시피(BLOTTING 등 누락) 가 화면에 빈칸 없이 표시됨
                foreach (var name in PointNames.All)
                {
                    if (grouped.Any(g => string.Equals(g.PointName, name, StringComparison.OrdinalIgnoreCase)))
                        continue;
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
            catch
            {
                TeachingPoints = new ObservableCollection<TeachingPoint>();
            }
        }

        private void LoadMotorData(SqliteConnection db, string recipeName)
        {
            var details = db.Query<dynamic>(@"
                SELECT d.* FROM RecipeDetails_Motor d 
                JOIN Recipes r ON d.RecipeId = r.Id 
                WHERE r.Name = @recipeName", new { recipeName }).ToList();

            if (details.Count == 0) return;

            foreach (var axis in AxisList)
            {
                var data = details.FirstOrDefault(d => d.AxisNo == axis.Info.AxisNo);
                if (data != null)
                {
                    // null 체크 및 안전한 변환
                    axis.Info.MotionConfig.Move.Velocity = Convert.ToDouble(data.MoveVel ?? 0);
                    axis.Info.MotionConfig.Move.Acceleration = Convert.ToDouble(data.MoveAcc ?? 0);
                    axis.Info.MotionConfig.Move.Deceleration = Convert.ToDouble(data.MoveDec ?? 0);
                    axis.Info.MotionConfig.Jog.Velocity = Convert.ToDouble(data.JogVel ?? 0);
                    axis.Info.MotionConfig.Jog.Acceleration = Convert.ToDouble(data.JogAcc ?? 0);
                    axis.Info.MotionConfig.Jog.Deceleration = Convert.ToDouble(data.JogDec ?? 0);
                    axis.Info.MotionConfig.Printing.Velocity = Convert.ToDouble(data.PrintVel ?? 0);
                    axis.Info.MotionConfig.Printing.Acceleration = Convert.ToDouble(data.PrintAcc ?? 0);
                    axis.Info.MotionConfig.Printing.Deceleration = Convert.ToDouble(data.PrintDec ?? 0);
                }
            }

            var temp = AxisList;
            // 강제 PropertyChanged 발생 — 일시적으로 null 후 복원 (UI 갱신용)
            AxisList = null!;
            AxisList = temp;
        }
        

        private void ExecuteCreateRecipe()
        {
            string newName = Microsoft.VisualBasic.Interaction.InputBox("새 레시피 이름을 입력하세요", "레시피 생성", "NewModel");
            if (string.IsNullOrWhiteSpace(newName) || RecipeNames.Contains(newName)) return;

            using (var db = new SqliteConnection(_dbPath))
            {
                db.Open();
                using (var trans = db.BeginTransaction())
                {
                    try
                    {
                        int nextOrder = db.QuerySingleOrDefault<int?>("SELECT MAX(SortOrder) FROM Recipes", transaction: trans) ?? 0;
                        int id = db.QuerySingle<int>("INSERT INTO Recipes (Name, SortOrder) VALUES (@newName, @order); SELECT last_insert_rowid();", new { newName, order = nextOrder + 1 }, trans);

                        foreach (var axis in AxisList)
                        {
                            db.Execute(@"INSERT INTO RecipeDetails_Motor
                                             (RecipeId, AxisNo, MoveVel, MoveAcc, MoveDec, JogVel, JogAcc, JogDec, PrintVel, PrintAcc, PrintDec)
                                         VALUES (@id, @AxisNo, @vel, @acc, @dec, @jvel, @jacc, @jdec, @pvel, @pacc, @pdec)",
                                         new
                                         {
                                             id,
                                             AxisNo = axis.Info.AxisNo,
                                             vel  = axis.Info.MotionConfig.Move.Velocity,
                                             acc  = axis.Info.MotionConfig.Move.Acceleration,
                                             dec  = axis.Info.MotionConfig.Move.Deceleration,
                                             jvel = axis.Info.MotionConfig.Jog.Velocity,
                                             jacc = axis.Info.MotionConfig.Jog.Acceleration,
                                             jdec = axis.Info.MotionConfig.Jog.Deceleration,
                                             pvel = axis.Info.MotionConfig.Printing.Velocity,
                                             pacc = axis.Info.MotionConfig.Printing.Acceleration,
                                             pdec = axis.Info.MotionConfig.Printing.Deceleration
                                         }, trans);
                        }
                        trans.Commit();
                        _addLogAction?.Invoke($"[RECIPE] {newName} — 생성 완료", LogLevel.Success);
                    }
                    catch (Exception)
                    {
                        trans.Rollback();
                        _raiseAlarm?.Invoke("RCP-CREATE-FAIL");
                    }
                }
            }
            RefreshRecipeList();
            SelectedRecipeName = newName;
        }

        private void ExecuteCancelEdit()
        {
            if (!IsDirty) return;

            var result = Dialogs.Show(
                T("Msg_RecipeCancelConfirm", SelectedRecipeName),
                T("Msg_RecipeCancelTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                LoadAllRecipeData(SelectedRecipeName);
        }

        private void ExecuteSaveRecipe()
        {
            if (string.IsNullOrEmpty(SelectedRecipeName)) return;

            // 1. 유효성 검사 (Validation) - 저장 전 수치 확인
            foreach (var axis in AxisList)
            {
                var config = axis.Info.MotionConfig;

                // 범위를 벗어난 값이 있는지 확인 (예: 0 이하의 속도 등)
                if (config.Move.Velocity < 0 || config.Move.Velocity > 2000 ||
                    config.Jog.Velocity < 0 || config.Jog.Velocity > 5000)
                {
                    string warnMsg = CurrentLanguage switch
                    {
                        "EN" => $"[Axis: {axis.Info.Name}] Invalid value.\nMove speed: 1~2000, Jog speed: 0~5000.",
                        _ => $"[{axis.Info.Name}] 설정값이 범위를 벗어났습니다.\nMove 속도: 1~2000, Jog 속도: 0~5000."
                    };
                    Dialogs.Show(warnMsg, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (config.Printing.Velocity < 0 || config.Printing.Velocity > 5000 ||
                    config.Printing.Acceleration < 0 || config.Printing.Acceleration > 50000 ||
                    config.Printing.Deceleration < 0 || config.Printing.Deceleration > 50000)
                {
                    string warnMsg = CurrentLanguage switch
                    {
                        "EN" => $"[Axis: {axis.Info.Name}] Print parameter out of range.\nVelocity: 0~5000, Acc/Dec: 0~50000.",
                        _ => $"[{axis.Info.Name}] 인쇄 파라미터가 범위를 벗어났습니다.\n속도: 0~5000, 가속도/감속도: 0~50000."
                    };
                    Dialogs.Show(warnMsg, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 2. 데이터베이스 저장 로직
            using (var db = new SqliteConnection(_dbPath))
            {
                db.Open();
                using (var trans = db.BeginTransaction())
                {
                    try
                    {
                        int recipeId = db.QuerySingle<int>("SELECT Id FROM Recipes WHERE Name = @SelectedRecipeName", new { SelectedRecipeName }, trans);

                        // DELETE + INSERT: AxisNo가 JSON 설정에서 변경되어도 항상 최신 상태로 저장
                        db.Execute("DELETE FROM RecipeDetails_Motor WHERE RecipeId=@recipeId", new { recipeId }, trans);

                        foreach (var axis in AxisList)
                        {
                            db.Execute(@"INSERT INTO RecipeDetails_Motor
                                 (RecipeId, AxisNo, MoveVel, MoveAcc, MoveDec, JogVel, JogAcc, JogDec, PrintVel, PrintAcc, PrintDec)
                                 VALUES (@recipeId, @AxisNo, @vel, @acc, @dec, @jvel, @jacc, @jdec, @pvel, @pacc, @pdec)",
                                         new
                                         {
                                             recipeId,
                                             AxisNo = axis.Info.AxisNo,
                                             vel  = axis.Info.MotionConfig.Move.Velocity,
                                             acc  = axis.Info.MotionConfig.Move.Acceleration,
                                             dec  = axis.Info.MotionConfig.Move.Deceleration,
                                             jvel = axis.Info.MotionConfig.Jog.Velocity,
                                             jacc = axis.Info.MotionConfig.Jog.Acceleration,
                                             jdec = axis.Info.MotionConfig.Jog.Deceleration,
                                             pvel = axis.Info.MotionConfig.Printing.Velocity,
                                             pacc = axis.Info.MotionConfig.Printing.Acceleration,
                                             pdec = axis.Info.MotionConfig.Printing.Deceleration
                                         }, trans);
                        }

                        // PurgeTime / Swath / HeadLength 저장
                        db.Execute("UPDATE Recipes SET PurgeTime=@purgeTime, Swath=@swath, HeadLength=@headLength WHERE Name=@name",
                            new { purgeTime = PurgeTime, swath = SwathCount, headLength = HeadLength, name = SelectedRecipeName }, trans);

                        // 티칭 포인트 저장
                        if (TeachingPoints.Count > 0)
                        {
                            db.Execute("DELETE FROM RecipeDetails_Position WHERE RecipeId=@recipeId", new { recipeId }, trans);
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
                        }

                        // ✅ 변경 이력(Audit Trail) DB 기록
                        db.Execute(@"INSERT INTO RecipeChangeLogs (LogTime, RecipeName, ActionType, Details, User)
                             VALUES (@time, @name, 'SAVE', @details, @user)",
                                     new
                                     {
                                         time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                         name = SelectedRecipeName,
                                         details = "Parameters Updated by User",
                                         user = "Engineer" // 로그인 기능 연결 시 해당 유저명 사용
                                     }, trans);

                        trans.Commit();
                        IsDirty = false;

                       // RefreshChangeLogs(); // 변경 리스트 작성

                        // 저장 대상이 현재 활성 레시피라면 스냅샷도 즉시 갱신.
                        // (편집 중인 *다른* 레시피 저장은 그대로 격리 — 활성 시퀀스 영향 없음)
                        if (SelectedRecipeName == ActiveRecipeName)
                        {
                            RefreshActivePointsSnapshot();
                            _addLogAction?.Invoke(
                                $"[RECIPE] {ActiveRecipeName} — 활성 레시피 저장, 스냅샷 갱신됨",
                                LogLevel.Info);
                        }

                        // UI 알림 및 로그
                        _addLogAction?.Invoke($"[RECIPE] {SelectedRecipeName} — 파라미터 저장 완료", LogLevel.Success);

                        string successMsg = CurrentLanguage == "KO" ? "저장되었습니다." : "Saved successfully.";
                        Dialogs.Show(successMsg);
                    }
                    catch (Exception)
                    {
                        trans.Rollback();
                        _raiseAlarm?.Invoke("RCP-SAVE-FAIL");
                    }
                }
            }
        }

        private void ExecuteApplyRecipe()
        {
            if (string.IsNullOrEmpty(SelectedRecipeName)) return;

            if (Dialogs.Show($"[{SelectedRecipeName}] 모델을 설비에 실제 적용하시겠습니까?\n(가동 중인 데이터가 변경됩니다)", "모델 적용", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                using (var db = new SqliteConnection(_dbPath))
                {
                    db.Open();
                    // 현재 활성화 모델명 업데이트
                    db.Execute("INSERT OR REPLACE INTO SystemSettings (Key, Value) VALUES ('ActiveRecipe', @name)", new { name = SelectedRecipeName });

                    ActiveRecipeName = SelectedRecipeName;

                    // 적용 순간의 포인트 데이터를 snapshot으로 고정 — 이후 편집/저장은 영향 X
                    RefreshActivePointsSnapshot();

                    // 실제 모터 주입 로직은 여기서 호출 (이미 LoadAllRecipeData가 되어있으므로, 필요 시 PLC/Driver 전송 로직 추가)
                    _addLogAction?.Invoke($"[RECIPE] {SelectedRecipeName} — 모델 적용 완료", LogLevel.Success);
                    Dialogs.Show("설비에 적용되었습니다.");
                }
            }
        }
        private void ExecuteRenameRecipe()
        {
            if (string.IsNullOrEmpty(SelectedRecipeName)) return;

            // 현재 활성 레시피인 경우 경고
            if (ActiveRecipeName == SelectedRecipeName)
            {
                var warn = Dialogs.Show(
                    T("Msg_RecipeRenameActiveWarn", SelectedRecipeName),
                    T("Msg_RecipeRenameActiveTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (warn != MessageBoxResult.Yes) return;
            }

            // 현재 이름을 기본값으로 입력창 띄우기
            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                $"[{SelectedRecipeName}]의 새 이름을 입력하세요.", "레시피 이름 변경", SelectedRecipeName);

            // 유효성 검사 (빈 값 또는 동일한 이름 제외)
            if (string.IsNullOrWhiteSpace(newName) || newName == SelectedRecipeName) return;

            // 이름 중복 체크
            if (RecipeNames.Contains(newName))
            {
                Dialogs.Show("이미 존재하는 레시피 이름입니다.", "중복 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                using (var db = new SqliteConnection(_dbPath))
                {
                    db.Open();
                    using (var trans = db.BeginTransaction())
                    {
                        // A. 레시피 테이블 이름 업데이트
                        db.Execute("UPDATE Recipes SET Name = @newName WHERE Name = @oldName",
                            new { newName, oldName = SelectedRecipeName }, trans);

                        // B. 만약 현재 실행 중인(Active) 모델의 이름을 바꾼 것이라면 시스템 설정도 업데이트
                        if (ActiveRecipeName == SelectedRecipeName)
                        {
                            db.Execute("UPDATE SystemSettings SET Value = @newName WHERE Key = 'ActiveRecipe'",
                                new { newName }, trans);
                            ActiveRecipeName = newName;
                        }

                        trans.Commit();

                        // 활성 레시피 이름이 변경되었으면 snapshot 키도 다시 캡처
                        if (ActiveRecipeName == newName)
                            RefreshActivePointsSnapshot();
                    }
                }

                _addLogAction?.Invoke($"[RECIPE] 이름 변경: {SelectedRecipeName} → {newName}", LogLevel.Info);

                // 리스트 갱신 및 선택 유지
                RefreshRecipeList();
                SelectedRecipeName = newName;
            }
            catch (Exception)
            {
                _raiseAlarm?.Invoke("RCP-RENAME-FAIL");
            }
        }
        private void RefreshRecipeList()
        {
            using (var db = new SqliteConnection(_dbPath))
            {
                db.Open();
                var list = db.Query<string>("SELECT Name FROM Recipes ORDER BY SortOrder, Id").ToList();
                RecipeNames = new ObservableCollection<string>(list);
            }
            RaiseDeleteCanExecute();
        }

        private bool CanMoveRecipe(int direction)
        {
            if (string.IsNullOrEmpty(SelectedRecipeName)) return false;
            int idx = RecipeNames.IndexOf(SelectedRecipeName);
            if (idx < 0) return false;
            return direction < 0 ? idx > 0 : idx < RecipeNames.Count - 1;
        }

        private void ExecuteMoveRecipe(int direction)
        {
            if (!CanMoveRecipe(direction)) return;

            int idx    = RecipeNames.IndexOf(SelectedRecipeName);
            int swapIdx = idx + direction;

            string nameA = RecipeNames[idx];
            string nameB = RecipeNames[swapIdx];

            using (var db = new SqliteConnection(_dbPath))
            {
                db.Open();

                // 두 레시피의 현재 SortOrder를 가져옴
                int orderA = db.QuerySingle<int>("SELECT SortOrder FROM Recipes WHERE Name=@name", new { name = nameA });
                int orderB = db.QuerySingle<int>("SELECT SortOrder FROM Recipes WHERE Name=@name", new { name = nameB });

                // SortOrder가 같으면 구분 가능하도록 재할당
                if (orderA == orderB)
                {
                    var all = db.Query<(string Name, int SortOrder)>("SELECT Name, SortOrder FROM Recipes ORDER BY SortOrder, Id").ToList();
                    for (int i = 0; i < all.Count; i++)
                        db.Execute("UPDATE Recipes SET SortOrder=@order WHERE Name=@name", new { order = (i + 1) * 10, name = all[i].Name });
                    orderA = db.QuerySingle<int>("SELECT SortOrder FROM Recipes WHERE Name=@name", new { name = nameA });
                    orderB = db.QuerySingle<int>("SELECT SortOrder FROM Recipes WHERE Name=@name", new { name = nameB });
                }

                db.Execute("UPDATE Recipes SET SortOrder=@order WHERE Name=@name", new { order = orderB, name = nameA });
                db.Execute("UPDATE Recipes SET SortOrder=@order WHERE Name=@name", new { order = orderA, name = nameB });
            }

            RefreshRecipeList();

            // RefreshRecipeList가 컬렉션을 교체하면 ListBox 선택이 해제되므로
            // 내부 필드를 초기화하고 setter를 통해 정상 경로로 재선택
            _selectedRecipeName = string.Empty;
            SelectedRecipeName  = nameA;
        }

        private void ExecuteDeleteRecipe()
        {
            if (string.IsNullOrEmpty(SelectedRecipeName)) return;

            if (SelectedRecipeName == ActiveRecipeName)
            {
                Dialogs.Show($"[{SelectedRecipeName}]은 현재 적용 중인 모델입니다.\n적용 중인 모델은 삭제할 수 없습니다.",
                    "삭제 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Dialogs.Show($"[{SelectedRecipeName}] 레시피를 삭제하시겠습니까?", "삭제", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                using (var db = new SqliteConnection(_dbPath))
                {
                    db.Open();
                    db.Execute("DELETE FROM Recipes WHERE Name = @SelectedRecipeName", new { SelectedRecipeName });
                }
                RefreshRecipeList();
                SelectedRecipeName = RecipeNames.FirstOrDefault() ?? string.Empty;
            }
        }

        private void RaiseDeleteCanExecute()
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ((RelayCommand)DeleteRecipeCommand).RaiseCanExecuteChanged();
                ((RelayCommand)MoveRecipeUpCommand).RaiseCanExecuteChanged();
                ((RelayCommand)MoveRecipeDownCommand).RaiseCanExecuteChanged();
            });
        }
        private void ExecuteOpenDiff()
        {
            // 기본 비교 — 좌측 ActiveRecipe (없으면 첫 번째), 우측 SelectedRecipe.
            // 사용자는 윈도우에서 자유롭게 변경 가능.
            string? left  = string.IsNullOrEmpty(ActiveRecipeName)
                            ? RecipeNames.FirstOrDefault() : ActiveRecipeName;
            string? right = string.IsNullOrEmpty(SelectedRecipeName) ||
                            SelectedRecipeName == left
                            ? RecipeNames.FirstOrDefault(n => n != left) : SelectedRecipeName;

            var vm  = new RecipeDiffViewModel(_dbPath, left, right);
            var win = new RecipeDiffWindow { DataContext = vm };

            // Owner 안전 탐색 — Application.Current.MainWindow 가 LoginWindow 일 수 있음
            var owner = System.Windows.Application.Current.Windows
                .OfType<MainWindow>()
                .FirstOrDefault(w => w.IsLoaded);
            if (owner != null) win.Owner = owner;
            else               win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            win.Show();
        }

        private void ExecuteCopyRecipe()
        {
            if (string.IsNullOrEmpty(SelectedRecipeName)) return;

            string title, msg, errorDuplicate, errorFail, defaultSuffix;

            switch (CurrentLanguage) // 또는 현재 ViewModel의 Language 속성
            {
                case "EN":
                    title = "Copy Recipe";
                    msg = $"Copying [{SelectedRecipeName}] model.\nPlease enter a new name.";
                    errorDuplicate = "This name already exists.";
                    errorFail = "Copy failed: ";
                    defaultSuffix = "_Copy";
                    break;
                default: // KO
                    title = "레시피 복사";
                    msg = $"[{SelectedRecipeName}] 모델을 복사합니다.\n새 이름을 입력하세요.";
                    errorDuplicate = "이미 존재하는 이름입니다.";
                    errorFail = "복사 실패: ";
                    defaultSuffix = "_복사";
                    break;
            }

            // 2. 입력창 띄우기
            string newName = Microsoft.VisualBasic.Interaction.InputBox(msg, title, SelectedRecipeName + defaultSuffix);

            // 3. 유효성 검사
            if (string.IsNullOrWhiteSpace(newName) || RecipeNames.Contains(newName))
            {
                if (RecipeNames.Contains(newName))
                    Dialogs.Show(errorDuplicate, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using (var db = new SqliteConnection(_dbPath))
                {
                    db.Open();
                    using (var trans = db.BeginTransaction())
                    {
                        // A. 새 레시피 기본 정보 추가 (SortOrder = MAX + 1)
                        int nextOrder = db.QuerySingleOrDefault<int?>("SELECT MAX(SortOrder) FROM Recipes", transaction: trans) ?? 0;
                        int newId = db.QuerySingle<int>(
                            "INSERT INTO Recipes (Name, SortOrder) VALUES (@newName, @order); SELECT last_insert_rowid();",
                            new { newName, order = nextOrder + 1 }, trans);

                        // B. Motor 상세 데이터 복사
                        db.Execute(@"
                        INSERT INTO RecipeDetails_Motor (RecipeId, AxisNo, MoveVel, MoveAcc, MoveDec, JogVel, JogAcc, JogDec, PrintVel, PrintAcc, PrintDec)
                        SELECT @newId, AxisNo, MoveVel, MoveAcc, MoveDec, JogVel, JogAcc, JogDec, PrintVel, PrintAcc, PrintDec
                        FROM RecipeDetails_Motor
                        WHERE RecipeId = (SELECT Id FROM Recipes WHERE Name = @oldName)",
                            new { newId, oldName = SelectedRecipeName }, trans);

                        // C. PurgeTime / Swath / HeadLength 복사
                        db.Execute(@"UPDATE Recipes SET
                                         PurgeTime  = (SELECT PurgeTime  FROM Recipes WHERE Name=@oldName),
                                         Swath      = (SELECT Swath      FROM Recipes WHERE Name=@oldName),
                                         HeadLength = (SELECT HeadLength FROM Recipes WHERE Name=@oldName)
                                     WHERE Id=@newId",
                            new { oldName = SelectedRecipeName, newId }, trans);

                        // D. 티칭 포인트 복사
                        db.Execute(@"
                        INSERT INTO RecipeDetails_Position (RecipeId, PointName, AxisName, PosValue, IsUsed)
                        SELECT @newId, PointName, AxisName, PosValue, IsUsed
                        FROM RecipeDetails_Position
                        WHERE RecipeId = (SELECT Id FROM Recipes WHERE Name = @oldName)",
                            new { newId, oldName = SelectedRecipeName }, trans);

                        trans.Commit();
                    }
                }

                _addLogAction?.Invoke($"[RECIPE] 복사: {SelectedRecipeName} → {newName}", LogLevel.Success);

                RefreshRecipeList();
                SelectedRecipeName = newName;
            }
            catch (Exception ex)
            {
                Dialogs.Show(errorFail + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string GetDbPath(string fileName) => PathUtils.GetConfigPath(fileName);
    }
}