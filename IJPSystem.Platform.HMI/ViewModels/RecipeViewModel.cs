using Dapper;
using IJPSystem.Platform.Application.Sequences;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Models.Motion;
using IJPSystem.Platform.Common.Utilities;
using IJPSystem.Platform.HMI;
using IJPSystem.Platform.HMI.Common;
using IJPSystem.Platform.HMI.Views;
using IJPSystem.Platform.Infrastructure.Config;
using static IJPSystem.Platform.HMI.Common.Loc;
using MachineKeys = IJPSystem.Platform.Infrastructure.Config.MachineSettingsStore.Keys;
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
        // ActiveSwath 는 하단 네비게이터 표시에 바인딩되므로 값 변경 시 알림(반응형).
        private int _activeSwath = 1;
        public int ActiveSwath { get => _activeSwath; private set => SetProperty(ref _activeSwath, value); }
        public double ActiveHeadLength { get; private set; } = 0;

        // 활성 레시피의 프린팅 방향(0=단방향, 1=양방향). 하단 상태바 표기 + 오토프린트 시퀀스 생성에 사용.
        private int _activePrintDirection = 1;   // 기본 양방향(현행 동작)
        public int ActivePrintDirection { get => _activePrintDirection; private set => SetProperty(ref _activePrintDirection, value); }
        // 표기용 — 하단 상태바 바인딩(언어전환 시 갱신되도록 CurrentLanguage 변경도 알림).
        public string ActivePrintDirectionText =>
            Common.Loc.T(_activePrintDirection == 1 ? "Opt_Bidirectional" : "Opt_Unidirectional");

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
            ActivePrintDirection = 1;
            OnPropertyChanged(nameof(ActivePrintDirectionText));
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
                ActivePrintDirection = db.QueryFirstOrDefault<int?>(
                    "SELECT PrintDirection FROM Recipes WHERE Name=@recipe", new { recipe = _activeRecipeName }) ?? 1;
                OnPropertyChanged(nameof(ActivePrintDirectionText));

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

        // 프린팅 방향 — 기타정보 화면 콤보박스(0=단방향, 1=양방향)에 SelectedIndex 로 바인딩
        private int _printDirectionIndex = 1;   // 0=단방향(Unidirectional), 1=양방향(Bidirectional) — 기본 양방향
        public int PrintDirectionIndex
        {
            get => _printDirectionIndex;
            set
            {
                int clamped = Math.Max(0, Math.Min(1, value));
                if (SetProperty(ref _printDirectionIndex, clamped) && !_isLoading)
                    IsDirty = true;
            }
        }

        // ── 노즐 정보 (헤드 사양) ─────────────────────────────────────────────
        // <b>레시피에 딸린다</b>(2026-08-13 변경). 장비 하나로 여러 헤드를 갈아 쓰므로,
        // 헤드는 장비가 아니라 "이 제품을 이 헤드로 찍는다" 는 선택이다.
        //
        // 예전에는 장비 설정(MachineSettings)에만 뒀다. 그러면 헤드를 바꿀 때마다 손으로 고쳐야
        // 했고, 지난 레시피가 어떤 헤드로 찍힌 것인지 기록이 남지 않았다.
        //
        // 다만 읽는 쪽 중 SpitService 는 Infrastructure 계층이라 레시피 DB 를 못 본다. 그래서
        // 활성 레시피의 헤드를 MachineSettings 로 비춰 준다(ApplyHeadSpecToMachine) —
        // 즉 MachineSettings 는 이제 "장비 고정값" 이 아니라 <b>지금 물린 헤드</b>를 뜻한다.

        private static MachineSettingsStore Machine =>
            IJPSystem.Platform.Infrastructure.Config.MachineSettings.Current;

        private string _headName = "";
        /// <summary>
        /// 헤드 이름(예: <c>EPSON-S3200</c>). 표시·기록용 — 계산에는 쓰지 않는다.
        ///
        /// <para>영문 대문자로 정규화한다. 화면 입력은 <c>CharacterCasing</c> 이 이미 대문자로
        /// 바꾸지만, <b>붙여넣기와 옛 레시피 값</b>은 그 길로 오지 않는다 — 여기서 한 번 더 맞춰야
        /// 같은 헤드가 대소문자만 다른 두 이름으로 갈리지 않는다.</para>
        /// </summary>
        public string HeadName
        {
            get => _headName;
            set
            {
                string v = (value ?? "").Trim().ToUpperInvariant();
                if (SetProperty(ref _headName, v) && !_isLoading) IsDirty = true;
            }
        }

        private double _headWidthMm;
        /// <summary>
        /// 헤드 폭[mm] — 스캔 방향 치수. 0=미입력.
        /// <para>길이(<see cref="HeadLength"/>)가 노즐이 늘어선 방향이고, 폭은 그 직각이다.
        /// S3200 은 칩이 스캔 방향으로 15.24mm 엇갈려 있어 폭이 그만큼 필요하다.</para>
        /// </summary>
        public double HeadWidthMm
        {
            get => _headWidthMm;
            set { if (SetProperty(ref _headWidthMm, Math.Max(0, value)) && !_isLoading) IsDirty = true; }
        }

        private double _nozzlePitchUm;
        /// <summary>같은 열 안에서 인접 노즐 간 거리[µm]. 0=미입력.</summary>
        public double NozzlePitchUm
        {
            get => _nozzlePitchUm;
            set { if (SetProperty(ref _nozzlePitchUm, Math.Max(0, value)) && !_isLoading) IsDirty = true; }
        }

        public int[] NozzleRowOptions { get; } = { 1, 2, 3, 4 };

        private int _nozzleRows;
        /// <summary>노즐 열 수(1~4). 0=미입력.</summary>
        public int NozzleRows
        {
            get => _nozzleRows;
            set
            {
                if (SetProperty(ref _nozzleRows, Math.Clamp(value, 0, 4)) && !_isLoading) IsDirty = true;
                OnPropertyChanged(nameof(NozzleCountHint));
            }
        }

        private double _nozzleRowPitchUm;
        /// <summary>열과 열 사이 거리[µm]. 1열 헤드면 의미 없다.</summary>
        public double NozzleRowPitchUm
        {
            get => _nozzleRowPitchUm;
            set { if (SetProperty(ref _nozzleRowPitchUm, Math.Max(0, value)) && !_isLoading) IsDirty = true; }
        }

        // 노즐 구경은 화면에서 뺐다(2026-08-13) — 입력만 받고 어디에서도 쓰지 않았고,
        // 액적 크기는 드랍와처가 실제로 재는 값이라 여기 적힌 숫자가 근거가 되지 못했다.

        private int _nozzlesPerRow;
        /// <summary>
        /// <b>칩 하나의 한 열</b> 노즐 수. S3200 = 400. 0=미입력.
        /// <para>칩 수 × 열 수 × 이 값 = 총 노즐 수여야 한다 — 어긋나면 화면에 계산값을 같이 보여
        /// 준다(<see cref="NozzleCountHint"/>). 조용히 두면 패턴이 헤드보다 좁거나 넓게 만들어진다.</para>
        /// </summary>
        public int NozzlesPerRow
        {
            get => _nozzlesPerRow;
            set
            {
                if (SetProperty(ref _nozzlesPerRow, Math.Max(0, value)) && !_isLoading) IsDirty = true;
                OnPropertyChanged(nameof(NozzleCountHint));
            }
        }

        public int[] ChipCountOptions { get; } = { 1, 2, 3, 4 };

        private int _chipCount = 1;
        /// <summary>
        /// 헤드 안의 칩 수(1~4). S3200 = 4, S800 = 1.
        /// <para>1 이면 칩 없는 헤드로 다뤄져 지금까지와 똑같이 동작한다.
        /// 2 이상이면 칩이 겹쳐 붙은 배치(<c>ChipHeadLayout</c>)가 쓰인다.</para>
        /// </summary>
        public int ChipCount
        {
            get => _chipCount;
            set
            {
                if (SetProperty(ref _chipCount, Math.Clamp(value, 1, 4)) && !_isLoading) IsDirty = true;
                OnPropertyChanged(nameof(NozzleCountHint));
            }
        }

        /// <summary>
        /// 칩 수 · 열 수 · 열당 노즐 수로 계산한 총 노즐 수. 입력한 총 노즐 수와 다르면 그 사실을 말한다.
        ///
        /// <para>세 값을 각각 받으면 서로 안 맞아도 화면은 아무 말이 없다 — S3200 은
        /// 4칩 × 2열 × 400 = 3,200 인데, 열 수에 칩 수를 적어 넣기 쉽다(4열로 두면 6,400 이 된다).
        /// 계산값을 옆에 띄워 두면 그 자리에서 드러난다.</para>
        /// </summary>
        public string NozzleCountHint
        {
            get
            {
                int computed = ChipCount * NozzleRows * NozzlesPerRow;
                if (computed <= 0) return "";
                return computed == NozzleCount
                    ? $"= {ChipCount} × {NozzleRows} × {NozzlesPerRow}"
                    : $"⚠ {ChipCount} × {NozzleRows} × {NozzlesPerRow} = {computed}";
            }
        }

        /// <summary>
        /// 웨이브폼(액적 크기) 목록. 엡손 계열 헤드의 계조 단계 이름 그대로다.
        /// S3200 사양의 "Grey scale: Up to 4" 가 이 넷을 말한다.
        ///
        /// <para>액적 부피: <c>Vibration</c> = 토출 안 함(메니스커스만 흔들어 노즐 마름 방지) /
        /// <c>Small</c> = 3.2pL / <c>Middle</c> = 5.1pL / <c>Large</c> = 10.1pL.
        /// 화면 설명글이 이 순서를 그대로 따르므로, 목록 순서를 바꾸면 설명도 같이 고칠 것.</para>
        /// </summary>
        public string[] WaveformOptions { get; } = { "Vibration", "Small", "Middle", "Large" };

        private string _waveform = "Middle";
        /// <summary>선택된 웨이브폼 단계. 헤드에 실린 웨이브폼의 어느 계조로 쏠지.</summary>
        public string Waveform
        {
            get => _waveform;
            set
            {
                // 빈 값이 들어오면 콤보가 선택 없음으로 보인다 — 목록에 있는 값만 받는다.
                string v = string.IsNullOrWhiteSpace(value) ? _waveform : value.Trim();
                if (Array.IndexOf(WaveformOptions, v) < 0) return;
                if (SetProperty(ref _waveform, v) && !_isLoading) IsDirty = true;
            }
        }

        private int _nozzleCount;
        /// <summary>헤드 전체 노즐 수.</summary>
        public int NozzleCount
        {
            get => _nozzleCount;
            set
            {
                if (SetProperty(ref _nozzleCount, Math.Max(0, value)) && !_isLoading) IsDirty = true;
                OnPropertyChanged(nameof(NozzleCountHint));
            }
        }

        /// <summary>
        /// 레시피에서 읽은 헤드 사양을 화면에 채운다. 레시피에 값이 없으면(옛 레시피)
        /// <b>장비 설정에 남아 있던 값</b>으로 채운다 — 예전 방식으로 저장된 헤드가 그것이라,
        /// 빈 화면을 보여 주고 다시 입력하게 하는 것보다 낫다.
        /// </summary>
        private void LoadNozzleSpec(dynamic? row)
        {
            bool prev = _isLoading;
            _isLoading = true;      // 읽기만으로 IsDirty 가 켜지면 안 된다
            try
            {
                bool machine = IJPSystem.Platform.Infrastructure.Config.MachineSettings.IsReady;

                double D(object? v, string key) =>
                    v != null && Convert.ToDouble(v) > 0 ? Convert.ToDouble(v)
                    : machine ? Machine.GetDouble(key) : 0;

                int I(object? v, string key, int fallback = 0) =>
                    v != null && Convert.ToInt32(v) > 0 ? Convert.ToInt32(v)
                    : machine ? Machine.GetInt(key, fallback) : fallback;

                string S(object? v, string key, string fallback) =>
                    v is string s && s.Length > 0 ? s
                    : machine ? Machine.GetString(key, fallback) : fallback;

                HeadName    = row?.HeadName as string ?? "";
                HeadWidthMm = row?.HeadWidthMm != null ? Convert.ToDouble(row.HeadWidthMm) : 0;

                NozzlePitchUm    = D(row?.NozzlePitchUm,     MachineKeys.NozzlePitchUm);
                NozzleRows       = I(row?.NozzleRows,        MachineKeys.NozzleRows);
                NozzleRowPitchUm = D(row?.NozzleRowPitchUm,  MachineKeys.NozzleRowPitchUm);
                NozzleCount      = I(row?.NozzleCount,       MachineKeys.NozzleCount);
                ChipCount        = I(row?.HeadChipCount,     MachineKeys.HeadChipCount, 1);
                NozzlesPerRow    = I(row?.HeadNozzlesPerRow, MachineKeys.HeadNozzlesPerRow);
                Waveform         = S(row?.HeadWaveform,      MachineKeys.HeadWaveform, "Middle");
            }
            finally { _isLoading = prev; }
        }

        /// <summary>
        /// 화면의 헤드 사양을 <b>장비 설정에 비춘다</b> — 레시피 DB 를 못 보는 쪽(SpitService 등)을
        /// 위한 것이다. 여기서 MachineSettings 는 "장비 고정값"이 아니라 <b>지금 물린 헤드</b>다.
        ///
        /// <para>비추고 나면 캐시를 반드시 버려야 한다. <see cref="HeadSpec"/> 은 값을 들고 있고
        /// <c>SpitService</c> 는 노즐 수로 만든 어댑터를 들고 있어서, 버리지 않으면 헤드를 바꿔도
        /// 노즐 선택은 새 헤드로 보이는데 <b>토출은 옛 헤드로 나간다</b>.</para>
        /// </summary>
        private void ApplyHeadSpecToMachine()
        {
            if (!IJPSystem.Platform.Infrastructure.Config.MachineSettings.IsReady) return;

            Machine.Set(MachineKeys.NozzlePitchUm,     NozzlePitchUm);
            Machine.Set(MachineKeys.NozzleRows,        NozzleRows);
            Machine.Set(MachineKeys.NozzleRowPitchUm,  NozzleRowPitchUm);
            Machine.Set(MachineKeys.NozzleCount,       NozzleCount);
            Machine.Set(MachineKeys.HeadChipCount,     ChipCount);
            Machine.Set(MachineKeys.HeadNozzlesPerRow, NozzlesPerRow);
            Machine.Set(MachineKeys.HeadWaveform,      Waveform);

            IJPSystem.Platform.Infrastructure.Config.HeadSpec.Reload();

            // 토출 중에 어댑터를 갈면 헤드가 도는 채로 참조가 끊긴다 — 멈춘 뒤에만 바꾼다.
            // (돌고 있으면 지금 헤드 그대로 쓰는 게 맞다. 다음 기동에서 새 사양이 잡힌다)
            if (!Infrastructure.Devices.DropWatcher.SpitService.IsSpitting)
                Infrastructure.Devices.DropWatcher.SpitService.Reset();
            else _addLogAction?.Invoke(
                "[RECIPE] 토출 중이라 헤드 사양 반영을 미룹니다 — 토출을 멈춘 뒤 다시 적용하세요.",
                LogLevel.Warning);
        }

        // ── 글라스 정보 ───────────────────────────────────────────────────────
        // 인쇄 영역·스캔 횟수 산출의 입력값. 원점 오프셋은 척 기준점과 글라스 좌상단의 차이다.

        private double _glassWidthMm;
        public double GlassWidthMm
        {
            get => _glassWidthMm;
            set { if (SetProperty(ref _glassWidthMm, Math.Max(0, value)) && !_isLoading) IsDirty = true; }
        }

        private double _glassHeightMm;
        public double GlassHeightMm
        {
            get => _glassHeightMm;
            set { if (SetProperty(ref _glassHeightMm, Math.Max(0, value)) && !_isLoading) IsDirty = true; }
        }

        private double _glassThicknessMm;
        public double GlassThicknessMm
        {
            get => _glassThicknessMm;
            set { if (SetProperty(ref _glassThicknessMm, Math.Max(0, value)) && !_isLoading) IsDirty = true; }
        }

        private double _glassOriginXMm;
        /// <summary>척 기준점 → 글라스 기준점 오프셋 X[mm]. 음수 허용(기준점보다 앞쪽).</summary>
        public double GlassOriginXMm
        {
            get => _glassOriginXMm;
            set { if (SetProperty(ref _glassOriginXMm, value) && !_isLoading) IsDirty = true; }
        }

        private double _glassOriginYMm;
        public double GlassOriginYMm
        {
            get => _glassOriginYMm;
            set { if (SetProperty(ref _glassOriginYMm, value) && !_isLoading) IsDirty = true; }
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
            // 헤드 사양은 레시피에 딸리므로 여기서 읽지 않는다 — 레시피를 불러올 때 같이 온다.
            // (레시피가 아직 없는 첫 실행에서는 화면이 비어 있고, 장비 설정에 남은 값으로 채워진다)

            CreateRecipeCommand   = new RelayCommand(_ => ExecuteCreateRecipe());
            DeleteRecipeCommand   = new RelayCommand(_ => ExecuteDeleteRecipe(), _ => !string.IsNullOrEmpty(SelectedRecipeName) && SelectedRecipeName != ActiveRecipeName);
            SaveRecipeCommand     = new RelayCommand(_ => ExecuteSaveRecipe());
            // 선택 모델이 이미 가동(활성) 모델이면 지정할 필요가 없으므로 비활성화.
            ApplyRecipeCommand    = new RelayCommand(_ => ExecuteApplyRecipe(),
                                        _ => !string.IsNullOrEmpty(SelectedRecipeName) && SelectedRecipeName != ActiveRecipeName);
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

                // PrintDirection(프린팅 방향: 0=단방향, 1=양방향) 컬럼 마이그레이션.
                // 기본 1(양방향) — 이 컬럼이 없던 기존 레시피의 현행 동작(양방향)을 그대로 유지.
                try { db.Execute("ALTER TABLE Recipes ADD COLUMN PrintDirection INTEGER DEFAULT 1"); }
                catch { /* 이미 존재하면 무시 */ }

                // 글라스 정보 컬럼 마이그레이션(2026-08-07). 기본 0 = 미입력.
                //
                // 노즐 헤드 사양도 여기 둔다(2026-08-13 변경). 예전에는 장비 설정(MachineSettings)에만
                // 뒀는데, 장비 하나로 <b>여러 헤드를 갈아 쓰는</b> 운용이라 그 전제가 틀렸다 —
                // 헤드를 바꿀 때마다 손으로 다섯 칸을 고쳐야 했고, 어느 레시피가 어떤 헤드로 찍은
                // 것인지 기록이 남지 않았다.
                //
                // ※ 값의 진실은 레시피다. 다만 SpitService 는 Infrastructure 계층이라 레시피 DB 를
                //   볼 수 없으므로, 활성 레시피의 헤드를 MachineSettings 로 비춰 준다
                //   (<see cref="ApplyHeadSpecToMachine"/>). 그래서 읽는 쪽 코드는 그대로다.
                foreach (string col in new[]
                {
                    "GlassWidthMm REAL DEFAULT 0",
                    "GlassHeightMm REAL DEFAULT 0",
                    "GlassThicknessMm REAL DEFAULT 0",
                    "GlassOriginXMm REAL DEFAULT 0",     // 글라스 기준점 오프셋
                    "GlassOriginYMm REAL DEFAULT 0",

                    "HeadName TEXT",                     // 헤드 이름 — 어느 헤드로 찍은 레시피인지
                    "HeadWidthMm REAL DEFAULT 0",        // 헤드 폭(스캔 방향). 길이는 HeadLength
                    "NozzlePitchUm REAL DEFAULT 0",
                    "NozzleRows INTEGER DEFAULT 0",
                    "NozzleRowPitchUm REAL DEFAULT 0",
                    "HeadChipCount INTEGER DEFAULT 1",
                    "HeadNozzlesPerRow INTEGER DEFAULT 0",
                    "HeadWaveform TEXT",
                    "NozzleCount INTEGER DEFAULT 0",
                })
                {
                    try { db.Execute($"ALTER TABLE Recipes ADD COLUMN {col}"); }
                    catch { /* 이미 존재하면 무시 */ }
                }

                // 복사 목록(RecipeColumns)에 있는 열이 실제 테이블에 다 있는지 확인한다.
                // 없으면 복사 SQL 이 통째로 실패하므로, 조용히 지나가지 않고 로그로 알린다
                // — 열을 만들고 위 목록에 넣는 것을 빠뜨렸다는 뜻이다.
                try
                {
                    var actual = db.Query<string>("SELECT name FROM pragma_table_info('Recipes')")
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = RecipeColumns.Copyable.Where(c => !actual.Contains(c)).ToList();
                    if (missing.Count > 0)
                        _addLogAction?.Invoke(
                            $"[RECIPE] Recipes 테이블에 없는 복사 대상 열: {string.Join(", ", missing)} — " +
                            "마이그레이션 목록에 빠졌습니다. 레시피 복사가 실패합니다.",
                            LogLevel.Error);
                }
                catch { /* pragma 미지원 등 — 진단이라 실패해도 앱은 계속 돈다 */ }
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
                    PrintDirectionIndex = db.QueryFirstOrDefault<int?>(
                        "SELECT PrintDirection FROM Recipes WHERE Name=@recipeName",
                        new { recipeName }) ?? 1;

                    // 글라스·노즐 정보 — 한 번의 조회로 가져온다(컬럼마다 왕복하면 열 번이 된다).
                    var spec = db.QueryFirstOrDefault(
                        @"SELECT GlassWidthMm, GlassHeightMm, GlassThicknessMm, GlassOriginXMm, GlassOriginYMm,
                                 HeadName, HeadWidthMm, NozzlePitchUm, NozzleRows, NozzleRowPitchUm,
                                 HeadChipCount, HeadNozzlesPerRow, HeadWaveform, NozzleCount
                          FROM Recipes WHERE Name=@recipeName", new { recipeName });
                    if (spec != null)
                    {
                        GlassWidthMm      = Convert.ToDouble(spec.GlassWidthMm     ?? 0d);
                        GlassHeightMm     = Convert.ToDouble(spec.GlassHeightMm    ?? 0d);
                        GlassThicknessMm  = Convert.ToDouble(spec.GlassThicknessMm ?? 0d);
                        GlassOriginXMm    = Convert.ToDouble(spec.GlassOriginXMm   ?? 0d);
                        GlassOriginYMm    = Convert.ToDouble(spec.GlassOriginYMm   ?? 0d);
                    }

                    // 헤드 사양도 레시피에 딸린다 — 이 레시피가 어떤 헤드로 찍는지.
                    LoadNozzleSpec(spec);
                }

                // 활성 레시피를 불러왔을 때만 장비에 비춘다. 편집하려고 다른 레시피를 열어 본 것만으로
                // 토출·노즐 선택이 그 헤드로 바뀌면, 보기만 했는데 장비가 따라 움직이는 셈이 된다.
                if (recipeName == ActiveRecipeName) ApplyHeadSpecToMachine();

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
                    "UpperLimit",
                    "LowerLimit",
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

            // 1-2. 티칭 좌표 범위 검사 (MotorConfig.json 의 축별 TeachLimit — 예: T축 0~30°).
            // 이동은 막지 않는다. 값이 공정 좌표로 굳어지는 이 지점에서만 막는다.
            var outOfRange = TeachLimitCheck.Find(
                TeachingPoints.Select(p => (p.PointName,
                                            (IReadOnlyDictionary<string, double>)p.Positions,
                                            (IReadOnlyDictionary<string, bool>)p.AxisUsed)),
                AxisList.Select(a => a.Info));
            if (outOfRange.Count > 0)
            {
                Dialogs.Show(TeachLimitCheck.Message(outOfRange, CurrentLanguage == "EN"),
                             "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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

                        // PurgeTime / Swath / HeadLength / PrintDirection + 노즐·글라스 정보 저장
                        db.Execute(@"UPDATE Recipes SET
                                         PurgeTime=@purgeTime, Swath=@swath, HeadLength=@headLength, PrintDirection=@printDir,
                                         GlassWidthMm=@gW, GlassHeightMm=@gH, GlassThicknessMm=@gT,
                                         GlassOriginXMm=@gX, GlassOriginYMm=@gY,
                                         HeadName=@headName, HeadWidthMm=@headWidth, NozzlePitchUm=@nPitch, NozzleRows=@nRows,
                                         NozzleRowPitchUm=@nRowPitch, HeadChipCount=@chips,
                                         HeadNozzlesPerRow=@perRow, HeadWaveform=@wave, NozzleCount=@nCount
                                     WHERE Name=@name",
                            new
                            {
                                purgeTime = PurgeTime, swath = SwathCount, headLength = HeadLength,
                                printDir = PrintDirectionIndex,
                                gW = GlassWidthMm, gH = GlassHeightMm, gT = GlassThicknessMm,
                                gX = GlassOriginXMm, gY = GlassOriginYMm,
                                headName = HeadName, headWidth = HeadWidthMm, nPitch = NozzlePitchUm, nRows = NozzleRows,
                                nRowPitch = NozzleRowPitchUm, chips = ChipCount,
                                perRow = NozzlesPerRow, wave = Waveform, nCount = NozzleCount,
                                name = SelectedRecipeName
                            }, trans);

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

                            // 활성 레시피의 헤드를 고쳤으면 장비에도 비춘다 — 안 하면 화면은 새 헤드,
                            // 토출은 옛 헤드가 된다(HeadSpec·SpitService 가 값을 캐시한다).
                            ApplyHeadSpecToMachine();

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

                    // 이 레시피의 헤드를 장비에 물린다. 레시피마다 헤드가 다를 수 있으므로
                    // 적용이 곧 헤드 교체다 — 노즐 선택·패턴 생성·토출이 모두 이 값을 따른다.
                    ApplyHeadSpecToMachine();

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
                ((RelayCommand)ApplyRecipeCommand).RaiseCanExecuteChanged();
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

                        // C. 인쇄 조건 + 헤드·글라스 사양 복사.
                        //
                        // ★ 컬럼을 새로 만들면 <b>여기에도 반드시 더할 것.</b> 빠뜨리면 복사본이
                        //   그 항목만 비어 있는데, 화면은 멀쩡히 열리고 저장도 되기 때문에
                        //   한참 뒤 "복사했더니 칩 수·헤드명이 안 따라왔다" 로 발견된다(2026-08-13).
                        //   원본 한 줄을 통째로 읽어 넣는 편이 안전하지만, Id·Name·SortOrder 같은
                        //   새 레시피 고유값을 덮어쓰면 안 되므로 열을 적어 준다.
                        db.Execute(
                            $"UPDATE Recipes SET\n{RecipeColumns.BuildCopySetClause()}\nWHERE Id=@newId",
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