# Set-LargeAddressAware.ps1
#
# 32비트 apphost(.exe)의 PE 헤더에 LARGE_ADDRESS_AWARE(0x0020) 를 세운다.
#
# ★ 왜 필요한가
#   이 앱은 win-x86 로 발행한다(Comizoa ComiEcatSdk.dll 이 32비트라 선택의 여지가 없다).
#   그런데 .NET SDK 의 x86 apphost 는 이 플래그를 켜 주지 않는다 — 그러면 64비트 Windows
#   위에서도 프로세스 주소공간이 2GB 로 잘린다. 4GB 를 쓰려면 이 비트를 세워야 한다.
#
#   2026-09-03 실장에서 오토런 중 WPF 가 네이티브 할당에 실패해(0x80070008 in
#   GlyphTypeface.GlyphMetrics) 렌더 스레드가 죽었다(UCEERR_RENDERTHREADFAILURE).
#   관리 힙이면 OutOfMemoryException 이 났을 것이다 — 네이티브가 못 잡았다는 건
#   주소공간이 없다는 뜻이고, 원인은 이 2GB 천장이었다.
#
# ★ 왜 빌드 단계인가
#   MSBuild 에는 이 플래그를 켜는 속성이 없다. 발행 후 헤더를 직접 고치는 수밖에 없고,
#   손으로 하면 잊는다 — 발행할 때마다 자동으로 걸리게 csproj 가 이 스크립트를 부른다.
#
# 서명은 하지 않으므로 헤더 수정이 서명을 깨뜨릴 일은 없다. 이미 세워져 있으면 아무것도 안 한다.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Path
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Set-LargeAddressAware: 파일이 없습니다 — $Path"
}

$bytes = [System.IO.File]::ReadAllBytes($Path)

# DOS 헤더 → PE 서명 위치
if ($bytes.Length -lt 0x40) { throw "Set-LargeAddressAware: PE 파일이 아닙니다(너무 작음) — $Path" }
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
if ($peOffset -le 0 -or ($peOffset + 24) -ge $bytes.Length) {
    throw "Set-LargeAddressAware: PE 헤더 위치가 이상합니다 — $Path"
}
if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
    throw "Set-LargeAddressAware: PE 서명이 없습니다 — $Path"
}

# COFF 헤더: Machine(2) NumberOfSections(2) TimeDateStamp(4) PointerToSymbolTable(4)
#            NumberOfSymbols(4) SizeOfOptionalHeader(2) Characteristics(2)
$machineOffset = $peOffset + 4
$charsOffset   = $peOffset + 4 + 18

$machine = [BitConverter]::ToUInt16($bytes, $machineOffset)
$chars   = [BitConverter]::ToUInt16($bytes, $charsOffset)

# x64/ARM64 는 애초에 주소공간이 넉넉하고 이 비트가 의미 없다 — 건드리지 않는다.
if ($machine -ne 0x014C) {
    # 괄호 필수 — 없으면 -f 가 Write-Host 의 -ForegroundColor 로 해석된다.
    Write-Host ("  LAA: 건너뜀 — 32비트 이미지가 아닙니다 (Machine=0x{0:X4}) : {1}" -f $machine, $Path)
    exit 0
}

if (($chars -band 0x0020) -ne 0) {
    Write-Host ("  LAA: 이미 설정됨 (Characteristics=0x{0:X4}) : {1}" -f $chars, $Path)
    exit 0
}

$newChars = [uint16]($chars -bor 0x0020)
[BitConverter]::GetBytes($newChars).CopyTo($bytes, $charsOffset)
[System.IO.File]::WriteAllBytes($Path, $bytes)

# 되읽어 확인 — 쓰기가 먹었는지 믿지 말고 본다.
$verify = [System.IO.File]::ReadAllBytes($Path)
$after  = [BitConverter]::ToUInt16($verify, $charsOffset)
if (($after -band 0x0020) -eq 0) {
    throw "Set-LargeAddressAware: 설정 후에도 비트가 없습니다 — $Path"
}

Write-Host ("  LAA: 설정 완료 0x{0:X4} -> 0x{1:X4} (2GB -> 4GB) : {2}" -f $chars, $after, $Path)
