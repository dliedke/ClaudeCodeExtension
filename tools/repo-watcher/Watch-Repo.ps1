# PR / issue watcher for the ClaudeCodeExtension GitHub repo.
# Installed and scheduled by Install-RepoWatcher.ps1 (see docs/SKILLS.md -> Watch PRs and issues).
# Runs from Task Scheduler:  Watch-Repo.ps1 -Kind pr|issue -Repo owner/name -RepoDir <clone> -Claude <claude.exe>
# The check uses only gh/git (no tokens); Claude Code (Opus, high effort) is started only when an
# open PR (or a new commit on one) or a new open issue hasn't been handled yet.

param(
    [ValidateSet('pr', 'issue')][string]$Kind = 'pr',
    [string]$Repo    = 'dliedke/ClaudeCodeExtension',
    [Parameter(Mandatory)][string]$RepoDir,
    [Parameter(Mandatory)][string]$Claude
)

$ErrorActionPreference = 'Stop'

$Root        = $PSScriptRoot
$Prompt      = Join-Path $Root "prompt-$Kind.md"
$StateFile   = Join-Path $Root "state-$Kind.json"
$LogDir      = Join-Path $Root 'logs'
$MainLog     = Join-Path $LogDir 'watcher.log'
$MaxAttempts = 3

New-Item -ItemType Directory -Force $LogDir | Out-Null

function Write-Log([string]$msg) {
    Add-Content -Path $MainLog -Value ("{0:yyyy-MM-dd HH:mm:ss}  [{1}] {2}" -f (Get-Date), $Kind, $msg)
}

. (Join-Path $Root 'Notify.ps1')

# Emails the merged PRs / fixed issues from Claude's results file, if email is set up.
function Send-Results([string]$resultFile) {
    try {
        $config = Get-WatcherMailConfig $Root
        if (-not $config) { return }
        if (-not (Test-Path $resultFile)) { Write-Log 'No results file; no email sent.'; return }
        $done = @((Get-Content $resultFile -Raw | ConvertFrom-Json) |
            Where-Object { $_.number -and $_.outcome -in 'merged', 'fixed' })
        if ($done.Count -eq 0) { return }

        $noun  = if ($Kind -eq 'pr') { 'PR' } else { 'Issue' }
        $path  = if ($Kind -eq 'pr') { 'pull' } else { 'issues' }
        $lines = foreach ($r in $done) {
            $version = if ($r.version) { "published v$($r.version)" } else { 'not published' }
            "$noun #$($r.number) $($r.outcome), $version`n$($r.summary)`nhttps://github.com/$Repo/$path/$($r.number)`n"
        }
        $first   = $done[0]
        $subject = if ($done.Count -eq 1) {
            "[ClaudeCodeExtension] $noun #$($first.number) $($first.outcome)" + $(if ($first.version) { ", v$($first.version) published" })
        } else {
            "[ClaudeCodeExtension] $($done.Count) ${noun}s handled"
        }
        Send-WatcherMail $config $subject (($lines -join "`n") + "`nRun log: $runLog")
        Write-Log "Emailed $($config.To): $subject"
    }
    catch {
        Write-Log "Email failed: $($_.Exception.Message)"
    }
}

# One run at a time across PRs and issues: both edit the repo and publish, and a publish can
# outlast the polling interval. A busy lock just means this tick is skipped.
$mutex = New-Object System.Threading.Mutex($false, 'Global\ClaudeCodeExtensionRepoWatcher')
if (-not $mutex.WaitOne(0)) { exit 0 }

try {
    # --- Free check -----------------------------------------------------------------------
    if ($Kind -eq 'pr') {
        $json = & gh pr list --repo $Repo --state open --json number,headRefOid,title,author 2>&1
    } else {
        $json = & gh issue list --repo $Repo --state open --limit 100 --json number,title,author 2>&1
    }
    if ($LASTEXITCODE -ne 0) { Write-Log "gh failed: $json"; exit 0 }
    # Filter blanks: Windows PowerShell turns "[]" into one empty object.
    $items = @(($json | Out-String | ConvertFrom-Json) | Where-Object { $_.number })
    if ($items.Count -eq 0) { exit 0 }

    # PRs are keyed by head commit, so new pushes to a handled PR are picked up again.
    # Issues are handled once; follow-up comments or reopens need a manual look.
    function Get-Key($item) {
        if ($Kind -eq 'pr') { "$($item.number):$($item.headRefOid)" } else { "$($item.number)" }
    }

    # state-<kind>.json: { "handled": { "<key>": true }, "attempts": { "<key>": 1 } }
    $state = @{ handled = @{}; attempts = @{} }
    if (Test-Path $StateFile) {
        $raw = Get-Content $StateFile -Raw | ConvertFrom-Json
        foreach ($p in $raw.handled.PSObject.Properties)  { $state.handled[$p.Name]  = $true }
        foreach ($p in $raw.attempts.PSObject.Properties) { $state.attempts[$p.Name] = [int]$p.Value }
    }

    $pending = @($items | Where-Object { -not $state.handled.ContainsKey((Get-Key $_)) })
    if ($pending.Count -eq 0) { exit 0 }
    $numbers = ($pending | ForEach-Object { "#$($_.number)" }) -join ', '

    # --- Don't touch the repo while you're working in it ----------------------------------
    $branch = (& git -C $RepoDir rev-parse --abbrev-ref HEAD).Trim()
    $dirty  = & git -C $RepoDir status --porcelain
    if ($branch -ne 'master' -or $dirty) {
        Write-Log ("{0} waiting, but the repo is busy (branch '{1}', dirty={2}); will retry." -f
            $numbers, $branch, [bool]$dirty)
        exit 0
    }

    # --- Work to do: start Claude Code ----------------------------------------------------
    $list   = ($pending | ForEach-Object { "#$($_.number) '$($_.title)' by $($_.author.login)" }) -join '; '
    $runLog = Join-Path $LogDir ("run-{0}-{1:yyyyMMdd-HHmmss}.log" -f $Kind, (Get-Date))
    Write-Log "Starting Claude for: $list (log: $(Split-Path $runLog -Leaf))"

    # Claude reports each item's outcome here; merged PRs and fixed issues are emailed.
    $resultFile = [IO.Path]::ChangeExtension($runLog, '.results.json')
    $text = (Get-Content $Prompt -Raw) + @"


Items to handle in this run: $numbers

When you are done, write a JSON array with one entry per item to $resultFile, for example:
[{"number": 220, "outcome": "merged", "version": "266.0", "summary": "One user-facing sentence."}]
outcome is one of: merged, fixed, answered, asked-for-details, deferred, skipped-unsafe, failed.
version is the published version, or null when nothing was published.
"@
    $promptFile = [IO.Path]::ChangeExtension($runLog, '.prompt.md')
    Set-Content $promptFile $text -Encoding utf8

    # This script runs hidden; the Claude run gets its own visible console window so you can see
    # it working (and don't shut down mid-publish). -Wait keeps the lock until the window closes.
    $title = "$(if ($Kind -eq 'pr') { 'PR' } else { 'issue' }) $numbers"
    $runArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Root 'Run-Claude.ps1')`" " +
               "-Claude `"$Claude`" -PromptFile `"$promptFile`" -RunLog `"$runLog`" -Title `"$title`""
    $proc = Start-Process powershell.exe -ArgumentList $runArgs -WorkingDirectory $RepoDir -Wait -PassThru
    $exit = $proc.ExitCode
    Remove-Item $promptFile -Force -ErrorAction SilentlyContinue

    foreach ($item in $pending) {
        $key = Get-Key $item
        if ($exit -eq 0) {
            $state.handled[$key] = $true
            $state.attempts.Remove($key)
        } else {
            $state.attempts[$key] = 1 + [int]$state.attempts[$key]
            # Stop retrying (and spending tokens) on an item that keeps failing.
            if ($state.attempts[$key] -ge $MaxAttempts) { $state.handled[$key] = $true }
        }
    }
    $state | ConvertTo-Json -Depth 4 | Set-Content $StateFile -Encoding utf8
    Write-Log "Claude finished with exit code $exit."

    Send-Results $resultFile

    # Keep the last 30 runs per kind.
    Get-ChildItem $LogDir -Filter "run-$Kind-*.log" | Sort-Object Name -Descending |
        Select-Object -Skip 30 | ForEach-Object {
            Remove-Item $_.FullName, ([IO.Path]::ChangeExtension($_.FullName, '.results.json')) -Force -ErrorAction SilentlyContinue
        }
}
catch {
    Write-Log "Error: $($_.Exception.Message)"
}
finally {
    $mutex.ReleaseMutex()
}
