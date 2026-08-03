using IJPSystem.Platform.Common.Enums;
using IJPSystem.Platform.Domain.Common;
using IJPSystem.Platform.Domain.Models.Alarm;
using IJPSystem.Platform.HMI.Common;
using static IJPSystem.Platform.HMI.Common.Loc;
using IJPSystem.Platform.Infrastructure.Repositories;
using IJPSystem.Platform.HMI.Views;
using IJPSystem.Platform.Common.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace IJPSystem.Platform.HMI.ViewModels
{
    public class AlarmViewModel : ViewModelBase
    {
        private readonly AlarmRepository _alarmRepo = new AlarmRepository();
        // SystemLog 적재용 콜백 — MainViewModel.AddLog 주입. null 이면 SystemLog 기록 생략.
        private readonly Action<string, LogLevel>? _addLog;

        /// <summary>
        /// 알람 발생 순간의 장비 상태 한 줄(축 위치·서보·진행 스텝 등)을 만들어 주는 콜백.
        /// 알람 코드만 남기면 "그때 무슨 상황이었나"를 사후에 복원할 수 없다.
        /// MainViewModel 이 주입한다(모션/IO 접근은 여기 계층에 없으므로).
        /// </summary>
        public Func<string>? SnapshotProvider { get; set; }
        public ObservableCollection<AlarmModel> AlarmHistory { get; set; } = new ObservableCollection<AlarmModel>();
        public ICollectionView FilteredAlarmHistory { get; }

        public ICommand ExportCsvCommand { get; }
        public ICommand ClearAlarmCommand { get; }
        public ICommand DeleteHistoryCommand { get; }
        public ICommand SearchCommand { get; }
        public ICommand ResetFilterCommand { get; }
        public ICommand AckSingleAlarmCommand { get; }

        private DateTime? _filterStartDate;
        public DateTime? FilterStartDate
        {
            get => _filterStartDate;
            set => SetProperty(ref _filterStartDate, value);
        }

        private DateTime? _filterEndDate;
        public DateTime? FilterEndDate
        {
            get => _filterEndDate;
            set => SetProperty(ref _filterEndDate, value);
        }

        private string _filterKeyword = string.Empty;
        public string FilterKeyword
        {
            get => _filterKeyword;
            set => SetProperty(ref _filterKeyword, value);
        }

        private bool _hasActiveAlarm;
        public bool HasActiveAlarm
        {
            get => _hasActiveAlarm;
            set { SetProperty(ref _hasActiveAlarm, value); }
        }

        public int ActiveAlarmCount => AlarmHistory.Count(a => !a.IsCleared);

        public AlarmViewModel(Action<string, LogLevel>? addLog = null)
        {
            _addLog = addLog;
            FilteredAlarmHistory = CollectionViewSource.GetDefaultView(AlarmHistory);
            FilteredAlarmHistory.Filter = ApplyDateFilter;

            ExportCsvCommand     = new RelayCommand(_ => ExecuteExportCsv());
            ClearAlarmCommand    = new RelayCommand(_ => ClearAlarmAndSetStandby());
            DeleteHistoryCommand = new RelayCommand(_ => ExecuteDeleteHistory());
            SearchCommand        = new RelayCommand(_ => ExecuteSearch());
            ResetFilterCommand   = new RelayCommand(_ => ExecuteResetFilter());
            AckSingleAlarmCommand = new RelayCommand(
                param => { if (param is AlarmModel alarm) ClearSingleAlarm(alarm); },
                param => param is AlarmModel alarm && !alarm.IsCleared);

            AlarmHistory.CollectionChanged += (_, __) => OnPropertyChanged(nameof(ActiveAlarmCount));

            LoadHistoryFromDatabase();
        }

        private void ExecuteSearch()
        {
            if (FilterStartDate.HasValue && FilterEndDate.HasValue &&
                FilterEndDate.Value.Date < FilterStartDate.Value.Date)
            {
                Dialogs.Show("종료 날짜가 시작 날짜보다 이전입니다.\n날짜 범위를 다시 확인하세요.",
                                "날짜 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            FilteredAlarmHistory.Refresh();
        }

        private bool ApplyDateFilter(object item)
        {
            if (item is not AlarmModel alarm) return true;

            if (FilterStartDate.HasValue && alarm.OccurredTime.Date < FilterStartDate.Value.Date)
                return false;
            if (FilterEndDate.HasValue && alarm.OccurredTime.Date > FilterEndDate.Value.Date)
                return false;

            if (!string.IsNullOrWhiteSpace(FilterKeyword))
            {
                string kw = FilterKeyword.Trim();
                bool matched = alarm.AlarmCode?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true
                            || alarm.AlarmName?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true
                            || alarm.Severity?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true;
                if (!matched) return false;
            }

            return true;
        }

        private void ExecuteResetFilter()
        {
            FilterStartDate  = null;
            FilterEndDate    = null;
            FilterKeyword    = string.Empty;
            FilteredAlarmHistory.Refresh();
        }
        // 코드별 자동 해제 타이머 (AutoResetDelayMs 적용 알람)
        private readonly Dictionary<string, DispatcherTimer> _autoResetTimers = new();

        public void RaiseAlarm(string code) => RaiseAlarm(code, Array.Empty<object>());

        // params 오버로드 — 마스터 메시지에 {0}, {1} 등 placeholder가 있으면 args로 치환.
        // 예) RaiseAlarm("MOT-AXIS-ALM", "X")  →  "축 X 하드웨어 알람"
        public void RaiseAlarm(string code, params object[] args)
        {
            // 1. 마스터 정보 조회 + 기본값 보정 (placeholder 치환 포함)
            var info = _alarmRepo.GetAlarmInfo(code);

            string title    = SafeFormat(info?.AlarmName_KR   ?? "알 수 없는 오류", args);
            string guide    = SafeFormat(info?.ActionGuide_KR ?? "코드를 확인하세요.", args);
            string severity = string.IsNullOrWhiteSpace(info?.Severity) ? "Error" : info!.Severity!;
            bool   ack      = info?.AckRequired ?? true;
            int?   autoMs   = info?.AutoResetDelayMs;

            // SystemLog 적재용 LogLevel — 알람 마스터의 Severity 를 그대로 매핑.
            // 신규/반복 두 분기 모두 같은 레벨로 기록해야 ERROR(Fatal+Error) 카테고리가
            // 반복 발생까지 일관되게 잡을 수 있음.
            LogLevel mappedLevel = severity switch
            {
                "Fatal" or "Critical" => LogLevel.Fatal,
                "Error"               => LogLevel.Error,
                "Warning"             => LogLevel.Warning,
                _                     => LogLevel.Error,
            };

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // 2. 동일 코드의 미해제 알람이 있으면 RepeatCount만 증가 (DB 신규 행 X)
                //    팝업은 AckRequired=true 일 때 다시 표시 — 이 분기는 모달이 닫힌 뒤
                //    재호출되거나, 이전 세션의 미해제 잔여 알람을 DB에서 로드한 경우에만 진입함
                //    (모달이 떠 있는 동안엔 UI 스레드가 차단되어 RaiseAlarm 자체가 호출되지 않음)
                var existing = AlarmHistory.FirstOrDefault(a => !a.IsCleared && a.AlarmCode == code);
                if (existing != null)
                {
                    existing.RepeatCount++;
                    existing.OccurredTime = DateTime.Now;
                    // HasActiveAlarm 방어적 set — 이전 세션 잔여 알람을 LoadHistory 에서
                    // 못 잡았던 케이스를 여기서 보정 (이중 안전장치)
                    HasActiveAlarm = true;
                    RestartAutoResetTimer(existing, autoMs);
                    _addLog?.Invoke(
                        $"[ALARM] {code} — 반복 발생 (×{existing.RepeatCount}) {title}",
                        mappedLevel);
                    if (ack)
                        ShowAlarmPopup(code, title, guide, severity);
                    return;
                }

                // 3. 신규 발생 — DB 적재 후 UI 추가
                long dbId = _alarmRepo.LogAlarmStart(code, title, severity);
                var newAlarm = new AlarmModel
                {
                    DbId         = dbId,
                    AlarmCode    = code,
                    AlarmName    = title,
                    AlarmGuide   = guide,
                    Severity     = severity,
                    OccurredTime = DateTime.Now,
                    IsCleared    = false,
                    RepeatCount  = 1,
                };
                AlarmHistory.Insert(0, newAlarm);
                HasActiveAlarm = true;

                _addLog?.Invoke($"[ALARM] {code} — 발생: {title}", mappedLevel);

                // 발생 시점 스냅샷 — 스냅샷 수집이 실패해도 알람 처리는 계속되어야 한다.
                try
                {
                    string snap = SnapshotProvider?.Invoke() ?? "";
                    if (!string.IsNullOrEmpty(snap))
                        _addLog?.Invoke($"[ALARM] {code} — 상태: {snap}", mappedLevel);
                }
                catch (Exception ex)
                {
                    _addLog?.Invoke($"[ALARM] {code} — 상태 수집 실패: {ex.GetType().Name}", LogLevel.Warning);
                }

                // 4. 자동 해제 예약 (AutoResetDelayMs 보유 시)
                RestartAutoResetTimer(newAlarm, autoMs);

                // 5. 팝업 — AckRequired=true일 때만 모달 표시. false는 히스토리만 적재.
                if (ack)
                    ShowAlarmPopup(code, title, guide, severity);
            });
        }

        // 마스터 메시지에 placeholder가 없으면 그대로 반환, FormatException은 안전하게 무시.
        private static string SafeFormat(string template, object[] args)
        {
            if (args == null || args.Length == 0) return template;
            try { return string.Format(template, args); }
            catch (FormatException) { return template; }
        }

        // 자동 해제 타이머 시작/재시작. autoMs가 null이면 아무 것도 안 함.
        private void RestartAutoResetTimer(AlarmModel alarm, int? autoMs)
        {
            if (alarm == null || string.IsNullOrEmpty(alarm.AlarmCode)) return;

            if (_autoResetTimers.TryGetValue(alarm.AlarmCode, out var prev))
            {
                prev.Stop();
                _autoResetTimers.Remove(alarm.AlarmCode);
            }

            if (!autoMs.HasValue || autoMs.Value <= 0) return;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoMs.Value) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                _autoResetTimers.Remove(alarm.AlarmCode);
                if (!alarm.IsCleared) ClearSingleAlarm(alarm);
            };
            _autoResetTimers[alarm.AlarmCode] = timer;
            timer.Start();
        }
        private void ShowAlarmPopup(string code, string alarmName, string actionGuide, string severity = "Fatal")
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new AlarmDialog { Severity = severity };

                dialog.DataContext = new
                {
                    AlarmCode = code,        // XAML의 {Binding AlarmCode}와 매칭
                    AlarmMessage = alarmName, // XAML의 {Binding AlarmMessage}와 매칭
                    ActionGuide = actionGuide // XAML의 {Binding ActionGuide}와 매칭
                };

                // 메인 윈도우 중앙에 띄우기 — Application.Current.MainWindow 는 LoginWindow 등 이전에
                // 닫힌 윈도우를 가리킬 수 있으므로 Windows 컬렉션에서 표시 중인 실제 MainWindow 찾는다
                Window? mainWindow = System.Windows.Application.Current.Windows
                    .OfType<MainWindow>()
                    .FirstOrDefault(w => w.IsLoaded);
                if (mainWindow != null)
                {
                    dialog.Owner = mainWindow;
                }
                else
                {
                    // MainWindow 가 아직 표시되지 않았거나 찾지 못한 경우 — Owner 미지정으로 화면 중앙
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }

                bool? result = dialog.ShowDialog();
                if (result == true)
                    ClearAlarmAndSetStandby();  // RESET 버튼 → 알람 즉시 해제
            });
        }

        public void ExecuteExportCsv()
        {
            if (AlarmHistory.Count == 0)
            {
                Dialogs.Show(T("Msg_AlarmNoData"));
                return;
            }
            try
            {
                string folderPath = AppConstants.LogFolder;
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                string fileName = $"AlarmHistory_{DateTime.Now.ToFileStamp()}.csv";
                string filePath = Path.Combine(folderPath, fileName);

                var sb = new StringBuilder();
                sb.AppendLine("Occurred Time,Alarm Code,Message,Status,Resolved Time");
                foreach (var alarm in AlarmHistory)
                {
                    string status = alarm.IsCleared ? "CLEARED" : "ACTIVE";
                    string resolvedTime = alarm.ResolvedTime.HasValue
                        ? alarm.ResolvedTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";
                    sb.AppendLine($"{alarm.OccurredTime:yyyy-MM-dd HH:mm:ss},{alarm.AlarmCode},\"{alarm.AlarmName}\",{status},{resolvedTime}");
                }
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);

                Dialogs.Show(T("Msg_AlarmExportSuccess", filePath), T("Msg_AlarmExportTitle"));
            }
            catch (UnauthorizedAccessException)
            {
                RaiseAlarm("EX-FILE-PERMISSION");
            }
            catch (Exception)
            {
                RaiseAlarm("EX-FILE-SAVE");
            }
        }
        /// <summary>
        /// 알람 발생 통합 메서드 (가이드 문구 추가 버전)
        /// </summary>
        /// <param name="code">알람 코드</param>
        /// <param name="message">알람 메시지</param>
        /// <param name="guide">조치 가이드 (추가된 부분)</param>
        /// <summary>
        /// 선택된 단일 알람 행을 CLEARED 상태로 전환합니다.
        /// </summary>
        public void ClearSingleAlarm(AlarmModel alarm)
        {
            if (alarm == null || alarm.IsCleared) return;

            alarm.IsCleared    = true;
            alarm.ResolvedTime = DateTime.Now;

            if (alarm.DbId > 0)
                _alarmRepo.LogAlarmEnd(alarm.DbId);

            // 자동 해제 타이머 정리
            if (!string.IsNullOrEmpty(alarm.AlarmCode) &&
                _autoResetTimers.TryGetValue(alarm.AlarmCode, out var t))
            {
                t.Stop();
                _autoResetTimers.Remove(alarm.AlarmCode);
            }

            // 아직 ACTIVE 알람이 남아있는지 확인
            HasActiveAlarm = AlarmHistory.Any(a => !a.IsCleared);
            OnPropertyChanged(nameof(ActiveAlarmCount));
            ((RelayCommand)AckSingleAlarmCommand).RaiseCanExecuteChanged();

            _addLog?.Invoke($"[ALARM] {alarm.AlarmCode} — 해제: {alarm.AlarmName}", LogLevel.Info);
        }

        public void ClearAlarmAndSetStandby()
        {
            var result = Dialogs.Show(T("Msg_AlarmResetAllConfirm"), T("Msg_AlarmResetAllTitle"),
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var clearedList = AlarmHistory.Where(a => !a.IsCleared).ToList();
                int clearedCount = clearedList.Count;
                HasActiveAlarm = false;
                foreach (var alarm in clearedList)
                {
                    alarm.IsCleared = true;
                    alarm.ResolvedTime = DateTime.Now;

                    if (alarm.DbId > 0)
                        _alarmRepo.LogAlarmEnd(alarm.DbId);
                }

                // 모든 자동 해제 타이머 정리
                foreach (var t in _autoResetTimers.Values) t.Stop();
                _autoResetTimers.Clear();

                OnPropertyChanged(nameof(ActiveAlarmCount));

                _addLog?.Invoke(
                    $"[ALARM] 전체 해제 — {clearedCount}건 클리어, 시스템 STANDBY",
                    LogLevel.Info);
            }
        }
        public void LoadHistoryFromDatabase()
        {
            // DB에서 전체 이력을 가져옵니다.
            var dbList = _alarmRepo.GetTotalHistory();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                AlarmHistory.Clear(); // 기존 리스트 비우기
                foreach (var item in dbList)
                {
                    AlarmHistory.Add(item); // DB 데이터 추가
                }

                // 이전 세션에서 미해제(IsCleared=false) 상태로 종료된 알람이 있다면
                // HasActiveAlarm = true 로 복원 → MachineStatusText 가 ALARM 으로 표시되고
                // SyncSystemStatusWithAlarm 트리거되어 시퀀스 시작도 차단됨
                HasActiveAlarm = AlarmHistory.Any(a => !a.IsCleared);
            });
        }

        private void ExecuteDeleteHistory()
        {
            // 1. 사용자에게 정말 삭제할지 확인 (실수 방지)
            var result = Dialogs.Show(T("Msg_AlarmDeleteConfirm"), T("Msg_AlarmDeleteTitle"),
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    _alarmRepo.DeleteAllHistory();

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        AlarmHistory.Clear();
                    });

                    Dialogs.Show(T("Msg_AlarmDeleteSuccess"));
                }
                catch (Exception ex)
                {
                    Dialogs.Show(T("Msg_AlarmDeleteError", ex.Message));
                }
            }
        }
        
    }

}