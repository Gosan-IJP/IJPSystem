<#
    Build-Hotfix.ps1 — 실장에 보낼 _hotfix 번들을 만든다. (개발 PC 에서 실행)

        pwsh -File Tools\Deploy\Build-Hotfix.ps1
        pwsh -File Tools\Deploy\Build-Hotfix.ps1 -Config DropWatcherConfig.json,StrobeConfig.json

    <b>왜 스크립트여야 하는가</b>: bin 밑에는 구성이 바뀌면서 버려진 출력 폴더가 남는다.
    손으로 고르면 그중 하나를 집게 되고, 복사한 파일은 수정시각만 새로 찍혀 방금 빌드한 것처럼
    보인다. 실제로 사라진 win-x86 폴더의 7/22 산출물을 며칠 배포했고, 그 사이 다른 DLL 은
    최신이라 짝이 어긋나 MethodNotFound 로 앱이 죽었다(2026-08-07).

    그래서 여기서는 <b>퍼블리시를 새로 돌리고 그 산출물만</b> 쓴다. 설치본과 같은 구성(Release,
    win-x86, self-contained)이라 실장에 그대로 얹을 수 있고, 한 번의 퍼블리시에서 나온 파일이라
    조합이 어긋날 수 없다. 마지막에 어셈블리에 박힌 빌드 시각이 전부 같은지 확인한다.
#>

param(
    [string[]] $Config,          # _hotfix\Config 에 넣을 설정 파일 이름들(생략 시 설정 없음)
    [switch]   $SkipPublish      # 직전 퍼블리시 산출물을 그대로 쓴다(빠른 재구성)
)

$ErrorActionPreference = 'Stop'
function Head($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Bad ($t) { Write-Host "  [!] $t" -ForegroundColor Red }
function Ok  ($t) { Write-Host "  [OK] $t" -ForegroundColor Green }
function Note($t) { Write-Host "      $t" -ForegroundColor DarkGray }

$repo    = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$proj    = Join-Path $repo 'IJPSystem.Platform.HMI\IJPSystem.Platform.HMI.csproj'
$pubDir  = Join-Path $repo 'Installer\publish'
$outDir  = Join-Path $repo '_hotfix'

# ── 퍼블리시 ───────────────────────────────────────────────────────────────
if (-not $SkipPublish) {
    Head "퍼블리시 (Release · win-x86 · self-contained)"
    if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
    dotnet publish $proj -c Release -r win-x86 --self-contained true `
        -p:PublishSingleFile=false -o $pubDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { Bad "퍼블리시 실패 (exit $LASTEXITCODE)"; return }
    Ok $pubDir
}
elseif (-not (Test-Path $pubDir)) { Bad "퍼블리시 산출물이 없다: $pubDir"; return }

# ── DLL 수집 ───────────────────────────────────────────────────────────────
# 자체 어셈블리만 담는다. 3rd-party/런타임 DLL 은 설치본에 이미 있고, 버전이 바뀌었다면
# DLL 교체가 아니라 설치 파일로 배포해야 한다(deps.json 이 같이 바뀌어야 하므로).
Head "DLL"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Get-ChildItem $outDir -Filter *.dll -File | Remove-Item -Force
$dlls = @(Get-ChildItem $pubDir -Filter 'IJPSystem.*.dll' -File)
if ($dlls.Count -eq 0) { Bad "퍼블리시 산출물에 IJPSystem.*.dll 이 없다"; return }
$dlls | Copy-Item -Destination $outDir -Force
$dlls | ForEach-Object { Ok $_.Name }

# ── 빌드 시각 대조 ─────────────────────────────────────────────────────────
# 전부 같아야 한다. 다르면 서로 다른 빌드의 파일이 섞인 것이고, 그 조합은 실장에서
# MethodNotFound 로 죽는다 — 여기서 걸러야 현장에서 안 겪는다.
Head "빌드 시각"
$stamps = @{}
foreach ($d in $dlls) {
    $info = (Get-Item (Join-Path $outDir $d.Name)).VersionInfo.ProductVersion
    $plus = $info.IndexOf('+')
    $rev  = if ($plus -ge 0) { $info.Substring($plus + 1) } else { '(없음)' }
    $stamps[$d.Name] = $rev
}
$distinct = @($stamps.Values | Sort-Object -Unique)
if ($distinct.Count -eq 1 -and $distinct[0] -ne '(없음)') {
    Ok "전부 $($distinct[0])"
} else {
    Bad "빌드 시각이 섞였다 — 이 번들을 배포하면 안 된다"
    $stamps.GetEnumerator() | Sort-Object Name | ForEach-Object { Note ("{0,-42} {1}" -f $_.Key, $_.Value) }
    return
}

# ── 설정 파일 ──────────────────────────────────────────────────────────────
# 지정한 것만 넣는다. 폴더째 두면 실장에서 -Config 로 축·스트로브 설정까지 딸려간다.
Head "설정 파일"
$cfgOut = Join-Path $outDir 'Config'
New-Item -ItemType Directory -Force -Path $cfgOut | Out-Null
Get-ChildItem $cfgOut -Filter *.json -File | Remove-Item -Force
if ($Config) {
    foreach ($name in $Config) {
        $src = Join-Path $repo ('Config\' + $name)
        if (Test-Path $src) { Copy-Item $src $cfgOut -Force; Ok $name }
        else { Bad "$name 없음 — Config 폴더 확인" }
    }
} else { Note "없음 (필요하면 -Config DropWatcherConfig.json 처럼 지정할 것)" }

# ── 적용 스크립트 동봉 ─────────────────────────────────────────────────────
Copy-Item (Join-Path $PSScriptRoot 'Apply-Hotfix.ps1') $outDir -Force

Head "완료"
Ok "$outDir"
Write-Host ""
# 설정을 넣지 않았으면 -Config 를 붙이라고 안내하면 안 된다 — 현장에서 그대로 복사해 실행하면
# 있지도 않은 설정을 덮으려 들고, 실제로 축·스트로브 설정이 날아간 적이 있다(2026-08-07).
$cfgArg = if ($Config) { ' -Config' } else { '' }
Write-Host "  실장 PC 에서:" -ForegroundColor Yellow
Write-Host "      powershell -ExecutionPolicy Bypass -File C:\hotfix\Apply-Hotfix.ps1$cfgArg -Force -Restart" -ForegroundColor White
Write-Host "  앱 제목이 이렇게 떠야 한다:  Pulse - build $($distinct[0])" -ForegroundColor DarkGray
