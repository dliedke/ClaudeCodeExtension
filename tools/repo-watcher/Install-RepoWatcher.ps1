# Installs (or removes) the PR and issue watchers as two Windows scheduled tasks.
# See docs/SKILLS.md -> Watch PRs and issues.
#
#   .\Install-RepoWatcher.ps1              # install / update, poll every 10 minutes
#   .\Install-RepoWatcher.ps1 -Minutes 15  # different interval
#   .\Install-RepoWatcher.ps1 -Uninstall   # remove both tasks (keeps logs and state)
#   .\Install-RepoWatcher.ps1 -SetupEmail you@gmail.com   # email every PR / issue outcome
#
# -SetupEmail asks for a Gmail app password (https://myaccount.google.com/apppasswords, needs
# 2-step verification), stores it DPAPI-encrypted in the install folder and sends a test email.
# Run it in a normal PowerShell window: it reads the password from the keyboard.
#
# The scripts are copied out of the repo on purpose: the tasks run the copy in
# %USERPROFILE%\.claude\repo-watcher, so a merged PR can't change what the unattended agent runs.
# Re-run this script after editing the files in tools/repo-watcher.

param(
    [string]$Repo    = 'dliedke/ClaudeCodeExtension',
    [string]$RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [int]$Minutes    = 10,
    [switch]$Uninstall,
    [string]$SetupEmail
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

if ($SetupEmail) {
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'Notify.ps1') $InstallDir -Force
    . (Join-Path $InstallDir 'Notify.ps1')
    $password = Read-Host "Gmail app password for $SetupEmail" -AsSecureString
    New-Object System.Management.Automation.PSCredential($SetupEmail, $password) |
        Export-Clixml (Join-Path $InstallDir 'email-credential.xml')
    @{ To = $SetupEmail; From = $SetupEmail } | ConvertTo-Json | Set-Content (Join-Path $InstallDir 'email.json') -Encoding utf8
    Send-WatcherMail (Get-WatcherMailConfig $InstallDir) '[ClaudeCodeExtension] Repo watcher email test' `
        'Email notifications are set up. You will get one email per watcher run with the outcome of each PR or issue (merged, fixed, rejected, deferred, failed, ...).'
    Write-Host "Test email sent to $SetupEmail. To turn emails off, delete email.json in $InstallDir."
    return
}

$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) { throw 'claude was not found on PATH. Install Claude Code first.' }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) was not found on PATH.' }
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in. Run "gh auth login" first.' }

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Watch-Repo.ps1'), (Join-Path $PSScriptRoot 'Run-Claude.ps1'), (Join-Path $PSScriptRoot 'Notify.ps1'), (Join-Path $PSScriptRoot 'prompt-*.md') $InstallDir -Force

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
Write-Host "  email:  $(if (Test-Path (Join-Path $InstallDir 'email.json')) { 'on' } else { 'off (run with -SetupEmail you@gmail.com)' })"
