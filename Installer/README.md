# IJPSystem HMI 설치 파일(setup.exe) 만들기

장비 PC에 HMI 를 배포하는 **Inno Setup 기반 설치 프로그램**을 생성합니다.
프로그램은 **self-contained**(win-x64)로 publish 되므로 대상 PC에 .NET 8 설치가 필요 없습니다.

## 사전 준비 (빌드 PC)
1. **.NET 8 SDK**
2. **Inno Setup 6** — https://jrsoftware.org/isdl.php (한국어 메시지 포함)
3. (선택) 실장비용 벤더 DLL 을 `native\` 폴더에 복사 (`native\README.txt` 참고)

## 빌드
```powershell
cd Installer
.\build-installer.ps1
```
1. `dotnet publish` (Release, win-x64, self-contained) → `Installer\publish\`
2. Inno Setup 컴파일 → **`Installer\dist\IJPSystem_Setup_1.0.0.exe`**

## 설치 파일이 포함하는 것
| 항목 | 위치(설치 후) | 비고 |
|---|---|---|
| 프로그램 본체 | `{app}\` | self-contained publish 전체 |
| Config/데이터 | `{app}\Config\` | 기존 파일 있으면 **보존**(사이트 설정·레시피·DB 미덮어씀) |
| 벤더 DLL | `{app}\` | `native\` 에 넣어둔 파일만 |
| 바로가기 | 시작 메뉴 / (선택)바탕화면 | |

## 실장(현장) 체크리스트
- `Config\AppConfig.json` 의 `DriverMode` 를 `Comizoa`/`Imaqdx` 등 실장비로 설정.
- `Config\ComiEcatLibCfg.ini` 의 IP/포트/`SimulationMode=0` 확인.
- **벤더 런타임 별도 설치**: Comizoa EtherCAT, NI-DAQmx/IMAQdx 등은 각 벤더 설치관리자로
  대상 PC에 설치해야 함(이 설치 파일은 앱만 배포).
- `ecat_*` P/Invoke 는 아직 스켈레톤 — 실 SDK 시그니처 확정 후 재빌드 필요.

## 버전 올리기
`IJPSystem.iss` 의 `MyAppVersion` 수정 후 다시 빌드. `AppId`(GUID)는 **바꾸지 말 것**
(업그레이드 인식용). 회사명은 `MyAppPublisher` 수정.
