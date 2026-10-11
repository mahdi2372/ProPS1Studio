param(
    [string]$Page = "dashboard",
    [string]$OutDir = "C:\Users\mahdi\Desktop\ps1 p\ProPS1Studio\tools\shots"
)
$exe = "C:\Users\mahdi\Desktop\ps1 p\ProPS1Studio\src\ProPS1Studio.App\bin\Debug\net8.0-windows\ProPS1Studio.exe"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$out = Join-Path $OutDir "page-$Page.png"
if (Test-Path $out) { Remove-Item $out -Force }

$env:PROPS_SELFTEST = "1"
$env:PROPS_SELFTEST_PAGE = $Page
$env:PROPS_SELFTEST_OUT = $out

$p = Start-Process -FilePath $exe -PassThru
$exited = $p.WaitForExit(20000)
if (-not $exited) {
    Write-Host "TIMEOUT - killing"
    Stop-Process -Id $p.Id -Force
    exit 1
}
Write-Host "EXIT=$($p.ExitCode)"
if ((Test-Path $out) -and $p.ExitCode -eq 0) { Write-Host "SAVED $out" } else { Write-Host "NO_OUTPUT"; exit 1 }
