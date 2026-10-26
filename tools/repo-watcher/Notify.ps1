# Email notification helpers for the repo watcher (dot-sourced by Watch-Repo.ps1 and
# Install-RepoWatcher.ps1). Mail goes through Gmail SMTP with an app password saved by
# "Install-RepoWatcher.ps1 -SetupEmail" in the install folder:
#   email.json            { "To": "...", "From": "..." }
#   email-credential.xml  Export-Clixml credential; DPAPI-encrypted, readable only by this
#                         Windows user on this machine.
# Or, where SMTP is blocked, through the signed-in classic Outlook ("-SetupEmail ... -Outlook"):
#   email.json            { "To": "...", "Transport": "outlook" }

function Get-WatcherMailConfig([string]$Dir) {
    $configFile = Join-Path $Dir 'email.json'
    $credFile   = Join-Path $Dir 'email-credential.xml'
    if (-not (Test-Path $configFile)) { return $null }
    $config = Get-Content $configFile -Raw | ConvertFrom-Json
    if ($config.Transport -eq 'outlook') { return [pscustomobject]@{ To = $config.To; Transport = 'outlook' } }
    if (-not (Test-Path $credFile)) { return $null }
    [pscustomobject]@{ To = $config.To; From = $config.From; Credential = Import-Clixml $credFile }
}

function Send-WatcherMail($Config, [string]$Subject, [string]$Body) {
    if ($Config.Transport -eq 'outlook') {
        # Attaches to the running Outlook (or starts it) in this user's session; sent from its default account.
        $outlook = New-Object -ComObject Outlook.Application
        $mail = $outlook.CreateItem(0)
        try {
            $mail.To = $Config.To
            $mail.Subject = $Subject
            $mail.Body = $Body
            $mail.Send()
        } finally {
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($mail)
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($outlook)
        }
        return
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $message = New-Object System.Net.Mail.MailMessage($Config.From, $Config.To, $Subject, $Body)
    $client  = New-Object System.Net.Mail.SmtpClient('smtp.gmail.com', 587)
    try {
        $client.EnableSsl   = $true
        $client.Credentials = $Config.Credential.GetNetworkCredential()
        $client.Send($message)
    } finally {
        $message.Dispose()
        $client.Dispose()
    }
}
