; ============================================================================
;  IJPSystem HMI  —  Inno Setup 설치 스크립트
;  build-installer.ps1 이 먼저 self-contained publish 후 이 스크립트를 컴파일한다.
;  산출물: Installer\dist\IJPSystem_Setup_<버전>.exe
; ============================================================================

#define MyAppName      "IJPSystem HMI"
#define MyAppVersion   "1.0.27"
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

[Icons]
Name: "{group}\{#MyAppName}";                         Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}";   Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";                   Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
