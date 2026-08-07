<#
    Apply-Hotfix.ps1 — 실장 PC 에 DLL 을 교체 적용한다.

        powershell -ExecutionPolicy Bypass -File Apply-Hotfix.ps1
        powershell -ExecutionPolicy Bypass -File Apply-Hotfix.ps1 -Source D:\받은파일 -Restart

    탐색기로 직접 복사하지 말 것. 복사한 줄 알았는데 예전 버전이 계속 실행되는 일이
    반복됐고(2026-08-07), 원인이 셋 다 "복사는 성공한 것처럼 보인다"는 것이었다.

      ① 설치본이 두 군데   — 고친 폴더와 바로가기가 실행하는 폴더가 다르다
      ② UAC 파일 가상화    — 관리자 권한 없이 Program Files 에 넣으면 VirtualStore 로 새고,
                             탐색기는 성공으로 보이지만 실제 폴더의 파일은 그대로다
      ③ 앱이 실행 중       — 덮어쓰기가 조용히 건너뛰어진다

    이 스크립트는 설치 경로를 <레지스트리에서> 읽고(추측하지 않는다), 관리자 권한으로
    자기를 다시 띄우고, 복사 후 해시를 대조해 <실제로 바뀌었는지> 확인한다.
    확인이 끝나면 화면 상태바에 떠야 할 build 표시를 알려주므로, 앱을 켜서 눈으로 맞춰볼 수 있다.
#>

param(
    [string] $Source,           # DLL 이 있는 폴더. 생략하면 이 스크립트 기준 ..\..\_hotfix
    [string] $AppDir,           # 설치 폴더 직접 지정(자동 탐지가 실패할 때만)
    [switch] $Restart,          # 적용 후 앱 실행
    [switch] $Force,            # 실행 중인 앱을 종료하고 진행
    [switch] $Config            # Source\Config\*.json 도 적용(기존 파일은 .bak 로 백업)
)

$ErrorActionPreference = 'Stop'
function Head($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Bad ($t) { Write-Host "  [!] $t" -ForegroundColor Red }
function Ok  ($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Note($t) { Write-Host "      $t" -ForegroundColor DarkGray }

$exeName = 'IJPSystem.Platform.HMI.exe'
$procName = 'IJPSystem.Platform.HMI'
# Inno Setup 의 AppId — IJPSystem.iss 와 반드시 같아야 한다(설치 경로를 여기서 읽는다).
$appId   = '{8B5E2A10-3C4D-4E6F-9A1B-7D2C5F8E9A00}_is1'

# ── 관리자 권한 ────────────────────────────────────────────────────────────
# 권한 없이 Program Files 에 쓰면 VirtualStore 로 새어 "복사 성공 + 반영 안 됨"이 된다.
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "관리자 권한으로 다시 실행합니다..." -ForegroundColor Yellow
    $argList = @('-ExecutionPolicy','Bypass','-NoProfile','-File', "`"$PSCommandPath`"")
    if ($Source)  { $argList += @('-Source',  "`"$Source`"") }
    if ($AppDir)  { $argList += @('-AppDir',  "`"$AppDir`"") }
    if ($Restart) { $argList += '-Restart' }
    if ($Force)   { $argList += '-Force' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    return
}

# ── 원본 폴더 ──────────────────────────────────────────────────────────────
# 기본값은 <이 스크립트가 있는 폴더>다. 받은 파일과 스크립트를 같은 폴더에 두고 그냥 실행하면
# 되도록 — 경로를 인자로 적게 하면 공백이 든 경로에서 따옴표를 빠뜨려 실행조차 안 된다
# (실장 2026-08-07: -File C:\Program Files (x86)\... 가 'C:\Program' 에서 끊겼다).
Head "원본"
if (-not $Source) {
    $Source = $PSScriptRoot
    if (@(Get-ChildItem $Source -Filter *.dll -File).Count -eq 0) {
        $repo = Join-Path $PSScriptRoot '..\..\_hotfix'      # 개발 PC 에서 저장소째로 실행할 때
        if (Test-Path $repo) { $Source = $repo }
    }
}
$Source = (Resolve-Path $Source -ErrorAction SilentlyContinue).Path
if (-not $Source -or -not (Test-Path $Source)) { Bad "원본 폴더가 없다: $Source"; return }

$srcDlls = @(Get-ChildItem $Source -Filter *.dll -File)
if ($srcDlls.Count -eq 0) {
    Bad "원본 폴더에 DLL 이 없다: $Source"
    Note "받은 DLL 과 이 스크립트를 같은 폴더에 두고 다시 실행할 것."
    return
}
Ok "$Source  (DLL $($srcDlls.Count) 개)"

# 어느 빌드를 넣는지 <b>적용 전에</b> 보여준다. 받은 파일을 복사하지 않은 채 스크립트만 다시
# 돌리면 예전 번들이 그대로 재적용되는데, 그때도 복사·검증은 전부 [OK] 로 끝나 알아채기 어렵다.
$srcStamps = @{}
foreach ($d in $srcDlls) {
    $info = $d.VersionInfo.ProductVersion
    $plus = if ($info) { $info.IndexOf('+') } else { -1 }
    $srcStamps[$d.Name] = if ($plus -ge 0) { $info.Substring($plus + 1) } else { '(빌드시각 없음)' }
}
$srcRev = @($srcStamps.Values | Sort-Object -Unique)
if ($srcRev.Count -eq 1) {
    Note "빌드 $($srcRev[0])"
} else {
    # 빌드가 섞인 조합은 실장에서 MethodNotFound 로 앱이 죽는다 — 넣기 전에 막는다.
    Bad "원본 DLL 의 빌드 시각이 섞였다 — 이대로 적용하면 앱이 MethodNotFound 로 죽는다"
    $srcStamps.GetEnumerator() | Sort-Object Name | ForEach-Object { Note ("{0,-42} {1}" -f $_.Key, $_.Value) }
    Note "개발 PC 에서 Build-Hotfix.ps1 로 번들을 다시 만들 것."
    return
}

# ── 설치 폴더 ──────────────────────────────────────────────────────────────
# 레지스트리(설치 관리자가 기록한 값)가 유일하게 믿을 수 있는 출처다.
# 폴더 이름을 짐작하면 설치본이 두 군데일 때 엉뚱한 쪽을 고치게 된다.
Head "설치 폴더"
if (-not $AppDir) {
    foreach ($root in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
                        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')) {
        $key = Join-Path $root $appId
        if (-not (Test-Path $key)) { continue }
        $loc = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
        if ($loc) { $AppDir = $loc.TrimEnd('\'); Note "레지스트리에서 확인: $key"; break }
    }
}
if (-not $AppDir -or -not (Test-Path (Join-Path $AppDir $exeName))) {
    Bad "설치 폴더를 못 찾았다 — 설치 파일(IJPSystem_Setup_*.exe)로 먼저 설치할 것"
    Note "이미 설치돼 있는데도 안 잡히면 -AppDir 로 직접 지정할 것"
    return
}
Ok $AppDir

# 원본과 대상이 같으면 복사해 봐야 아무것도 바뀌지 않는다. 명령은 성공하고 검증도 통과하는데
# 화면은 그대로라, 여기서 막지 않으면 "적용했는데 왜 안 바뀌냐"가 한 바퀴 더 돈다.
if ($Source.TrimEnd('\') -ieq $AppDir.TrimEnd('\')) {
    Bad "원본과 설치 폴더가 같다 — 받은 파일은 설치 폴더가 아닌 별도 폴더에 두어야 한다"
    Note "예: C:\hotfix 에 받은 파일을 풀고, 그 폴더에서 이 스크립트를 실행할 것"
    return
}

# 다른 사본이 남아 있으면 이번에도 엉뚱한 쪽을 볼 수 있다 — 먼저 알린다.
# ※ 경로 결합에 Join-Path 를 쓰지 않는다 — 없는 드라이브(D:)를 주면 예외를 던져 스크립트가
#    통째로 멈춘다($ErrorActionPreference='Stop'). 후보 목록은 없는 경로가 섞이는 게 정상이다.
$others = @('C:\IJPSystem','D:\IJPSystem','C:\Program Files\IJPSystem','C:\Program Files (x86)\IJPSystem',
            "$env:LOCALAPPDATA\Programs\IJPSystem",
            "$env:LOCALAPPDATA\VirtualStore\Program Files (x86)\IJPSystem",
            "$env:LOCALAPPDATA\VirtualStore\Program Files\IJPSystem") |
          Where-Object { $_.TrimEnd('\') -ine $AppDir.TrimEnd('\') } |
          Where-Object { Test-Path ($_.TrimEnd('\') + '\' + $exeName) }
if ($others) {
    Bad "다른 설치본이 남아 있다 — 바로가기가 이쪽을 가리키면 이번 적용도 안 보인다"
    $others | ForEach-Object { Note $_ }
    Note "쓰지 않는 폴더는 지우거나 이름을 바꿔 둘 것."
}

# ── 실행 중인 앱 ───────────────────────────────────────────────────────────
Head "실행 상태"
$procs = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    if (-not $Force) {
        Bad "앱이 실행 중이다 — 이 상태로 덮어쓰면 조용히 건너뛰어진다"
        Note "앱을 닫고 다시 실행하거나, -Force 를 붙여 자동 종료시킬 것"
        return
    }
    Note "앱을 종료한다..."
    $procs | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    if (Get-Process -Name $procName -ErrorAction SilentlyContinue) { Bad "종료되지 않았다"; return }
}
Ok "실행 중인 인스턴스 없음"

# ── 복사 ───────────────────────────────────────────────────────────────────
# 앱 폴더에 이미 있는 DLL 만 교체한다. 없던 파일을 새로 넣으면 deps.json 에 등록되지
# 않아 로드되지 않고, 자리만 차지한 채 "넣었는데 왜 안 되지"가 된다.
Head "복사"
$applied = @(); $skipped = @(); $failed = @()
foreach ($f in $srcDlls) {
    $dest = Join-Path $AppDir $f.Name
    if (-not (Test-Path $dest)) { $skipped += $f.Name; continue }
    try {
        Copy-Item $f.FullName $dest -Force
        Unblock-File $dest -ErrorAction SilentlyContinue   # 공유 폴더 경유 시 붙는 차단 표시 제거
        $applied += $f.Name
    } catch { $failed += "$($f.Name) — $($_.Exception.Message)" }
}
foreach ($n in $applied) { Ok $n }
foreach ($n in $skipped) { Note "$n — 설치 폴더에 없는 파일이라 건너뜀" }
foreach ($n in $failed)  { Bad  $n }

# 설정 파일은 사이트 값(교정·레시피)이 들어 있으므로 <b>기본적으로 덮지 않는다</b>.
# 다만 새 버전에서 키가 추가되면 손으로 옮기지 않는 한 그 기능이 조용히 꺼진 채로 남는다
# (FieldOfViewXUm 을 넣었는데 현장 파일에 없어 스케일 자동 적용이 안 되던 건) → -Config 로 명시 적용.
$srcCfgDir = Join-Path $Source 'Config'
if (Test-Path $srcCfgDir) {
    $srcCfgs = @(Get-ChildItem $srcCfgDir -Filter *.json -File)
    if (-not $Config) {
        Note "원본에 설정 파일 $($srcCfgs.Count) 개가 있지만 덮어쓰지 않았다 — 현장 교정값이 날아가기 때문이다."
        Note "적용하려면 -Config 를 붙일 것 (기존 파일은 .bak 로 백업된다)."
    }
    else {
        Head "설정 파일"
        $stampSuffix = Get-Date -Format 'yyyyMMdd-HHmm'
        foreach ($c in $srcCfgs) {
            $dest = Join-Path $AppDir ('Config\' + $c.Name)
            if (Test-Path $dest) {
                Copy-Item $dest "$dest.bak-$stampSuffix" -Force
                Note "백업: $($c.Name).bak-$stampSuffix"
            }
            Copy-Item $c.FullName $dest -Force
            Ok $c.Name
        }
        Note "현장에서 [교정값 저장]으로 맞춘 값이 있었다면 .bak 파일에서 다시 옮길 것."
    }
}

# ── 검증 ───────────────────────────────────────────────────────────────────
# 여기가 이 스크립트의 핵심이다. 복사 명령이 성공했다는 것과 파일이 실제로 바뀌었다는 것은
# 다른 얘기다(VirtualStore·잠금). 해시가 같아야 비로소 적용된 것이다.
Head "검증"
$allMatch = $true
foreach ($n in $applied) {
    $s = (Get-FileHash (Join-Path $Source $n) -Algorithm SHA256).Hash
    $d = (Get-FileHash (Join-Path $AppDir $n) -Algorithm SHA256).Hash
    if ($s -eq $d) { Ok "$n  일치" }
    else { Bad "$n  불일치 — 복사가 실제로는 적용되지 않았다"; $allMatch = $false }
}

# VirtualStore 로 샌 사본이 있으면 앞으로도 계속 헷갈린다 — 찾아서 알린다.
$vs = Join-Path $env:LOCALAPPDATA ("VirtualStore\" + $AppDir.Substring(3))
if (Test-Path $vs) {
    Bad "VirtualStore 사본이 있다: $vs"
    Note "예전에 권한 없이 복사한 흔적이다. 이 폴더는 지울 것."
}

if (-not $allMatch -or $failed.Count -gt 0) { Bad "적용 실패 — 위의 빨간 줄을 확인할 것"; return }

# ── 결과 ───────────────────────────────────────────────────────────────────
Head "완료"
$hmi = Get-Item (Join-Path $AppDir 'IJPSystem.Platform.HMI.dll')
$stamp = "build " + $hmi.LastWriteTime.ToString('MMdd-HHmm')
Ok "$($applied.Count) 개 적용"
Write-Host ""
Write-Host "  앱을 켜면 상태바 오른쪽 끝에 이렇게 떠야 한다:" -ForegroundColor Yellow
Write-Host "      $stamp" -ForegroundColor White
Write-Host "  다르게 뜨면 다른 폴더의 앱이 실행된 것이다 (바로가기 '대상' 확인)." -ForegroundColor DarkGray

if ($Restart) { Start-Process (Join-Path $AppDir $exeName) -WorkingDirectory $AppDir }
