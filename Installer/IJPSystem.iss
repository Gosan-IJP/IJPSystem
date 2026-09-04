; ============================================================================
;  IJPSystem HMI  —  Inno Setup 설치 스크립트
;  build-installer.ps1 이 먼저 self-contained publish 후 이 스크립트를 컴파일한다.
;  산출물: Installer\dist\IJPSystem_Setup_<버전>.exe
; ============================================================================

#define MyAppName      "IJPSystem HMI"
; 담는 것이 달라지면 반드시 올릴 것 — 같은 번호로 다른 내용이 나가면 현장에서 무엇이 깔려
; 있는지 알 길이 없다. 1.0.29: Meteor 네이티브(PrinterInterface/PrintEngine) 동봉 시작.
; 1.0.30: 인쇄 편집 화면 — 픽셀 편집(선·도형), Fill 해상도, 눈금 하한, 닫기 확인.
; 1.0.31: Meteor SDK 4.7.33387.75 → 4.9.33800.14 (제어PC 설치본과 일치). 관리 어셈블리
;         (PrinterInterfaceCLS/MeteorCLS)가 함께 바뀌므로 <b>DLL 핫픽스로는 못 나간다</b> —
;         반드시 이 설치 파일로 배포할 것. 그 밖에 글라스 정렬·대시보드 시각화 개선 다수.
; 1.0.32: 실행 파일에 LARGE_ADDRESS_AWARE 를 세워 주소공간 천장을 2GB → 4GB 로 올렸다.
;         2026-09-03 오토런 중 WPF 네이티브 할당 실패(0x80070008 in GlyphTypeface.GlyphMetrics)
;         → 렌더 스레드 사망(UCEERR_RENDERTHREADFAILURE)의 원인이 이 2GB 천장이었다.
;         ★고친 곳이 <b>.exe 헤더</b>라 DLL 핫픽스로는 못 나간다 — 반드시 이 설치 파일로.
;         함께: 주소공간 감시 로그([MEM]), 부족하면 대형객체힙 압축으로 회수 시도
;         (운전 중에는 하지 않음), 같은 예외 반복 로그 억제.
; 1.0.33: Meteor PCC 펌웨어(SocApp · PccE_ES800.rbx · HDC_ES800.rbx) 동봉. 지금까지는 호기마다
;         Meteor 설치 폴더에서 손으로 복사해야 PCC 가 붙었다 — 안 하면 34초마다 접속/절단만
;         반복하고 PccsAttached 가 0 에 머문다(2026-09-04 11호기). 이제 설치만으로 끝난다.
#define MyAppVersion   "1.0.33"
#define MyAppPublisher "GosanTech"                      ; ← 회사명으로 수정
#define MyAppExeName   "IJPSystem.Platform.HMI.exe"
#define PublishDir     "publish"                        ; build-installer.ps1 의 publish 출력

[Setup]
; AppId 는 업그레이드 식별용 고정 GUID — 절대 바꾸지 말 것
AppId={{8B5E2A10-3C4D-4E6F-9A1B-7D2C5F8E9A00}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\IJPSystem
DefaultGroupName=IJPSystem
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=IJPSystem_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
; ※ 앱을 x86(win-x86)으로 빌드하므로 32비트 모드로 설치한다(→ Program Files (x86)).
;   Comizoa ComiEcatSdk.dll 이 32비트라 앱도 x86 이어야 로드된다.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=
PrivilegesRequired=admin
CloseApplications=yes
WizardStyle=modern

[Languages]
Name: "korean";  MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 1) 프로그램 본체(self-contained publish 전체)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

; 2) Config/데이터 — 기존 파일이 있으면 보존(사이트 설정·레시피·DB 를 덮어쓰지 않음)
Source: "..\Config\*"; DestDir: "{app}\Config"; Flags: recursesubdirs createallsubdirs onlyifdoesntexist

; 2-1) 설정 파일 원본 사본 — 위(2)가 기존 파일을 건드리지 않으므로, 새 버전에서 <b>추가된 키</b>가
;      현장 설정에는 들어가지 않는다. 그 차이를 현장에서 눈으로 비교할 수 있도록 항상 최신본을
;      따로 깔아 둔다(앱은 이 폴더를 읽지 않는다 — 순수 참고용).
;      예: FieldOfViewXUm 을 새로 넣었는데 기존 DropWatcherConfig.json 에는 없어 적용이 안 되던 건.
Source: "..\Config\*.json"; DestDir: "{app}\Config\_reference"; Flags: ignoreversion

; 2-2) 운용 스크립트 — DLL 핫픽스 적용(Apply-Hotfix)과 기동 진단(Check-HmiStartup).
;      탐색기로 DLL 을 직접 복사하면 설치본이 두 군데이거나 UAC 가상화·파일 잠금 때문에
;      "복사는 성공했는데 예전 버전이 실행"되는 일이 반복된다. 그 도구를 장비에 같이 둔다.
Source: "..\Tools\Deploy\*.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "..\Tools\Diag\*.ps1";   DestDir: "{app}\Tools"; Flags: ignoreversion

; 3) 벤더 네이티브 DLL(ComiEcatSdk.dll, niimaqdx 등) — native\ 폴더에 넣어두면 포함, 없으면 건너뜀
Source: "native\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion skipifsourcedoesntexist

; 4) Meteor PCC 펌웨어 — PCC-E 는 전원만으로 부팅하지 못하고 PC 에서 SoC 앱과 FPGA 이미지를
;    내려받아야 살아난다. 없으면 PCC 가 34초마다 붙었다 끊기기만 하고 PccsAttached 는 0 에 머문다.
;    (2026-09-04 11호기 브링업에서 반나절을 쓴 자리 — 자세한 사연은 meteor\README.txt)
;    ※ ignoreversion 으로 항상 최신본을 덮는다. 사람이 편집하는 파일이 아니라 벤더 바이너리다.
Source: "meteor\ApplicationImages\*"; DestDir: "{app}\Config\ApplicationImages"; Flags: recursesubdirs createallsubdirs ignoreversion skipifsourcedoesntexist
;    Rbf 는 엔진이 찾는 기준이 둘로 갈려 두 군데에 깐다. 어느 쪽을 읽는지 확정되면 한 줄 지울 것.
Source: "meteor\Rbf\*";              DestDir: "{app}\Rbf";                      Flags: recursesubdirs createallsubdirs ignoreversion skipifsourcedoesntexist
Source: "meteor\Rbf\*";              DestDir: "{app}\Config\Rbf";               Flags: recursesubdirs createallsubdirs ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}";                         Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}";   Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";                   Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
