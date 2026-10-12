# Packages ProPS1 Studio release artifacts:
#   publish\ProPS1Studio-portable-<version>-x64.zip
#   publish\ProPS1Studio-Setup-<version>-x64.exe
param(
    [string]$Version = "1.0.1",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot          # repo root
$app = Join-Path $root "src\ProPS1Studio.App\ProPS1Studio.App.csproj"
$publishDir = Join-Path $root "publish\app"

if (-not $SkipPublish) {
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    Write-Host "== Publishing (Release, win-x64, self-contained) =="
    & $dotnet publish $app -c Release -r win-x64 --self-contained true -o $publishDir -m:1 --nologo "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:FileVersion=$Version.0"
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $publishDir "ProPS1Studio.exe"))) { throw "ProPS1Studio.exe missing in $publishDir" }

Write-Host "== Creating portable zip =="
$zip = Join-Path $root "publish\ProPS1Studio-portable-$Version-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "== Compiling installer =="
$iscc = $null
foreach ($c in @(
    "$env:LOCALAPPDATA\Programs\Inno\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)) { if (Test-Path $c) { $iscc = $c; break } }
if (-not $iscc) { throw "ISCC.exe not found - install Inno Setup 6" }

$iss = Join-Path $PSScriptRoot "installer.iss"
& $iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Join-Path $root "publish\ProPS1Studio-Setup-$Version-x64.exe"
if (-not (Test-Path $setup)) { throw "installer not produced at $setup" }

Write-Host "== Artifacts =="
foreach ($f in @($zip, $setup)) {
    $i = Get-Item $f
    $h = (Get-FileHash $f -Algorithm SHA256).Hash
    "{0}`n  {1:N0} bytes`n  SHA256 {2}" -f $i.Name, $i.Length, $h
}
