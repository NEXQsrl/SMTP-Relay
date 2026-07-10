<#
.SYNOPSIS
  Interfaccia grafica per configurare SMTP Relay (appsettings.json + segreti cifrati + servizio).
.DESCRIPTION
  - Carica/salva l'appsettings.json (scegli TU quale file, così modifichi quello giusto: quello accanto a SmtpRelay.exe).
  - Salva i segreti cifrati (via SmtpRelay set-credential, valore passato da stdin: niente password nella cronologia).
  - Test upstream, show-config, install/start/stop del servizio.
.NOTES
  Esegui preferibilmente come Amministratore (per ACL segreti e gestione servizio).
  Avvio:  powershell -ExecutionPolicy Bypass -File .\SmtpRelayConfigurator.ps1
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:Config = $null
$ctl = @{}

# ---------- Helper UI ----------
function Add-Row {
    param(
        [System.Windows.Forms.Control]$Parent,
        [ref]$Y,
        [string]$Label,
        [ValidateSet('text','combo','check','multiline','password')]$Type = 'text',
        [string[]]$Items,
        [int]$Width = 380
    )
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = $Label
    $lbl.AutoSize = $true
    $lbl.Location = New-Object System.Drawing.Point(14, ($Y.Value + 4))
    $Parent.Controls.Add($lbl)

    switch ($Type) {
        'combo' {
            $c = New-Object System.Windows.Forms.ComboBox
            $c.DropDownStyle = 'DropDownList'
            if ($Items) { [void]$c.Items.AddRange($Items) }
            $c.Width = $Width
        }
        'check' {
            $c = New-Object System.Windows.Forms.CheckBox
            $c.Text = ''
        }
        'multiline' {
            $c = New-Object System.Windows.Forms.TextBox
            $c.Multiline = $true; $c.ScrollBars = 'Vertical'; $c.Height = 96; $c.Width = $Width
        }
        'password' {
            $c = New-Object System.Windows.Forms.TextBox
            $c.UseSystemPasswordChar = $true; $c.Width = $Width
        }
        default {
            $c = New-Object System.Windows.Forms.TextBox
            $c.Width = $Width
        }
    }
    $c.Location = New-Object System.Drawing.Point(210, $Y.Value)
    $Parent.Controls.Add($c)

    $Y.Value += $(if ($Type -eq 'multiline') { 104 } elseif ($Type -eq 'check') { 28 } else { 30 })
    return $c
}

function New-Tab {
    param([System.Windows.Forms.TabControl]$Tabs, [string]$Title)
    $tab = New-Object System.Windows.Forms.TabPage
    $tab.Text = $Title
    $tab.AutoScroll = $true
    $tab.BackColor = [System.Drawing.Color]::White
    $Tabs.TabPages.Add($tab)
    return $tab
}

function Get-Node {
    # Ritorna il nodo figlio; se manca lo crea come PSCustomObject vuoto.
    param($Parent, [string]$Name)
    if ($null -eq $Parent.$Name) {
        $Parent | Add-Member -NotePropertyName $Name -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    return $Parent.$Name
}

function Set-Prop { param($Obj, [string]$Name, $Value)
    $Obj | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

# ---------- Form ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = "SMTP Relay - Configuratore"
$form.Size = New-Object System.Drawing.Size(700, 720)
$form.StartPosition = 'CenterScreen'
$form.MinimumSize = New-Object System.Drawing.Size(700, 640)

# --- Barra file (in alto) ---
$lblFile = New-Object System.Windows.Forms.Label
$lblFile.Text = "File appsettings.json:"; $lblFile.AutoSize = $true
$lblFile.Location = New-Object System.Drawing.Point(14, 16)
$form.Controls.Add($lblFile)

$txtFile = New-Object System.Windows.Forms.TextBox
$txtFile.Location = New-Object System.Drawing.Point(160, 13)
$txtFile.Width = 400
$txtFile.Anchor = 'Top,Left,Right'
$form.Controls.Add($txtFile)
$ctl.FilePath = $txtFile

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = "..."; $btnBrowse.Width = 32
$btnBrowse.Location = New-Object System.Drawing.Point(566, 12)
$btnBrowse.Anchor = 'Top,Right'
$form.Controls.Add($btnBrowse)

$btnLoad = New-Object System.Windows.Forms.Button
$btnLoad.Text = "Carica"; $btnLoad.Width = 70
$btnLoad.Location = New-Object System.Drawing.Point(604, 12)
$btnLoad.Anchor = 'Top,Right'
$form.Controls.Add($btnLoad)

# --- TabControl ---
$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Location = New-Object System.Drawing.Point(12, 46)
$tabs.Size = New-Object System.Drawing.Size(664, 560)
$tabs.Anchor = 'Top,Bottom,Left,Right'
$form.Controls.Add($tabs)

# ===== TAB LISTENER =====
$tListener = New-Tab $tabs "Listener"
$y = 14
$ctl.IpAddress            = Add-Row $tListener ([ref]$y) "IP di ascolto"
$ctl.Port                 = Add-Row $tListener ([ref]$y) "Porta"
$ctl.ServerName           = Add-Row $tListener ([ref]$y) "Server name (EHLO)"
$ctl.MaxMessageSizeBytes  = Add-Row $tListener ([ref]$y) "Dim. max messaggio (byte)"
$ctl.MaxRecipients        = Add-Row $tListener ([ref]$y) "Max destinatari"
$ctl.CommandTimeoutSeconds= Add-Row $tListener ([ref]$y) "Timeout comando (s)"

# ===== TAB TLS =====
$tTls = New-Tab $tabs "TLS ingresso"
$y = 14
$ctl.TlsMode              = Add-Row $tTls ([ref]$y) "Modalita" combo @('None','StartTlsOptional','StartTlsRequired')
$ctl.CertificatePath      = Add-Row $tTls ([ref]$y) "Percorso .pfx"
$ctl.CertificatePasswordSecret = Add-Row $tTls ([ref]$y) "Nome segreto pwd .pfx"
$ctl.CertificateThumbprint= Add-Row $tTls ([ref]$y) "Thumbprint (LocalMachine\My)"
$ctl.MinimumProtocol      = Add-Row $tTls ([ref]$y) "Protocollo minimo" combo @('Tls12','Tls13')

# ===== TAB SICUREZZA =====
$tSec = New-Tab $tabs "Sicurezza (IP)"
$y = 14
$lblHint = New-Object System.Windows.Forms.Label
$lblHint.Text = "IP/reti CIDR consentiti (uno per riga). Es: 192.168.10.0/24, 10.0.0.0/8, 127.0.0.1, ::1"
$lblHint.AutoSize = $true; $lblHint.Location = New-Object System.Drawing.Point(14, $y)
$tSec.Controls.Add($lblHint); $y += 24
$ctl.AllowedIps           = Add-Row $tSec ([ref]$y) "Allowlist" multiline
$ctl.RejectWhenEmpty      = Add-Row $tSec ([ref]$y) "Rifiuta se lista vuota (fail-closed)" check

# ===== TAB UPSTREAM =====
$tUp = New-Tab $tabs "Upstream"
$y = 14
$ctl.UpMode               = Add-Row $tUp ([ref]$y) "Modalita auth" combo @('Basic','Microsoft365')
$ctl.UpHost               = Add-Row $tUp ([ref]$y) "Host"
$ctl.UpPort               = Add-Row $tUp ([ref]$y) "Porta"
$ctl.UpSecurity           = Add-Row $tUp ([ref]$y) "Sicurezza" combo @('None','Auto','StartTls','StartTlsWhenAvailable','SslOnConnect')
$ctl.UpTimeout            = Add-Row $tUp ([ref]$y) "Timeout (s)"
$ctl.UpMaxAttempts        = Add-Row $tUp ([ref]$y) "Tentativi max"
$ctl.UpRetryDelay         = Add-Row $tUp ([ref]$y) "Ritardo retry base (s)"
$ctl.UpOverrideFrom       = Add-Row $tUp ([ref]$y) "Forza mittente (From)"

$lblB = New-Object System.Windows.Forms.Label
$lblB.Text = "--- Basic ---"; $lblB.AutoSize = $true; $lblB.Location = New-Object System.Drawing.Point(14, $y); $tUp.Controls.Add($lblB); $y += 24
$ctl.BasicUser            = Add-Row $tUp ([ref]$y) "Username"
$ctl.BasicPwdSecret       = Add-Row $tUp ([ref]$y) "Nome segreto password"

$lblM = New-Object System.Windows.Forms.Label
$lblM.Text = "--- Microsoft 365 ---"; $lblM.AutoSize = $true; $lblM.Location = New-Object System.Drawing.Point(14, $y); $tUp.Controls.Add($lblM); $y += 24
$ctl.M365Tenant           = Add-Row $tUp ([ref]$y) "Tenant ID"
$ctl.M365Client           = Add-Row $tUp ([ref]$y) "Client ID"
$ctl.M365SecretName       = Add-Row $tUp ([ref]$y) "Nome segreto client secret"
$ctl.M365Sender           = Add-Row $tUp ([ref]$y) "Sender UPN"
$ctl.M365Scope            = Add-Row $tUp ([ref]$y) "Scope"

# ===== TAB LOG =====
$tLog = New-Tab $tabs "Log / Delivery"
$y = 14
$ctl.LogDirectory         = Add-Row $tLog ([ref]$y) "Cartella log (vuoto=default)"
$ctl.LogRetainCount       = Add-Row $tLog ([ref]$y) "Retention: n. file"
$ctl.LogRetainDays        = Add-Row $tLog ([ref]$y) "Retention: giorni (vuoto=nessuno)"
$ctl.LogFileSize          = Add-Row $tLog ([ref]$y) "Dim. max file log (byte)"
$ctl.LogLevel             = Add-Row $tLog ([ref]$y) "Livello minimo" combo @('Verbose','Debug','Information','Warning','Error','Fatal')
$ctl.LogConsole           = Add-Row $tLog ([ref]$y) "Log su console" check
$ctl.LogAudit             = Add-Row $tLog ([ref]$y) "Audit trail separato" check
$ctl.SpoolFailed          = Add-Row $tLog ([ref]$y) "Spool messaggi falliti" check
$ctl.SpoolDir             = Add-Row $tLog ([ref]$y) "Cartella spool (vuoto=default)"

# ===== TAB STRUMENTI =====
$tTool = New-Tab $tabs "Strumenti"
$y = 14
$ctl.ExePath              = Add-Row $tTool ([ref]$y) "SmtpRelay.exe o .dll" text $null 300
$btnExe = New-Object System.Windows.Forms.Button
$btnExe.Text = "..."; $btnExe.Width = 32; $btnExe.Location = New-Object System.Drawing.Point(596, ($y - 30))
$tTool.Controls.Add($btnExe)

$lblSec = New-Object System.Windows.Forms.Label
$lblSec.Text = "--- Salva segreto cifrato (via stdin, non appare nella cronologia) ---"
$lblSec.AutoSize = $true; $lblSec.Location = New-Object System.Drawing.Point(14, $y); $tTool.Controls.Add($lblSec); $y += 26
$ctl.SecretName           = Add-Row $tTool ([ref]$y) "Nome segreto"
$ctl.SecretValue          = Add-Row $tTool ([ref]$y) "Valore" password
$btnSaveSecret = New-Object System.Windows.Forms.Button
$btnSaveSecret.Text = "Salva segreto"; $btnSaveSecret.Width = 130; $btnSaveSecret.Location = New-Object System.Drawing.Point(210, $y)
$tTool.Controls.Add($btnSaveSecret); $y += 40

$lblOps = New-Object System.Windows.Forms.Label
$lblOps.Text = "--- Diagnostica / Servizio ---"; $lblOps.AutoSize = $true; $lblOps.Location = New-Object System.Drawing.Point(14, $y); $tTool.Controls.Add($lblOps); $y += 26

$opsButtons = @(
    @{ Text='test-upstream'; Verb=@('test-upstream'); Elev=$false },
    @{ Text='show-config';   Verb=@('show-config');   Elev=$false },
    @{ Text='status';        Verb=@('status');        Elev=$false },
    @{ Text='install';       Verb=@('install');       Elev=$true  },
    @{ Text='start';         Verb=@('start');         Elev=$true  },
    @{ Text='stop';          Verb=@('stop');          Elev=$true  }
)
$bx = 14; $by = $y
foreach ($b in $opsButtons) {
    $btn = New-Object System.Windows.Forms.Button
    $btn.Text = $b.Text; $btn.Width = 100; $btn.Location = New-Object System.Drawing.Point($bx, $by)
    $btn.Tag = $b
    $btn.Add_Click({
        $info = $this.Tag
        try {
            $result = Invoke-Relay -RelayArgs $info.Verb -Elevated:$info.Elev
            $ctl.Output.Text = $result
        } catch { $ctl.Output.Text = "ERRORE: $($_.Exception.Message)" }
    })
    $tTool.Controls.Add($btn)
    $bx += 106; if ($bx -gt 500) { $bx = 14; $by += 34 }
}
$y = $by + 40

$ctl.Output = New-Object System.Windows.Forms.TextBox
$ctl.Output.Multiline = $true; $ctl.Output.ScrollBars = 'Both'; $ctl.Output.ReadOnly = $true
$ctl.Output.WordWrap = $false
$ctl.Output.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$ctl.Output.Location = New-Object System.Drawing.Point(14, $y)
$ctl.Output.Size = New-Object System.Drawing.Size(614, 150)
$tTool.Controls.Add($ctl.Output)

# --- Barra inferiore ---
$btnSave = New-Object System.Windows.Forms.Button
$btnSave.Text = "Salva appsettings.json"; $btnSave.Width = 180; $btnSave.Height = 32
$btnSave.Location = New-Object System.Drawing.Point(12, 616)
$btnSave.Anchor = 'Bottom,Left'
$form.Controls.Add($btnSave)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = ""; $lblStatus.AutoSize = $true
$lblStatus.Location = New-Object System.Drawing.Point(210, 624)
$lblStatus.Anchor = 'Bottom,Left'
$form.Controls.Add($lblStatus)

# ---------- Logica ----------
function Invoke-Relay {
    param([string[]]$RelayArgs, [string]$StdinValue, [switch]$Elevated)
    $exe = $ctl.ExePath.Text.Trim()
    if (-not $exe -or -not (Test-Path $exe)) { throw "Imposta un percorso valido di SmtpRelay.exe/.dll nella tab Strumenti." }
    $isDll = $exe.ToLower().EndsWith('.dll')

    if ($Elevated) {
        if ($isDll) { Start-Process -FilePath "dotnet" -ArgumentList (@("`"$exe`"") + $RelayArgs) -Verb RunAs | Out-Null }
        else        { Start-Process -FilePath $exe -ArgumentList $RelayArgs -Verb RunAs | Out-Null }
        return "Comando '$($RelayArgs -join ' ')' avviato con elevazione (conferma UAC). Controlla poi con 'status'."
    }

    if ($isDll) {
        if ($StdinValue) { return ($StdinValue | & dotnet "$exe" @RelayArgs 2>&1 | Out-String) }
        else             { return (& dotnet "$exe" @RelayArgs 2>&1 | Out-String) }
    } else {
        if ($StdinValue) { return ($StdinValue | & "$exe" @RelayArgs 2>&1 | Out-String) }
        else             { return (& "$exe" @RelayArgs 2>&1 | Out-String) }
    }
}

function Load-Config {
    param([string]$Path)
    if (-not (Test-Path $Path)) { throw "File non trovato: $Path" }
    $json = Get-Content $Path -Raw
    $root = $json | ConvertFrom-Json
    if ($null -eq $root.SmtpRelay) { throw "Il file non contiene la sezione 'SmtpRelay'." }
    $script:Config = $root
    $r = $root.SmtpRelay

    $ctl.IpAddress.Text             = "$($r.Listener.IpAddress)"
    $ctl.Port.Text                  = "$($r.Listener.Port)"
    $ctl.ServerName.Text            = "$($r.Listener.ServerName)"
    $ctl.MaxMessageSizeBytes.Text   = "$($r.Listener.MaxMessageSizeBytes)"
    $ctl.MaxRecipients.Text         = "$($r.Listener.MaxRecipients)"
    $ctl.CommandTimeoutSeconds.Text = "$($r.Listener.CommandTimeoutSeconds)"

    $ctl.TlsMode.SelectedItem              = "$($r.Tls.Mode)"
    $ctl.CertificatePath.Text              = "$($r.Tls.CertificatePath)"
    $ctl.CertificatePasswordSecret.Text    = "$($r.Tls.CertificatePasswordSecret)"
    $ctl.CertificateThumbprint.Text        = "$($r.Tls.CertificateThumbprint)"
    $ctl.MinimumProtocol.SelectedItem      = "$($r.Tls.MinimumProtocol)"

    $ctl.AllowedIps.Text        = (@($r.Security.AllowedIps) -join "`r`n")
    $ctl.RejectWhenEmpty.Checked= [bool]$r.Security.RejectWhenAllowListEmpty

    $ctl.UpMode.SelectedItem    = "$($r.Upstream.Mode)"
    $ctl.UpHost.Text            = "$($r.Upstream.Host)"
    $ctl.UpPort.Text            = "$($r.Upstream.Port)"
    $ctl.UpSecurity.SelectedItem= "$($r.Upstream.Security)"
    $ctl.UpTimeout.Text         = "$($r.Upstream.TimeoutSeconds)"
    $ctl.UpMaxAttempts.Text     = "$($r.Upstream.MaxAttempts)"
    $ctl.UpRetryDelay.Text      = "$($r.Upstream.RetryBaseDelaySeconds)"
    $ctl.UpOverrideFrom.Text    = "$($r.Upstream.OverrideFromAddress)"
    $ctl.BasicUser.Text         = "$($r.Upstream.Basic.Username)"
    $ctl.BasicPwdSecret.Text    = "$($r.Upstream.Basic.PasswordSecret)"
    $ctl.M365Tenant.Text        = "$($r.Upstream.Microsoft365.TenantId)"
    $ctl.M365Client.Text        = "$($r.Upstream.Microsoft365.ClientId)"
    $ctl.M365SecretName.Text    = "$($r.Upstream.Microsoft365.ClientSecretName)"
    $ctl.M365Sender.Text        = "$($r.Upstream.Microsoft365.SenderUsername)"
    $ctl.M365Scope.Text         = "$($r.Upstream.Microsoft365.Scope)"

    $ctl.LogDirectory.Text      = "$($r.Logging.Directory)"
    $ctl.LogRetainCount.Text    = "$($r.Logging.RetainedFileCountLimit)"
    $ctl.LogRetainDays.Text     = "$($r.Logging.RetainedFileTimeLimitDays)"
    $ctl.LogFileSize.Text       = "$($r.Logging.FileSizeLimitBytes)"
    $ctl.LogLevel.SelectedItem  = "$($r.Logging.MinimumLevel)"
    $ctl.LogConsole.Checked     = [bool]$r.Logging.Console
    $ctl.LogAudit.Checked       = [bool]$r.Logging.AuditTrail
    $ctl.SpoolFailed.Checked    = [bool]$r.Delivery.SpoolFailedMessages
    $ctl.SpoolDir.Text          = "$($r.Delivery.FailedSpoolDirectory)"

    # Auto-imposta il percorso exe se accanto al file c'e' SmtpRelay.exe/.dll
    $dir = Split-Path $Path
    foreach ($cand in @("SmtpRelay.exe","SmtpRelay.dll")) {
        $p = Join-Path $dir $cand
        if ((Test-Path $p) -and -not $ctl.ExePath.Text) { $ctl.ExePath.Text = $p }
    }

    # Prefill nome segreto in base alla modalita
    $ctl.SecretName.Text = if ("$($r.Upstream.Mode)" -eq 'Basic') { "$($r.Upstream.Basic.PasswordSecret)" } else { "$($r.Upstream.Microsoft365.ClientSecretName)" }

    $lblStatus.Text = "Caricato: $Path"
    $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
}

function Save-Config {
    param([string]$Path)
    if ($null -eq $script:Config) { throw "Carica prima un file." }
    $r = $script:Config.SmtpRelay

    Set-Prop $r.Listener 'IpAddress' $ctl.IpAddress.Text
    Set-Prop $r.Listener 'Port' ([int]$ctl.Port.Text)
    Set-Prop $r.Listener 'ServerName' $ctl.ServerName.Text
    Set-Prop $r.Listener 'MaxMessageSizeBytes' ([long]$ctl.MaxMessageSizeBytes.Text)
    Set-Prop $r.Listener 'MaxRecipients' ([int]$ctl.MaxRecipients.Text)
    Set-Prop $r.Listener 'CommandTimeoutSeconds' ([int]$ctl.CommandTimeoutSeconds.Text)

    Set-Prop $r.Tls 'Mode' "$($ctl.TlsMode.SelectedItem)"
    Set-Prop $r.Tls 'CertificatePath' $ctl.CertificatePath.Text
    Set-Prop $r.Tls 'CertificatePasswordSecret' $ctl.CertificatePasswordSecret.Text
    Set-Prop $r.Tls 'CertificateThumbprint' $ctl.CertificateThumbprint.Text
    Set-Prop $r.Tls 'MinimumProtocol' "$($ctl.MinimumProtocol.SelectedItem)"

    $ips = @($ctl.AllowedIps.Text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    Set-Prop $r.Security 'AllowedIps' $ips
    Set-Prop $r.Security 'RejectWhenAllowListEmpty' ([bool]$ctl.RejectWhenEmpty.Checked)

    Set-Prop $r.Upstream 'Mode' "$($ctl.UpMode.SelectedItem)"
    Set-Prop $r.Upstream 'Host' $ctl.UpHost.Text
    Set-Prop $r.Upstream 'Port' ([int]$ctl.UpPort.Text)
    Set-Prop $r.Upstream 'Security' "$($ctl.UpSecurity.SelectedItem)"
    Set-Prop $r.Upstream 'TimeoutSeconds' ([int]$ctl.UpTimeout.Text)
    Set-Prop $r.Upstream 'MaxAttempts' ([int]$ctl.UpMaxAttempts.Text)
    Set-Prop $r.Upstream 'RetryBaseDelaySeconds' ([int]$ctl.UpRetryDelay.Text)
    Set-Prop $r.Upstream 'OverrideFromAddress' $ctl.UpOverrideFrom.Text
    Set-Prop $r.Upstream.Basic 'Username' $ctl.BasicUser.Text
    Set-Prop $r.Upstream.Basic 'PasswordSecret' $ctl.BasicPwdSecret.Text
    Set-Prop $r.Upstream.Microsoft365 'TenantId' $ctl.M365Tenant.Text
    Set-Prop $r.Upstream.Microsoft365 'ClientId' $ctl.M365Client.Text
    Set-Prop $r.Upstream.Microsoft365 'ClientSecretName' $ctl.M365SecretName.Text
    Set-Prop $r.Upstream.Microsoft365 'SenderUsername' $ctl.M365Sender.Text
    Set-Prop $r.Upstream.Microsoft365 'Scope' $ctl.M365Scope.Text

    Set-Prop $r.Logging 'Directory' $ctl.LogDirectory.Text
    Set-Prop $r.Logging 'RetainedFileCountLimit' ([int]$ctl.LogRetainCount.Text)
    if ($ctl.LogRetainDays.Text.Trim()) { Set-Prop $r.Logging 'RetainedFileTimeLimitDays' ([int]$ctl.LogRetainDays.Text) }
    else { Set-Prop $r.Logging 'RetainedFileTimeLimitDays' $null }
    Set-Prop $r.Logging 'FileSizeLimitBytes' ([long]$ctl.LogFileSize.Text)
    Set-Prop $r.Logging 'MinimumLevel' "$($ctl.LogLevel.SelectedItem)"
    Set-Prop $r.Logging 'Console' ([bool]$ctl.LogConsole.Checked)
    Set-Prop $r.Logging 'AuditTrail' ([bool]$ctl.LogAudit.Checked)
    Set-Prop $r.Delivery 'SpoolFailedMessages' ([bool]$ctl.SpoolFailed.Checked)
    Set-Prop $r.Delivery 'FailedSpoolDirectory' $ctl.SpoolDir.Text

    $script:Config | ConvertTo-Json -Depth 25 | Set-Content -Path $Path -Encoding UTF8
    $lblStatus.Text = "Salvato: $Path"
    $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
}

# ---------- Eventi ----------
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = "appsettings (*.json)|*.json|Tutti|*.*"
    if ($dlg.ShowDialog() -eq 'OK') { $ctl.FilePath.Text = $dlg.FileName }
})
$btnExe.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = "SmtpRelay (*.exe;*.dll)|SmtpRelay.exe;SmtpRelay.dll|Tutti|*.*"
    if ($dlg.ShowDialog() -eq 'OK') { $ctl.ExePath.Text = $dlg.FileName }
})
$btnLoad.Add_Click({
    try { Load-Config $ctl.FilePath.Text }
    catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Errore", 'OK', 'Error') | Out-Null }
})
$btnSave.Add_Click({
    try { Save-Config $ctl.FilePath.Text }
    catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Errore", 'OK', 'Error') | Out-Null }
})
$btnSaveSecret.Add_Click({
    try {
        $name = $ctl.SecretName.Text.Trim()
        $val  = $ctl.SecretValue.Text
        if (-not $name) { throw "Indica il nome del segreto." }
        if (-not $val)  { throw "Indica il valore del segreto." }
        $out = Invoke-Relay -RelayArgs @('set-credential','--name',$name,'--stdin') -StdinValue $val
        $ctl.Output.Text = $out
        $ctl.SecretValue.Text = ""
        $lblStatus.Text = "Segreto '$name' salvato."; $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
    } catch {
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Errore", 'OK', 'Error') | Out-Null
    }
})

# Percorso di default: appsettings.json accanto allo script, altrimenti quello del progetto.
$defaultPath = Join-Path $PSScriptRoot "src\SmtpRelay.Service\appsettings.json"
if (Test-Path (Join-Path $PSScriptRoot "appsettings.json")) { $defaultPath = Join-Path $PSScriptRoot "appsettings.json" }
$ctl.FilePath.Text = $defaultPath
if (Test-Path $defaultPath) { try { Load-Config $defaultPath } catch {} }

[void]$form.ShowDialog()
