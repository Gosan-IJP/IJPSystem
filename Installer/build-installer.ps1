<#
    build-installer.ps1
    IJPSystem HMI 설치 파일(setup.exe) 생성 스크립트.

    동작:
      1) HMI 프로젝트를 self-contained(win-x64)로 publish → Installer\publish
      2) Inno Setup 컴파일러(ISCC)로 IJPSystem.iss 컴파일 → Installer\dist\IJPSystem_Setup_*.exe

    사전 준비:
      - .NET 8 SDK
      - Inno Setup 6 (https://jrsoftware.org/isdl.php)
      - (선택) 벤더 네이티브 DLL 을 Installer\native\ 에 복사(ComiEcatSdk.dll 등)

    사용:  PS> .\build-installer.ps1
#>
$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj    = Join-Path $here '..\IJPSystem.Platform.HMI\IJPSystem.Platform.HMI.csproj'
$pubDir  = Join-Path $here 'publish'

# 1) publish (self-contained: 대상 PC에 .NET 설치 불필요) --------------------
Write-Host '[1/2] dotnet publish (win-x64, self-contained)...' -ForegroundColor Cyan
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $pubDir
if ($LASTEXITCODE -ne 0) { throw "publish 실패 (exit $LASTEXITCODE)" }

# 2) Inno Setup 컴파일 -------------------------------------------------------
Write-Host '[2/2] Inno Setup 컴파일...' -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'ISCC.exe 를 찾지 못했습니다. Inno Setup 6 을 설치하세요: https://jrsoftware.org/isdl.php'
}

& $iscc (Join-Path $here 'IJPSystem.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 컴파일 실패 (exit $LASTEXITCODE)" }

$out = Join-Path $here 'dist'
Write-Host "완료. 설치 파일: $out" -ForegroundColor Green
Get-ChildItem $out -Filter *.exe | Select-Object Name, Length, LastWriteTime
