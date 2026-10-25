# Visible console for one unattended Claude Code run of the repo watcher.
# Started by Watch-Repo.ps1 only when there is work, so you can see that a run is in progress
# (and not shut down or reboot mid-publish). Shows Claude's steps live, keeps the PC awake while
# it runs, writes the final summary to the run log and exits with Claude's exit code.

param(
    [Parameter(Mandatory)][string]$Claude,
    [Parameter(Mandatory)][string]$PromptFile,
    [Parameter(Mandatory)][string]$RunLog,
    [Parameter(Mandatory)][string]$Title
)

$Host.UI.RawUI.WindowTitle = "Claude repo watcher - $Title - do not shut down"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# Keep the system awake for the duration of the run (cleared when this process exits).
Add-Type -Namespace Win32 -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
$ES_CONTINUOUS = [uint32]'0x80000000'; $ES_SYSTEM_REQUIRED = [uint32]'0x00000001'
[void][Win32.Power]::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED)

function Write-Banner([string]$text, [ConsoleColor]$color) {
    $line = '=' * [math]::Min(100, [math]::Max(40, $text.Length + 4))
    Write-Host $line -ForegroundColor $color
    Write-Host "  $text" -ForegroundColor $color
    Write-Host $line -ForegroundColor $color
}

function Format-Tool($use) {
    $i = $use.input
    $detail = switch -Regex ($use.name) {
        '^Bash$|^PowerShell$'      { $i.command }
        '^(Read|Edit|Write)$'      { $i.file_path }
        '^(Grep|Glob)$'            { $i.pattern }
        default                    { ($i | ConvertTo-Json -Compress -Depth 3) }
    }
    $detail = ("$detail" -replace '\s+', ' ').Trim()
    if ($detail.Length -gt 160) { $detail = $detail.Substring(0, 157) + '...' }
    "  > $($use.name): $detail"
}

Write-Banner "Claude is working on $Title. Please don't shut down or reboot." Yellow
Write-Host ("Started {0:yyyy-MM-dd HH:mm:ss}. Log: {1}`n" -f (Get-Date), $RunLog) -ForegroundColor DarkGray

$result = $null
$isError = $false
Get-Content $PromptFile -Raw |
    & $Claude -p --model opus --effort high --permission-mode bypassPermissions --output-format stream-json --verbose 2>&1 |
    ForEach-Object {
        $line = "$_"
        try { $evt = $line | ConvertFrom-Json } catch { Write-Host $line -ForegroundColor DarkGray; return }
        switch ($evt.type) {
            'assistant' {
                foreach ($c in $evt.message.content) {
                    if ($c.type -eq 'text' -and $c.text.Trim()) { Write-Host "`n$($c.text.Trim())" }
                    elseif ($c.type -eq 'tool_use') { Write-Host (Format-Tool $c) -ForegroundColor Cyan }
                }
            }
            'result' { $result = $evt.result; $isError = [bool]$evt.is_error }
        }
    }
$exit = [int]$LASTEXITCODE
# No final result event (crash, killed) or an error result counts as a failure.
if ($exit -eq 0 -and ($isError -or $null -eq $result)) { $exit = 1 }

$result | Out-File -FilePath $RunLog -Encoding utf8
if ($result) { Write-Host "`n--- Summary ---`n$result" -ForegroundColor White }
Write-Host ''
if ($exit -eq 0) { Write-Banner 'Done. It is safe to shut down.' Green }
else             { Write-Banner "Claude failed (exit code $exit). See the log above and $RunLog." Red }

# Leave the summary on screen for a while; the watcher holds its lock until this window closes.
Write-Host 'This window closes in 5 minutes. Press any key to close it now.' -ForegroundColor DarkGray
$deadline = (Get-Date).AddMinutes(5)
while ((Get-Date) -lt $deadline) {
    if ([Console]::KeyAvailable) { break }
    Start-Sleep -Milliseconds 250
}
exit $exit
