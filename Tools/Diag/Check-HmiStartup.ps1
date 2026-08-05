# HMI 기동 실패 진단 — 실장 PC 에서 실행한다.
#
#   powershell -ExecutionPolicy Bypass -File Check-HmiStartup.ps1
#   powershell -ExecutionPolicy Bypass -File Check-HmiStartup.ps1 -AppDir "D:\IJPSystem"
#
# DLL 만 교체한 뒤 exe 가 안 뜰 때 원인을 좁힌다. 읽기 전용이며, 유일한 변경은
# -Unblock 을 줬을 때의 차단 해제뿐이다. 앱을 실행하지 않으므로 장비에 영향이 없다.

param(
    [string] $AppDir,
    [switch] $Unblock,      # 다른 PC 에서 복사해 온 DLL 의 '차단' 표시를 해제
    [switch] $Trace         # .NET 호스트 어셈블리 해석 추적 로그를 남기며 앱을 1회 실행
)

$ErrorActionPreference = 'Continue'
function Head($t) { Write-Host ""; Write-Host "── $t " -NoNewline; Write-Host ("─" * [Math]::Max(0, 60 - $t.Length)) }
function Bad ($t) { Write-Host "  [!] $t" -ForegroundColor Red }
function Ok  ($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Note($t) { Write-Host "      $t" -ForegroundColor DarkGray }

$exeName = 'IJPSystem.Platform.HMI.exe'

# ── 설치 폴더 찾기 ─────────────────────────────────────────────────────────
Head "설치 폴더"
if (-not $AppDir) {
    $cands = @('C:\IJPSystem', 'D:\IJPSystem', 'C:\Program Files (x86)\IJPSystem', "$env:LOCALAPPDATA\Programs\IJPSystem") |
             Where-Object { Test-Path (Join-Path $_ $exeName) }
    if ($cands.Count -gt 0) { $AppDir = $cands[0] }
}
if (-not $AppDir -or -not (Test-Path (Join-Path $AppDir $exeName))) {
    Bad "설치 폴더를 못 찾았다. -AppDir 로 직접 지정할 것 (예: -AppDir `"D:\IJPSystem`")"
    return
}
Ok $AppDir

# ── 실행 중인 인스턴스 ────────────────────────────────────────────────────
# 단일 인스턴스 뮤텍스가 있어, 좀비 프로세스가 남아 있으면 두 번째 실행은 즉시 종료된다.
Head "실행 중인 프로세스"
$procs = Get-Process -Name 'IJPSystem.Platform.HMI' -ErrorAction SilentlyContinue
if ($procs) {
    Bad "이미 실행 중 — 단일 인스턴스 가드가 새 실행을 즉시 종료시킨다"
    $procs | Select-Object Id, StartTime, Responding | Format-Table -AutoSize | Out-String | Write-Host
    Note "응답 없음(Responding=False)이면 좀비다. 작업관리자에서 종료 후 다시 실행할 것."
} else { Ok "실행 중인 인스턴스 없음" }

# ── 차단(Zone.Identifier) ─────────────────────────────────────────────────
# 네트워크 공유·메일·압축으로 옮긴 DLL 은 Windows 가 차단 표시를 붙이고, .NET 이 로드를 거부한다.
Head "파일 차단 표시"
$blocked = Get-ChildItem $AppDir -Filter *.dll -File |
           Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }
if ($blocked) {
    Bad "차단된 DLL $($blocked.Count) 개 — 이것만으로도 기동이 실패한다"
    $blocked | Select-Object -First 10 -ExpandProperty Name | ForEach-Object { Note $_ }
    if ($Unblock) {
        $blocked | Unblock-File
        Ok "차단 해제 완료 — 앱을 다시 실행해 볼 것"
    } else {
        Note "해제하려면 이 스크립트를 -Unblock 옵션으로 다시 실행할 것"
    }
} else { Ok "차단된 파일 없음" }

# ── 교체한 DLL 상태 ───────────────────────────────────────────────────────
Head "교체 대상 DLL"
$targets = @(
    'IJPSystem.Platform.HMI.dll',
    'IJPSystem.Platform.Infrastructure.dll',
    'IJPSystem.Platform.Common.dll',
    'IJPSystem.Platform.Domain.dll',
    'IJPSystem.Platform.Application.dll',
    'IJPSystem.Drivers.Motion.dll',
    'IJPSystem.Drivers.Vision.dll',
    'IJPSystem.Drivers.IO.dll',
    'IJPSystem.Machines.Pulse.dll'
)
foreach ($t in $targets) {
    $p = Join-Path $AppDir $t
    if (Test-Path $p) {
        $f = Get-Item $p
        "{0,-42} {1,10:N0}B  {2}" -f $t, $f.Length, $f.LastWriteTime.ToString('MM-dd HH:mm') | Write-Host "      $_"
    } else { Bad "$t 없음" }
}

# 새 코드가 시리얼·Modbus 를 쓰므로, 이 세 개가 없으면 조명 코드에서 즉시 죽는다.
Head "필수 3rd-party 어셈블리"
foreach ($t in @('NModbus.dll', 'NModbus.Serial.dll', 'System.IO.Ports.dll')) {
    if (Test-Path (Join-Path $AppDir $t)) { Ok $t } else { Bad "$t 없음 — 조명/메니스커스 코드 로드 시 예외" }
}

# ── deps.json ─────────────────────────────────────────────────────────────
# .NET 은 폴더에 DLL 이 있어도 deps.json 에 없으면 로드하지 않는다.
Head "deps.json 등록 여부"
$deps = Join-Path $AppDir 'IJPSystem.Platform.HMI.deps.json'
if (Test-Path $deps) {
    $txt = Get-Content $deps -Raw
    foreach ($t in @('NModbus', 'System.IO.Ports', 'IJPSystem.Platform.Infrastructure')) {
        if ($txt -match [regex]::Escape($t)) { Ok "$t 등록됨" } else { Bad "$t 미등록 — 폴더에 있어도 로드 안 된다" }
    }
} else { Bad "deps.json 없음" }

# ── 설정 파일 ─────────────────────────────────────────────────────────────
Head "Config"
$cfg = Join-Path $AppDir 'Config\StrobeConfig.json'
if (Test-Path $cfg) {
    $s = Get-Content $cfg -Raw
    if ($s -match '"Devices"') { Ok "StrobeConfig.json 신규 형식" }
    else { Bad "StrobeConfig.json 이 구버전 — 새 DLL 과 짝이 안 맞는다. 같이 교체할 것" }
} else { Bad "Config\StrobeConfig.json 없음" }

# ── 앱 자체 로그 ──────────────────────────────────────────────────────────
Head "C:\Logs 최근 오류"
if (Test-Path 'C:\Logs') {
    $log = Get-ChildItem 'C:\Logs' -Filter *.txt -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        Note "$($log.Name)  ($($log.LastWriteTime))"
        $hits = Select-String -Path $log.FullName -Pattern 'FATAL|Exception|STARTUP|BANNER' |
                Select-Object -Last 25
        if ($hits) { $hits | ForEach-Object { Write-Host "      $($_.Line)" } }
        else { Note "FATAL/Exception 항목 없음" }
    } else { Bad "로그 파일 없음 — 앱이 로거 초기화 전에 죽었다는 뜻" }
} else { Bad "C:\Logs 폴더 없음 — 앱이 시작조차 못 했다" }

# ── Windows 이벤트 로그 ───────────────────────────────────────────────────
# 관리 예외 이전에 죽으면(어셈블리 로드 실패 등) 흔적은 여기에만 남는다.
Head "Windows 이벤트 로그 (.NET Runtime / Application Error)"
try {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-2) } -ErrorAction Stop |
          Where-Object { $_.ProviderName -match '\.NET Runtime|Application Error' -and $_.Message -match 'IJPSystem' } |
          Select-Object -First 3
    if ($ev) {
        foreach ($e in $ev) {
            Write-Host ""
            Write-Host "  [$($e.TimeCreated)] $($e.ProviderName)" -ForegroundColor Yellow
            ($e.Message -split "`n" | Select-Object -First 20) | ForEach-Object { Write-Host "      $_" }
        }
    } else { Note "관련 항목 없음" }
} catch { Note "이벤트 로그를 읽지 못했다: $($_.Exception.Message)" }

# ── 호스트 추적 ───────────────────────────────────────────────────────────
# 위에서 원인이 안 나오면, 이걸로 어느 어셈블리 해석에서 실패하는지 정확히 찍힌다.
if ($Trace) {
    Head "호스트 추적 실행"
    $traceFile = Join-Path $env:TEMP 'ijp_host_trace.log'
    Remove-Item $traceFile -ErrorAction SilentlyContinue
    $env:COREHOST_TRACE = '1'
    $env:COREHOST_TRACEFILE = $traceFile
    Note "앱을 1회 실행한다. 창이 뜨면 닫고, 안 뜨면 5초 기다린다."
    $p = Start-Process (Join-Path $AppDir $exeName) -WorkingDirectory $AppDir -PassThru
    $p.WaitForExit(15000) | Out-Null
    if (-not $p.HasExited) { Note "앱이 살아 있다 — 정상 기동으로 보인다" }
    $env:COREHOST_TRACE = $null
    $env:COREHOST_TRACEFILE = $null
    if (Test-Path $traceFile) {
        Ok "추적 로그: $traceFile"
        $err = Select-String -Path $traceFile -Pattern 'error|failed|not found|does not exist' | Select-Object -Last 15
        if ($err) { $err | ForEach-Object { Write-Host "      $($_.Line)" -ForegroundColor Red } }
    } else { Bad "추적 로그가 생성되지 않았다 — exe 가 아예 시작되지 못했다" }
}

Write-Host ""
Write-Host "완료. 빨간 [!] 줄을 그대로 알려줄 것." -ForegroundColor Cyan
