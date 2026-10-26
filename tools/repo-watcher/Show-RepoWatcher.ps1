# Live status of the repo watcher (see docs/SKILLS.md -> Watch PRs and issues).
#   .\Show-RepoWatcher.ps1   # task state, last/next run, then follows watcher.log until Ctrl+C
# The checks themselves run hidden; each one writes a line to watcher.log. When there is work,
# the Claude run opens its own console window with the live steps.

$InstallDir = Join-Path $env:USERPROFILE '.claude\repo-watcher'
$MainLog    = Join-Path $InstallDir 'logs\watcher.log'

$Host.UI.RawUI.WindowTitle = 'Claude repo watcher - status'

foreach ($name in 'ClaudeCodeExtension PR Watcher', 'ClaudeCodeExtension Issue Watcher') {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if (-not $task) { Write-Host "${name}: not installed (run Install-RepoWatcher.ps1)" -ForegroundColor Red; continue }
    $info  = $task | Get-ScheduledTaskInfo
    $color = if ($task.State -eq 'Running') { 'Yellow' } elseif ($task.State -eq 'Ready') { 'Green' } else { 'Red' }
    Write-Host ("{0,-34} {1,-8}  last {2:HH:mm:ss}  next {3:HH:mm:ss}" -f $name, $task.State, $info.LastRunTime, $info.NextRunTime) -ForegroundColor $color
}

$runs = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
    Where-Object { $_.CommandLine -match 'Run-Claude\.ps1' }
if ($runs) { Write-Host "Claude is running now (see its 'Claude repo watcher' window)." -ForegroundColor Yellow }

Write-Host "`nFollowing $MainLog (Ctrl+C to stop)`n" -ForegroundColor DarkGray
if (-not (Test-Path $MainLog)) { New-Item -ItemType File -Force $MainLog | Out-Null }
Get-Content $MainLog -Tail 20 -Wait | ForEach-Object {
    $color = if ($_ -match 'Error|failed') { 'Red' } elseif ($_ -match 'Starting Claude|Emailed') { 'Yellow' } else { 'Gray' }
    Write-Host $_ -ForegroundColor $color
}
