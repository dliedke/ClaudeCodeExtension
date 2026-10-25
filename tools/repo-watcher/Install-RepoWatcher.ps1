# Installs (or removes) the PR and issue watchers as two Windows scheduled tasks.
# See docs/SKILLS.md -> Watch PRs and issues.
#
#   .\Install-RepoWatcher.ps1              # install / update, poll every 10 minutes
#   .\Install-RepoWatcher.ps1 -Minutes 15  # different interval
#   .\Install-RepoWatcher.ps1 -Uninstall   # remove both tasks (keeps logs and state)
#
# The scripts are copied out of the repo on purpose: the tasks run the copy in
# %USERPROFILE%\.claude\repo-watcher, so a merged PR can't change what the unattended agent runs.
# Re-run this script after editing the files in tools/repo-watcher.

param(
    [string]$Repo    = 'dliedke/ClaudeCodeExtension',
    [string]$RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [int]$Minutes    = 10,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:USERPROFILE '.claude\repo-watcher'
$Tasks = [ordered]@{ pr = 'ClaudeCodeExtension PR Watcher'; issue = 'ClaudeCodeExtension Issue Watcher' }

if ($Uninstall) {
    foreach ($name in $Tasks.Values) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
    Write-Host "Removed both tasks. Logs and state are still in $InstallDir."
    return
}

$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) { throw 'claude was not found on PATH. Install Claude Code first.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) was not found on PATH.' }
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in. Run "gh auth login" first.' }

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Watch-Repo.ps1'), (Join-Path $PSScriptRoot 'prompt-*.md') $InstallDir -Force

$script = Join-Path $InstallDir 'Watch-Repo.ps1'
$offset = 1
foreach ($kind in $Tasks.Keys) {
    # conhost --headless keeps a console (publish.cmd needs one) without flashing a window.
    $arguments = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`" " +
                 "-Kind $kind -Repo $Repo -RepoDir `"$RepoDir`" -Claude `"$claude`""
    $action    = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument $arguments -WorkingDirectory $InstallDir
    # Stagger the two tasks so they don't fight over the shared lock on the same tick.
    $trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes($offset) `
                     -RepetitionInterval (New-TimeSpan -Minutes $Minutes)
    $settings  = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
                     -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 3)
    # Interactive logon: runs only while you're signed in, with your gh/git/Marketplace credentials.
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $Tasks[$kind] -Action $action -Trigger $trigger -Settings $settings `
        -Principal $principal -Force `
        -Description "Checks for open ${kind}s with gh (no tokens); starts Claude Code (Opus, high effort) only when there is work. Installed from $PSScriptRoot." |
        Out-Null
    $offset += [math]::Max(1, [int]($Minutes / 2))
}

Write-Host "Installed to $InstallDir"
Write-Host "  repo:   $Repo ($RepoDir)"
Write-Host "  claude: $claude"
Write-Host "  tasks:  $($Tasks.Values -join ', ') - every $Minutes minutes"
Write-Host "  log:    $(Join-Path $InstallDir 'logs\watcher.log')"
