-- ============================================================
-- AlarmMaster_Seed.sql
-- 대상 DB: AlarmSystemDb (AppConstants.AlarmSystemDb 경로)
-- 갱신일: 2026-04-30
-- 행수  : 83
-- 카테고리: 1.Sequence  2.AddLog  3.MessageBox  4.Sensor
--          5.Motor     6.IO     7.Exception   8.Recipe
-- 심각도 : Fatal / Error / Warning / Info
-- ============================================================

-- ─────────────────────────────────────────────
-- 1. Schema
--    (기존 5컬럼 → 13컬럼으로 확장. 신규 DB 기준 IF NOT EXISTS)
-- ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS AlarmMaster (
    AlarmCode         TEXT    PRIMARY KEY,
    Category          INTEGER NOT NULL DEFAULT 0,
    CategoryName      TEXT,
    Severity          TEXT    NOT NULL DEFAULT 'Info',
    AlarmName_KR      TEXT,
    AlarmName_EN      TEXT,
    ActionGuide_KR    TEXT,
    ActionGuide_EN    TEXT,
    TriggerCondition  TEXT,
    AckRequired       INTEGER NOT NULL DEFAULT 1,
    AutoResetDelayMs  INTEGER,
    FileLocation      TEXT,
    CreatedAt         DATETIME DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt         DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS IX_AlarmMaster_Category ON AlarmMaster(Category);
CREATE INDEX IF NOT EXISTS IX_AlarmMaster_Severity ON AlarmMaster(Severity);

-- ─────────────────────────────────────────────
-- 1-A. 레거시 DB 마이그레이션 (이미 5컬럼 테이블이 있는 경우)
--      SQLite는 ADD COLUMN IF NOT EXISTS 미지원 → C#에서 PRAGMA table_info 체크 후
--      개별 ALTER 실행 권장. 본 SQL은 신규 DB 기준이며, 마이그레이션 실행 시
--      아래 ALTER 라인만 따로 try/catch로 실행할 것.
-- ─────────────────────────────────────────────
-- ALTER TABLE AlarmMaster ADD COLUMN Category         INTEGER NOT NULL DEFAULT 0;
-- ALTER TABLE AlarmMaster ADD COLUMN CategoryName     TEXT;
-- ALTER TABLE AlarmMaster ADD COLUMN Severity         TEXT NOT NULL DEFAULT 'Info';
-- ALTER TABLE AlarmMaster ADD COLUMN TriggerCondition TEXT;
-- ALTER TABLE AlarmMaster ADD COLUMN AckRequired      INTEGER NOT NULL DEFAULT 1;
-- ALTER TABLE AlarmMaster ADD COLUMN AutoResetDelayMs INTEGER;
-- ALTER TABLE AlarmMaster ADD COLUMN FileLocation     TEXT;
-- ALTER TABLE AlarmMaster ADD COLUMN CreatedAt        DATETIME DEFAULT CURRENT_TIMESTAMP;
-- ALTER TABLE AlarmMaster ADD COLUMN UpdatedAt        DATETIME DEFAULT CURRENT_TIMESTAMP;

-- ─────────────────────────────────────────────
-- 2. Seed Data (83 rows)
--    INSERT OR IGNORE: 기존 코드 보존, 신규만 추가
--    (마스터 메시지 강제 갱신 필요 시 OR IGNORE → OR REPLACE 변경)
-- ─────────────────────────────────────────────
BEGIN TRANSACTION;

INSERT OR IGNORE INTO AlarmMaster
    (AlarmCode, Category, CategoryName, Severity, AlarmName_KR, ActionGuide_KR, TriggerCondition, AckRequired, AutoResetDelayMs, FileLocation)
VALUES
    -- ── [1] Sequence ──────────────────────────────────────────
    ('SEQ-WAIT-TIMEOUT',         1, 'Sequence',   'Error',   '조건 미충족 — 제한 시간 초과',                        '대상 IO 신호/모션 상태 확인 후 시퀀스 재시작', 'WaitHelper.ForIOSignal/ForMotionDone/ForCondition timeoutMs 초과', 1, NULL, 'WaitHelper.cs:40'),
    ('SEQ-NJI-NG',               1, 'Sequence',   'Error',   '노즐 검사 NG',                                        '노즐 청소 / 퍼지 후 재검사',                  'CaptureAndWait() 결과 IsPass==false 또는 result==null',           1, NULL, 'NJISequence.cs:34'),
    ('SEQ-STEP-FAIL',             1, 'Sequence',   'Error',   '시퀀스 스텝 실패',                                    '로그 메시지 확인 후 해당 스텝 재실행',         'Step Action 메서드에서 일반 Exception 발생',                       1, NULL, 'SequenceViewModel.cs:227'),
    ('SEQ-STEP-TIMEOUT',          1, 'Sequence',   'Error',   '시퀀스 스텝 타임아웃',                                '원인 IO/모션 점검 후 재실행',                  'Step 실행 중 TimeoutException',                                    1, NULL, 'SequenceViewModel.cs:229'),
    ('SEQ-INIT-TIMEOUT',          1, 'Sequence',   'Error',   'INITIALIZE 시퀀스 타임아웃',                          '원점복귀 신호 미수신 — 모터 드라이버/케이블 확인', '원점복귀/READY 이동이 30초 이내 미완료',                          1, NULL, 'InitializeViewModel.cs:199'),
    ('SEQ-INIT-FAIL',             1, 'Sequence',   'Error',   'INITIALIZE 시퀀스 실패',                              '로그 확인 후 INITIALIZE 재시작',               '서보 ON/원점복귀/위치이동 중 Exception',                          1, NULL, 'InitializeViewModel.cs:209'),
    ('SEQ-MOTION-TIMEOUT',        1, 'Sequence',   'Error',   '전축 모션 완료 대기 타임아웃',                        '축 알람·EMO·간섭 확인 후 INITIALIZE 재수행',  'ForAllMotionDone에서 IsMoving 지속 — 시간 초과',                  1, NULL, 'MainDashboardViewModel.cs:508'),
    ('SEQ-AUTO-PRINT-FAIL',       1, 'Sequence',   'Error',   'AUTO PRINT 시퀀스 실패',                              '로그 확인 → EMERGENCY RESET 후 재시도',         'AUTO PRINT 실행 중 예상치 못한 Exception',                        1, NULL, 'MainDashboardViewModel.cs:517'),
    ('SEQ-NO-ACTIVE-RECIPE',      1, 'Sequence',   'Warning', '적용된 레시피 없음',                                  'RECIPE 화면에서 레시피를 선택하고 APPLY 후 재시도', '시퀀스 시작 시 ActiveRecipeName 이 비어 있음',                     0, NULL, 'SequenceViewModel.cs / PnidViewModel.cs'),
    ('SEQ-ALARM-ACTIVE',          1, 'Sequence',   'Warning', '미해제 알람 존재',                                    '알람 화면에서 알람을 해제(Clear)한 뒤 재시도',     '시퀀스 시작 시 HasActiveAlarm == true',                            0, NULL, 'SequenceViewModel / PnidViewModel / MainDashboardViewModel'),

    -- ── [2] AddLog (Error/Fatal/Warning) ─────────────────────
    ('LOG-DW-CAPTURE-FAIL',       2, 'AddLog',     'Error',   'DropWatcher 캡쳐 실패',                               '카메라 케이블/드라이버/연결 상태 확인',         'Vision 카메라 촬영 중 Exception',                                  1, NULL, 'DropWatcherViewModel.cs:197'),
    ('LOG-DW-INSPECT-FAIL',       2, 'AddLog',     'Error',   'DropWatcher 검사 실패',                               'Vision 서버/모델 파일 상태 확인',               'CaptureAndInspectAsync() 중 Exception',                            1, NULL, 'DropWatcherViewModel.cs:223'),
    ('LOG-AXIS-MOVE-FAIL',        2, 'AddLog',     'Error',   '축 이동 실패',                                        '축 알람 리셋 / 리미트 스위치 상태 확인',        'MoveAbs/MoveRel/MoveJog Exception',                                1, NULL, 'AxisViewModel.cs:259'),
    ('LOG-AXIS-HW-ALARM',         2, 'AddLog',     'Error',   '축 하드웨어 알람 감지',                               '드라이버 알람 코드 확인 후 리셋',               'UpdateMotorStatus()에서 IsAlarm==true 처음 감지',                  1, NULL, 'AxisViewModel.cs:330'),
    ('LOG-AXIS-COMM-ERR',         2, 'AddLog',     'Warning', '축 통신 에러',                                        '모션 카드/통신 케이블 상태 확인',               'GetStatus() 100ms 폴링 중 Exception',                              0, 5000, 'AxisViewModel.cs:364'),
    ('LOG-NAV-BLOCKED',           2, 'AddLog',     'Warning', 'AUTO PRINT 진행 중 화면 전환 거부',                   'AUTO PRINT 종료 후 전환',                       'IsRunning==true 상태에서 메뉴 전환 시도',                          0, 3000, 'MainViewModel.cs:432'),
    ('LOG-PMC-PRESSURE-SV',       2, 'AddLog',     'Warning', 'PMC 양압 SV 적용',                                    '변경값 확인 (정보성 알람)',                     'SetOutput(AO_TARGET_PURGE) 호출',                                  0, 2000, 'PnidViewModel.cs:65'),
    ('LOG-PMC-VACUUM-SV',         2, 'AddLog',     'Warning', 'PMC 음압 SV 적용',                                    '변경값 확인 (정보성 알람)',                     'SetOutput(AO_TARGET_MENISCUS) 호출',                               0, 2000, 'PnidViewModel.cs:72'),
    ('LOG-NJI-CAPTURE-FAIL',      2, 'AddLog',     'Error',   'NJI 촬영 실패',                                       'Vision 카메라 상태 확인',                       'CaptureAsync() Exception',                                         1, NULL, 'NJIViewModel.cs:207'),
    ('LOG-NJI-NG',                2, 'AddLog',     'Error',   'NJI 검사 NG',                                         '노즐 퍼지 후 재검사',                           'CaptureAndInspectAsync 결과 IsPass==false',                        1, NULL, 'NJIViewModel.cs:229'),
    ('LOG-NJI-INSPECT-FAIL',      2, 'AddLog',     'Error',   'NJI 검사 실패',                                       'Vision 검사 모델 파일 확인',                    'CaptureAndInspectAsync() Exception',                               1, NULL, 'NJIViewModel.cs:236'),
    ('LOG-TEACH-LOAD-FAIL',       2, 'AddLog',     'Error',   '티칭 로드 실패',                                      'DB 파일 권한/스키마 확인',                       'DB 조회 중 Exception',                                             1, NULL, 'MotorTeachingViewModel.cs:252'),
    ('LOG-TEACH-SAVE-FAIL',       2, 'AddLog',     'Error',   '티칭 저장 실패',                                      'DB 잠금/디스크 용량 확인',                       'DB 저장 중 Exception',                                             1, NULL, 'MotorTeachingViewModel.cs:306'),
    ('LOG-WAVEFORM-LOAD-FAIL',    2, 'AddLog',     'Error',   '웨이브폼 로드 실패',                                  'Waveform 파일 경로/포맷 확인',                  '파일 로딩 중 Exception',                                           1, NULL, 'WaveformViewModel.cs:233'),
    ('LOG-EMO-ACTIVE',            2, 'AddLog',     'Fatal',   'EMO 비상정지 감지',                                   'EMO 해제 후 시스템 리셋',                       'EMO_FRONT/LEFT/RIGHT/BACK 중 하나 ON',                             1, NULL, 'MainDashboardViewModel.cs:594'),
    ('LOG-DOOR-LOCK-FAIL',        2, 'AddLog',     'Error',   '도어 잠금 실패',                                      '모든 도어 닫힘 및 잠금 확인',                    'IsDoorLocked()==false (가동 전 안전조건)',                         1, NULL, 'MainDashboardViewModel.cs:603'),
    ('LOG-PRESSURE-FAIL',         2, 'AddLog',     'Error',   '압력 스위치 미동작',                                  'CDA 라인 압력 확인',                            'IsPressureOk(n)==false',                                           1, NULL, 'MainDashboardViewModel.cs:613'),
    ('LOG-NOT-SERVO-ON',          2, 'AddLog',     'Error',   '가동 전 서보 OFF 상태 축 존재',                       'All Servo ON 후 재시도',                        '하나 이상의 축이 IsServoOn==false',                                1, NULL, 'MainDashboardViewModel.cs:562'),
    ('LOG-NOT-HOMED',             2, 'AddLog',     'Error',   '가동 전 원점복귀 미수행 축 존재',                     'INITIALIZE 수행 후 재시도',                     '하나 이상의 축이 IsHomeDone==false',                               1, NULL, 'MainDashboardViewModel.cs:550'),
    ('LOG-MC-ALL-HOME-FAIL',      2, 'AddLog',     'Error',   '일괄 원점복귀 실패',                                  '개별 축 상태 확인 후 재시도',                    '일괄 원점복귀 명령 중 Exception',                                  1, NULL, 'MotorControlViewModel.cs:85'),
    ('LOG-MC-ALL-SVO-ON-FAIL',    2, 'AddLog',     'Error',   '일괄 서보 ON 실패',                                   '개별 축 알람 상태 확인',                         '일괄 서보 ON 명령 중 Exception',                                   1, NULL, 'MotorControlViewModel.cs:98'),
    ('LOG-MC-ALL-SVO-OFF-FAIL',   2, 'AddLog',     'Error',   '일괄 서보 OFF 실패',                                  '개별 축 상태 확인',                              '일괄 서보 OFF 명령 중 Exception',                                  1, NULL, 'MotorControlViewModel.cs:111'),
    ('LOG-MC-ALL-STOP-FAIL',      2, 'AddLog',     'Error',   '일괄 정지 실패',                                      '개별 축 상태 확인',                              '일괄 정지 명령 중 Exception',                                      1, NULL, 'MotorControlViewModel.cs:124'),

    -- ── [3] MessageBox 팝업 ──────────────────────────────────
    ('MSG-LOGIN-FAIL',            3, 'MessageBox', 'Error',   '비밀번호 불일치',                                     '비밀번호 재입력',                                'LoginWindow 비밀번호 입력 불일치',                                 1, NULL, 'LoginWindow.xaml.cs:43'),
    ('MSG-VM-NOT-FOUND',          3, 'MessageBox', 'Error',   'MainViewModel 미검출',                               'App.xaml.cs DI 등록 확인',                       'DataContext가 MainViewModel이 아님',                               1, NULL, 'MainWindow.xaml.cs:30'),
    ('MSG-EXIT-CONFIRM',          3, 'MessageBox', 'Info',    '프로그램 종료 확인',                                  'Yes/No 선택',                                    '윈도우 닫기 시도',                                                 0, NULL, 'MainWindow.xaml.cs:65'),
    ('MSG-DATE-RANGE-INV',        3, 'MessageBox', 'Warning', '날짜 범위 오류',                                      '날짜 재선택',                                    'FilterEndDate < FilterStartDate',                                  0, NULL, 'AlarmViewModel.cs:87'),
    ('MSG-RECIPE-DIRTY',          3, 'MessageBox', 'Info',    '레시피 미저장 변경 사항 확인',                       'Yes/No/Cancel 선택',                             '수정된 레시피 미저장 상태에서 다른 레시피 선택',                  0, NULL, 'RecipeViewModel.cs:76'),
    ('MSG-RECIPE-CREATE-FAIL',    3, 'MessageBox', 'Error',   '레시피 생성 실패',                                    'DB 권한/디스크 용량 확인',                       'DB Insert Exception',                                              1, NULL, 'RecipeViewModel.cs:524'),
    ('MSG-RECIPE-VEL-OOR',        3, 'MessageBox', 'Warning', '속도 설정값 범위 초과',                               '속도값 범위 내로 수정 (Move 1~2000 / Jog 0~5000)', 'Move.Velocity 또는 Jog.Velocity 범위 위반',                       0, NULL, 'RecipeViewModel.cs:563'),
    ('MSG-RECIPE-PRINT-OOR',      3, 'MessageBox', 'Warning', '인쇄 파라미터 범위 초과',                             '속도 0~5000, 가속도/감속도 0~50000 범위로 수정', 'Printing 파라미터 범위 위반',                                       0, NULL, 'RecipeViewModel.cs:576'),
    ('MSG-RECIPE-APPLY-CONFIRM',  3, 'MessageBox', 'Info',    '레시피 적용 확인',                                    'Yes/No 선택',                                    '사용자 적용 클릭',                                                 0, NULL, 'RecipeViewModel.cs:670'),
    ('MSG-RECIPE-DUP-NAME',       3, 'MessageBox', 'Error',   '레시피 이름 중복',                                    '다른 이름 사용',                                 '신규/복제 시 동일 이름 입력',                                      0, NULL, 'RecipeViewModel.cs:710'),
    ('MSG-RECIPE-ACTIVE-DEL',     3, 'MessageBox', 'Warning', '적용 중 모델 삭제 불가',                              '다른 모델 적용 후 삭제',                         'ActiveRecipeName==SelectedRecipeName 상태에서 삭제',               0, NULL, 'RecipeViewModel.cs:813'),
    ('MSG-AXIS-NUMERIC-INV',      3, 'MessageBox', 'Warning', '숫자 입력 검증 실패',                                 '숫자값 입력',                                    'TargetPosition 비숫자 입력',                                       0, NULL, 'AxisViewModel.cs:229'),
    ('MSG-WAVEFORM-NO-RECIPE',    3, 'MessageBox', 'Warning', '적용 중인 레시피 없음',                               '레시피 먼저 적용',                               'ActiveRecipeName empty 상태에서 웨이브폼 저장',                    0, NULL, 'WaveformViewModel.cs:254'),

    -- ── [4] Sensor (센서/안전 인터락) ────────────────────────
    ('SNS-RES-OVERFLOW',          4, 'Sensor',     'Error',   '레저버 오버플로우',                                   '약액 공급 즉시 중단 및 배출',                    'DI_RESERVOIR_OVERFLOW_1 ON',                                       1, NULL, 'InkjetMachine.Fluid.cs:109'),
    ('SNS-RES-HIGH',              4, 'Sensor',     'Warning', '레저버 상한 수위',                                    '공급 일시 중단',                                 'DI_RESERVOIR_HIGH_1 ON',                                           1, 3000, 'InkjetMachine.Fluid.cs:110'),
    ('SNS-RES-EMPTY',             4, 'Sensor',     'Error',   '레저버 빈 상태 (잉크 공급 필요)',                     'Canister 잉크 보충 후 공급',                     'DI_RESERVOIR_EMPTY_1 ON',                                          1, NULL, 'InkjetMachine.Fluid.cs:112'),
    ('SNS-BOTTLE-NOT-DETECT',     4, 'Sensor',     'Error',   '잉크 보틀(Canister) 미감지',                          'Canister 장착 확인',                             'DI_BOTTLE_1_DETECT_SENSOR OFF',                                    1, NULL, 'InkjetMachine.Fluid.cs:78'),
    ('SNS-BOTTLE-LEAK',           4, 'Sensor',     'Error',   '잉크 보틀 누액 감지',                                 '즉시 공급 중단, 누액 처리',                      'DI_BOTTLE_1_LEAK_SENSOR ON',                                       1, NULL, 'InkjetMachine.Fluid.cs:79'),
    ('SNS-MT-BOTTLE-HIGH',        4, 'Sensor',     'Warning', '드레인 보틀 상한 수위',                               '드레인 보틀 비우기',                             'DI_BOTTLE_{n}_LEVEL_SENSOR_HIGH ON',                               1, NULL, 'InkjetMachine.Fluid.cs:82'),
    ('SNS-OVERFLOW-SUMP',         4, 'Sensor',     'Error',   '오버플로우 섬프 감지',                                '즉시 공정 중단 및 점검',                         'DI_OVERFLOW_SENSOR ON',                                            1, NULL, 'InkjetMachine.Fluid.cs:115'),
    ('SNS-MT-SPT-OVERFLOW',       4, 'Sensor',     'Warning', 'MT SPT 오버플로우 감지',                              'SPT 배출 점검',                                  'DI_MT_SPT_OVERFLOW_SENSOR ON',                                     1, NULL, 'InkjetMachine.Fluid.cs:116'),
    ('SNS-PRESSURE-NG',           4, 'Sensor',     'Error',   '압력 스위치 미동작',                                  'CDA/공압 라인 압력 점검',                        'DI_PRESSURE_SW{1~11} OFF',                                         1, NULL, 'InkjetMachine.Safety.cs:40'),
    ('SNS-EMO',                   4, 'Sensor',     'Fatal',   '비상정지 버튼 활성',                                  'EMO 버튼 해제 및 시스템 리셋',                    'EMO_FRONT/LEFT/RIGHT/BACK 중 하나 ON',                             1, NULL, 'InkjetMachine.Safety.cs:33'),
    ('SNS-TEMP-HIGH',             4, 'Sensor',     'Error',   '온도 상한 알람 (2호기)',                              '설비 온도/냉각 상태 점검',                       'DI_TEMP_HIGH_ALARM ON',                                            1, NULL, 'MainDashboardViewModel.cs'),
    ('SNS-TEMP-LOW',              4, 'Sensor',     'Error',   '온도 하한 알람 (2호기)',                              '설비 온도/히터 상태 점검',                       'DI_TEMP_LOW_ALARM ON',                                             1, NULL, 'MainDashboardViewModel.cs'),
    ('SNS-DOOR-OPEN',             4, 'Sensor',     'Error',   '도어 미잠금 상태',                                    '모든 도어 닫힘 및 잠금 확인',                    'DI_DOOR_LOCK_ALL OFF',                                             1, NULL, 'InkjetMachine.Door.cs:30'),
    ('SNS-RES-LEVEL-STATE',       4, 'Sensor',     'Info',    '레저버 수위 상태 변경',                               '수위 상태 표시 (P&ID)',                          '4센서 조합 — HH/High/Set/Low/Empty',                               0, 500,  'PnidViewModel.cs:247'),

    -- ── [5] Motor / Axis ─────────────────────────────────────
    ('MOT-AXIS-ALM',              5, 'Motor',      'Error',   '축 {0} 하드웨어 알람',                                '드라이버 알람 코드 확인 후 리셋',                'GetStatus().IsAlarm==true',                                        1, NULL, 'AxisViewModel.cs:330'),
    ('MOT-NOT-SERVO',             5, 'Motor',      'Error',   '가동 전 서보 OFF 축 존재',                            'All Servo ON 후 재시도',                         '하나 이상의 축이 IsServoOn==false',                                1, NULL, 'MainDashboardViewModel.cs:562'),
    ('MOT-NOT-HOMED',             5, 'Motor',      'Error',   '가동 전 원점복귀 미수행 축 존재',                     'INITIALIZE 수행',                                '하나 이상의 축이 IsHomeDone==false',                               1, NULL, 'MainDashboardViewModel.cs:550'),
    ('MOT-SERVO-ON-FAIL',         5, 'Motor',      'Error',   '서보 ON 실패 — {0}',                                  '드라이버 확인 후 프로그램 재실행 해주세요',      'INITIALIZE 서보 ON 후 5초 안에 켜지지 않은 축이 있음',            1, NULL, 'MotionServiceAdapter.cs:ServoOnAllAsync'),
    ('MOT-UPPER-LIMIT',          5, 'Motor',      'Warning', '상한 리미트 스위치 활성',                             '축을 하한 방향으로 후퇴 후 알람 리셋',          'UpperLimit==true',                                                 1, NULL, 'AxisViewModel.cs:121'),
    ('MOT-LOWER-LIMIT',          5, 'Motor',      'Warning', '하한 리미트 스위치 활성',                             '축을 상한 방향으로 후퇴 후 알람 리셋',          'LowerLimit==true',                                                 1, NULL, 'AxisViewModel.cs:128'),
    ('MOT-INPOS-TIMEOUT',         5, 'Motor',      'Error',   'InPosition 미달성 (타임아웃)',                        '위치 결정 게인/감속 파라미터 확인',              'WaitHelper IsInPosition timeoutMs 초과',                           1, NULL, 'WaitHelper.cs:84'),
    ('MOT-MOTION-TIMEOUT',        5, 'Motor',      'Error',   '모션 완료 미감지 (타임아웃)',                         '속도/거리 설정 확인',                            'WaitHelper ForAllMotionDone IsMoving 지속',                        1, NULL, 'WaitHelper.cs:76'),

    -- ── [6] IO 통신 ──────────────────────────────────────────
    ('IO-AXIS-COMM',              6, 'IO',         'Warning', '축 통신 에러',                                        '모션 카드 케이블/전원 확인',                     'Axis GetStatus() 100ms 폴링 Exception',                            0, 5000, 'AxisViewModel.cs:364'),
    ('IO-VISION-DISCONNECT',      6, 'IO',         'Warning', 'Vision 카메라 미연결',                                'Vision 서버/USB 연결 확인',                       'machine.Vision?.IsConnected==false',                               0, 5000, 'MainViewModel.cs:404'),
    ('IO-NJI-CAPTURE',            6, 'IO',         'Error',   'NJI 카메라 촬영 실패',                                '카메라 케이블/드라이버 확인',                     'NJI CaptureAsync() Exception',                                     1, NULL, 'NJIViewModel.cs:207'),
    ('IO-NJI-INSPECT',            6, 'IO',         'Error',   'NJI 검사 실패',                                       'Vision 모델/연결 확인',                           'NJI CaptureAndInspectAsync() Exception',                           1, NULL, 'NJIViewModel.cs:236'),
    ('IO-DW-CAPTURE',             6, 'IO',         'Error',   'DropWatcher 촬영 실패',                               'DropWatcher 카메라 점검',                         'Vision.CaptureAsync(CAM_DW) Exception',                            1, NULL, 'DropWatcherViewModel.cs:197'),
    ('IO-DW-INSPECT',             6, 'IO',         'Error',   'DropWatcher 검사 실패',                               'DropWatcher 모델/연결 확인',                      'Vision.CaptureAndInspectAsync(CAM_DW) Exception',                  1, NULL, 'DropWatcherViewModel.cs:223'),
    ('DW-NOZZLE-NG',              6, 'IO',         'Warning', 'DropWatcher 노즐 NG',                                  '노즐 청소/퍼지 후 재검사',                          'DropWatcher 검사 결과 IsPass==false',                              1, NULL, 'DropWatcherViewModel.cs:219'),

    -- ── [7] Exception ────────────────────────────────────────
    ('EX-TIMEOUT',                7, 'Exception',  'Error',   'TimeoutException — 시퀀스 대기 실패',                 '시간/조건 재검토',                                'WaitHelper 타임아웃',                                              1, NULL, 'WaitHelper.cs:40'),
    ('EX-NJI-NG',                 7, 'Exception',  'Error',   'InvalidOperationException — NJI NG',                 '노즐 청소 후 재검사',                              'NJISequence 결과 IsPass==false',                                   1, NULL, 'NJISequence.cs:34'),
    ('EX-DB-MIGRATION',           7, 'Exception',  'Info',    'DB 마이그레이션 무시',                                '정상 동작 (예상된 무시)',                          'ALTER TABLE 실패 시 try-catch 무시',                               0, NULL, 'RecipeViewModel.cs:238'),
    ('EX-SQLITE-MISS',            7, 'Exception',  'Warning', 'DB 테이블 미존재 (자동생성)',                         '자동 생성됨',                                      'RecipeDetails_Position 테이블 없음',                               0, 1000, 'MotorTeachingViewModel.cs:245'),
    ('EX-FILE-PERMISSION',        7, 'Exception',  'Error',   '로그 내보내기 권한 오류',                             '경로/권한 변경 후 재시도',                         '파일 쓰기 권한 없음 또는 경로 오류',                              1, NULL, 'AlarmViewModel.cs:213'),
    ('EX-FILE-SAVE',              7, 'Exception',  'Error',   '로그 저장 실패',                                      '디스크 용량/경로 확인',                            '파일 쓰기 Exception',                                              1, NULL, 'AlarmViewModel.cs:220'),
    ('EX-MOTION-DRIVER',          7, 'Exception',  'Error',   'Motion Driver Exception',                            '드라이버 상태 확인',                                'IMotionDriver 메서드 Exception',                                   1, NULL, 'AxisViewModel.cs:257'),
    ('EX-DB-INSERT',              7, 'Exception',  'Error',   'DB Insert Exception',                                 'DB 잠금/스키마 확인',                              'Sqlite Insert 실패',                                                1, NULL, 'RecipeViewModel.cs:521'),

    -- ── [8] Recipe / 입력 검증 ───────────────────────────────
    ('RCP-VEL-OOR',               8, 'Recipe',     'Warning', 'Move/Jog 속도 범위 초과',                              '범위 내 값으로 수정',                              'Move 1~2000, Jog 0~5000 범위 위반',                                0, NULL, 'RecipeViewModel.cs:555'),
    ('RCP-PRINT-OOR',             8, 'Recipe',     'Warning', '인쇄 파라미터 범위 초과',                              '범위 내 값으로 수정',                              '속도 0~5000, 가속도/감속도 0~50000 위반',                          0, NULL, 'RecipeViewModel.cs:567'),
    ('RCP-CREATE-FAIL',           8, 'Recipe',     'Error',   '레시피 생성 실패',                                    'DB 상태 확인',                                     'DB 트랜잭션 실패',                                                 1, NULL, 'RecipeViewModel.cs:521'),
    ('RCP-SAVE-FAIL',             8, 'Recipe',     'Error',   '레시피 저장 실패',                                    'DB 잠금/디스크 확인',                              'DELETE+INSERT 트랜잭션 실패',                                      1, NULL, 'RecipeViewModel.cs:657'),
    ('RCP-RENAME-FAIL',           8, 'Recipe',     'Error',   '레시피 이름 변경 실패',                               'DB 상태 확인',                                     'UPDATE 쿼리 실패',                                                 1, NULL, 'RecipeViewModel.cs:743'),
    ('RCP-COPY-DUP',              8, 'Recipe',     'Warning', '레시피 복제 시 이름 중복',                            '다른 이름으로 저장',                                'Copy 후 동일 이름 저장 시도',                                      0, NULL, 'RecipeViewModel.cs:870'),
    ('RCP-PURGETIME-CLAMP',       8, 'Recipe',     'Info',    'PurgeTime 자동 정정 (0~60s)',                         '자동 Clamp 처리',                                   'PurgeTime < 0 또는 > 60 입력',                                     0, 500,  'RecipeViewModel.cs:136');

COMMIT;

-- ─────────────────────────────────────────────
-- 2-A. 메시지 템플릿 보정 (이미 시드된 DB에도 placeholder 반영)
--      INSERT OR IGNORE는 기존 행을 건드리지 않으므로,
--      placeholder 추가 같은 메시지 변경은 별도 UPDATE로 동기화한다.
--      WHERE 조건으로 무의미한 쓰기는 회피.
-- ─────────────────────────────────────────────
UPDATE AlarmMaster SET AlarmName_KR = '축 {0} 하드웨어 알람'
 WHERE AlarmCode = 'MOT-AXIS-ALM' AND AlarmName_KR <> '축 {0} 하드웨어 알람';

-- ─────────────────────────────────────────────
-- 3. 검증용 쿼리 (실행 후 확인)
-- ─────────────────────────────────────────────
-- SELECT Category, CategoryName, COUNT(*) AS cnt FROM AlarmMaster GROUP BY Category ORDER BY Category;
-- SELECT Severity, COUNT(*) AS cnt FROM AlarmMaster GROUP BY Severity;
-- SELECT * FROM AlarmMaster WHERE Severity = 'Fatal';
