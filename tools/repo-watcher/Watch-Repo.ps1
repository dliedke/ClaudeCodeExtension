# PR / issue watcher for the ClaudeCodeExtension GitHub repo.
# Installed and scheduled by Install-RepoWatcher.ps1 (see docs/SKILLS.md -> Watch PRs and issues).
# Runs from Task Scheduler:  Watch-Repo.ps1 -Kind pr|issue -Repo owner/name -RepoDir <clone> -Claude <claude.exe>
# The check uses only gh/git (no tokens); Claude Code (Opus, high effort) is started only when an
# open PR (or a new commit on one) or a new open issue hasn't been handled yet.

param(
    [ValidateSet('pr', 'issue')][string]$Kind = 'pr',
    [string]$Repo    = 'dliedke/ClaudeCodeExtension',
    [Parameter(Mandatory)][string]$RepoDir,
    [Parameter(Mandatory)][string]$Claude,
    [switch]$Wsl
)

$ErrorActionPreference = 'Stop'
# WSL programs write UTF-8; without this, non-ASCII titles from gh come back garbled.
if ($Wsl) { [Console]::OutputEncoding = [Text.Encoding]::UTF8 }

function Invoke-Gh {
    if ($Wsl) { & wsl.exe -e gh @args } else { & gh @args }
}

$Root        = $PSScriptRoot
$Prompt      = Join-Path $Root "prompt-$Kind.md"
$StateFile   = Join-Path $Root "state-$Kind.json"
$LogDir      = Join-Path $Root 'logs'
$MainLog     = Join-Path $LogDir 'watcher.log'
$MaxAttempts = 3
# The watcher can run on several machines against the same repo. Before Claude starts on an item,
# this run posts a claim comment on it; other machines skip the item while a live claim exists.
$ClaimMarker = '<!-- repo-watcher-claim -->'
$ClaimHours  = 4      # longer than the 3-hour task limit, so a crashed run's claim expires
$claimed     = @{}    # item number -> id of this run's claim comment

New-Item -ItemType Directory -Force $LogDir | Out-Null
# Every tick logs one line (so Show-RepoWatcher.ps1 shows it's alive); keep the last 2000.
if ((Test-Path $MainLog) -and (Get-Item $MainLog).Length -gt 400KB) {
    $keep = Get-Content $MainLog -Tail 2000
    Set-Content $MainLog $keep
}

function Write-Log([string]$msg) {
    Add-Content -Path $MainLog -Value ("{0:yyyy-MM-dd HH:mm:ss}  [{1}] {2}" -f (Get-Date), $Kind, $msg)
}

. (Join-Path $Root 'Notify.ps1')

# Emails every item's outcome (merged, fixed, rejected, deferred, ...) from Claude's results file,
# if email is set up. A run that left no results (crash, killed, bad JSON) is emailed as a failure.
function Send-Results([string]$resultFile, [int]$exitCode) {
    try {
        $config = Get-WatcherMailConfig $Root
        if (-not $config) { return }

        $noun = if ($Kind -eq 'pr') { 'PR' } else { 'Issue' }
        $path = if ($Kind -eq 'pr') { 'pull' } else { 'issues' }
        $results = @()
        if (Test-Path $resultFile) {
            try { $results = @((Get-Content $resultFile -Raw | ConvertFrom-Json) | Where-Object { $_.number }) }
            catch { Write-Log "Results file is not valid JSON: $($_.Exception.Message)" }
        }

        if ($results.Count -eq 0) {
            $subject = "[ClaudeCodeExtension] $noun watcher run failed for $numbers"
            $body = "Claude exited with code $exitCode and reported no results for $list.`n" +
                    "It may have stopped partway through; check GitHub and the repo for half-done work.`n`n" +
                    "Run log: $runLog`nSummary:`n" + $(if (Test-Path $runLog) { Get-Content $runLog -Raw } else { '(none)' })
            Send-WatcherMail $config $subject $body
            Write-Log "Emailed $($config.To): $subject"
            return
        }

        $lines = foreach ($r in $results) {
            $version = if ($r.version) { "published v$($r.version)" } else { 'nothing published' }
            "$noun #$($r.number): $($r.outcome), $version`n$($r.summary)`n" +
                $(if ($r.details) { "`n$($r.details)`n" }) +
                "https://github.com/$Repo/$path/$($r.number)`n"
        }
        $first   = $results[0]
        $subject = if ($results.Count -eq 1) {
            "[ClaudeCodeExtension] $noun #$($first.number) $($first.outcome)" + $(if ($first.version) { ", v$($first.version) published" })
        } else {
            "[ClaudeCodeExtension] $($results.Count) ${noun}s: " + (($results | ForEach-Object { "#$($_.number) $($_.outcome)" }) -join ', ')
        }
        $summary = if (Test-Path $runLog) { "`n----- Claude's summary -----`n" + (Get-Content $runLog -Raw) } else { '' }
        Send-WatcherMail $config $subject (($lines -join "`n") + "`nRun log: $runLog`n" + $summary)
        Write-Log "Emailed $($config.To): $subject"
    }
    catch {
        Write-Log "Email failed: $($_.Exception.Message)"
    }
}

# Claim comments on an item that are still live. A claim older than $ClaimHours belongs to a run
# that died without cleaning up, so it no longer counts.
function Get-LiveClaims([int]$number) {
    $out = Invoke-Gh api --paginate --slurp "repos/$Repo/issues/$number/comments" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "could not list the comments: $out" }
    $cutoff = [DateTimeOffset]::UtcNow.AddHours(-$ClaimHours)
    $pages = $out | Out-String | ConvertFrom-Json
    foreach ($page in $pages) {
        foreach ($c in $page) {
            if (-not $c.body -or -not $c.body.StartsWith($ClaimMarker)) { continue }
            # Windows PowerShell keeps created_at as text; PowerShell 7 turns it into a DateTime in UTC.
            $created = if ($c.created_at -is [DateTime]) { [DateTimeOffset][DateTime]::SpecifyKind($c.created_at, 'Utc') }
                       else { [DateTimeOffset]::Parse($c.created_at, [Globalization.CultureInfo]::InvariantCulture) }
            if ($created -gt $cutoff) { $c }
        }
    }
}

# Deletes this run's claim on an item, if it has one.
function Remove-Claim([int]$number) {
    if (-not $claimed.ContainsKey($number)) { return }
    Invoke-Gh api -X DELETE "repos/$Repo/issues/comments/$($claimed[$number])" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Log "Could not remove the claim on #$number; it expires in $ClaimHours hours." }
    $claimed.Remove($number)
}

# One run at a time across PRs and issues: both edit the repo and publish, and a publish can
# outlast the polling interval. A busy lock just means this tick is skipped.
$mutex = New-Object System.Threading.Mutex($false, 'Global\ClaudeCodeExtensionRepoWatcher')
# Abandoned = a previous run was killed while holding it; the lock is ours now.
try { if (-not $mutex.WaitOne(0)) { Write-Log 'Another run is in progress; skipped.'; exit 0 } } catch [System.Threading.AbandonedMutexException] { }

try {
    # --- Free check -----------------------------------------------------------------------
    # Field lists are quoted: unquoted a,b,c is an array, and splatting it through Invoke-Gh
    # passes the fields as separate words, which gh rejects ("unknown arguments").
    if ($Kind -eq 'pr') {
        $json = Invoke-Gh pr list --repo $Repo --state open --json 'number,headRefOid,title,author' 2>&1
    } else {
        $json = Invoke-Gh issue list --repo $Repo --state open --limit 100 --json 'number,title,author' 2>&1
    }
    if ($LASTEXITCODE -ne 0) { Write-Log "gh failed: $json"; exit 0 }
    # Filter blanks: Windows PowerShell turns "[]" into one empty object.
    $items = @(($json | Out-String | ConvertFrom-Json) | Where-Object { $_.number })
    if ($items.Count -eq 0) { Write-Log "No open ${Kind}s."; exit 0 }

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
    if ($pending.Count -eq 0) { Write-Log "$($items.Count) open, all handled."; exit 0 }
    $numbers = ($pending | ForEach-Object { "#$($_.number)" }) -join ', '

    # --- Don't touch the repo while you're working in it ----------------------------------
    $branch = (& git -C $RepoDir rev-parse --abbrev-ref HEAD).Trim()
    $dirty  = & git -C $RepoDir status --porcelain
    if ($branch -ne 'master' -or $dirty) {
        Write-Log ("{0} waiting, but the repo is busy (branch '{1}', dirty={2}); will retry." -f
            $numbers, $branch, [bool]$dirty)
        exit 0
    }

    # --- Claim the items: skip any that another machine is already working on -------------
    # Each item gets a claim comment first. The earliest live claim (lowest comment id) wins, so
    # two machines that claim at the same moment agree on one winner and the other backs off.
    foreach ($item in $pending) {
        $number = [int]$item.number
        $text = "$ClaimMarker I am working on this now: a repo watcher run started " +
                "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC. Other watchers skip " +
                "it until this comment is removed when the run ends."
        try {
            $posted = Invoke-Gh api -X POST "repos/$Repo/issues/$number/comments" -f "body=$text" 2>&1
            if ($LASTEXITCODE -ne 0) { throw "could not post the claim: $posted" }
            $claimed[$number] = [long](($posted | Out-String | ConvertFrom-Json).id)
            $winner = Get-LiveClaims $number | ForEach-Object { [long]$_.id } | Sort-Object | Select-Object -First 1
            if ($winner -ne $claimed[$number]) {
                Remove-Claim $number
                Write-Log "#$number is already claimed by another watcher; skipped."
            }
        }
        catch {
            Remove-Claim $number
            Write-Log "Claim for #$number failed: $($_.Exception.Message)"
        }
    }
    $pending = @($pending | Where-Object { $claimed.ContainsKey([int]$_.number) })
    if ($pending.Count -eq 0) { exit 0 }
    $numbers = ($pending | ForEach-Object { "#$($_.number)" }) -join ', '

    # --- Work to do: start Claude Code ----------------------------------------------------
    $list   = ($pending | ForEach-Object { "#$($_.number) '$($_.title)' by $($_.author.login)" }) -join '; '
    $runLog = Join-Path $LogDir ("run-{0}-{1:yyyyMMdd-HHmmss}.log" -f $Kind, (Get-Date))
    Write-Log "Starting Claude for: $list (log: $(Split-Path $runLog -Leaf))"

    # Claude reports each item's outcome here; every outcome is emailed.
    $resultFile = [IO.Path]::ChangeExtension($runLog, '.results.json')
    $resultTarget = if ($Wsl) { (& wsl.exe -e wslpath -u ($resultFile -replace '\\', '/') | Out-String).Trim() } else { $resultFile }
    $text = (Get-Content $Prompt -Raw) + @"


Items to handle in this run: $numbers

When you are done, write a JSON array with one entry per item to $resultTarget, for example:
[{"number": 220, "outcome": "merged", "version": "266.0", "summary": "One user-facing sentence.", "details": "What you changed or checked, or why you rejected it."}]
outcome is one of: merged, fixed, answered, asked-for-details, deferred, rejected, skipped-unsafe, failed.
version is the published version, or null when nothing was published.
details is a few plain sentences for the maintainer: what was changed, tested and published, what was
not verified by hand, or the specific reasons for rejecting, deferring or skipping. This is emailed.
"@
    if ($Wsl) {
        $text += @"


You run inside WSL (Linux) on the Windows clone of the repo. Use git and gh from WSL. Windows tools work
through WSL interop from the repo root: run "cmd.exe /c test.cmd" and "cmd.exe /c publish.cmd", and build with
"/mnt/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe" ClaudeCodeExtension.sln -p:Configuration=Release -v:minimal
Keep each file's existing line endings.
"@
    }
    $promptFile = [IO.Path]::ChangeExtension($runLog, '.prompt.md')
    Set-Content $promptFile $text -Encoding utf8

    # This script runs hidden; the Claude run gets its own visible console window so you can see
    # it working (and don't shut down mid-publish). The lock is held until the window closes.
    # Not Start-Process -Wait: Windows PowerShell then waits for every descendant, and the build
    # servers test.cmd/publish.cmd leave behind (VBCSCompiler, MSBuild reuse nodes) kept it blocked
    # until the task's time limit killed it, with no state saved and no email sent.
    $title = "$(if ($Kind -eq 'pr') { 'PR' } else { 'issue' }) $numbers"
    $runArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Root 'Run-Claude.ps1')`" " +
               "-Claude `"$Claude`" -PromptFile `"$promptFile`" -RunLog `"$runLog`" -Title `"$title`"" + $(if ($Wsl) { ' -Wsl' })
    $proc = Start-Process powershell.exe -ArgumentList $runArgs -WorkingDirectory $RepoDir -PassThru
    $null = $proc.Handle   # cache the handle, or ExitCode reads back empty after exit
    $proc.WaitForExit()
    $exit = $proc.ExitCode
    Remove-Item $promptFile -Force -ErrorAction SilentlyContinue
    foreach ($number in @($claimed.Keys)) { Remove-Claim $number }

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

    Send-Results $resultFile $exit

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
    foreach ($number in @($claimed.Keys)) { Remove-Claim $number }
    $mutex.ReleaseMutex()
}
