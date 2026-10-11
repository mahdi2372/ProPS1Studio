$exe = "C:\Users\mahdi\Desktop\ps1 p\ProPS1Studio\src\ProPS1Studio.App\bin\Debug\net8.0-windows\ProPS1Studio.exe"
$logs = "$env:LOCALAPPDATA\ProPS1Studio\logs"

$env:PROPS_AUTH_BYPASS = "1"   # skip the KeyAuth login gate for this smoke check
$p = Start-Process -FilePath $exe -PassThru -ErrorAction Stop
for ($i = 1; $i -le 30; $i++) {
    Start-Sleep -Milliseconds 500
    try { $p.Refresh() } catch { }
    $state = if ($p.HasExited) { "EXITED code=$($p.ExitCode)" } else { "alive" }
    $h = $p.MainWindowHandle
    $t = $p.MainWindowTitle
    $logsExists = Test-Path $logs
    $logFiles = if ($logsExists) { (Get-ChildItem $logs -ErrorAction SilentlyContinue | Measure-Object).Count } else { 0 }
    Write-Host ("t={0,2}: {1} handle={2} title='{3}' logsDir={4} logFiles={5}" -f ($i * 0.5), $state, $h, $t, $logsExists, $logFiles)
    if ($p.HasExited) { break }
}

if (-not $p.HasExited) {
    Stop-Process -Id $p.Id -Force
    Write-Host "STOPPED_BY_US"
}

if (Test-Path $logs) {
    Get-ChildItem $logs | ForEach-Object {
        Write-Host "LOGFILE: $($_.FullName) size=$($_.Length)"
        Get-Content $_.FullName -Tail 25 | ForEach-Object { Write-Host "  $_" }
    }
} else {
    Write-Host "NO_LOGS_DIR"
}
