<#
.SYNOPSIS
  Pubblica il servizio SMTP Relay in una cartella pronta al deploy.
.EXAMPLE
  .\publish.ps1                       # framework-dependent (richiede .NET 10 sul server)
  .\publish.ps1 -SelfContained        # nessun runtime da installare sul server
#>
param(
  [switch]$SelfContained,
  [string]$Output = "publish",
  [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot "src\SmtpRelay.Service"

$args = @("publish", $project, "-c", $Configuration, "-o", $Output, "-r", "win-x64")
if ($SelfContained) { $args += @("--self-contained", "true") }
else                { $args += @("--self-contained", "false") }

Write-Host "dotnet $($args -join ' ')" -ForegroundColor Cyan
& dotnet @args

Write-Host ""
Write-Host "Pubblicato in: $(Resolve-Path $Output)" -ForegroundColor Green
Write-Host "Prossimi passi (come amministratore, sul server):" -ForegroundColor Yellow
Write-Host "  1) Copia la cartella su es. C:\Program Files\SmtpRelay"
Write-Host "  2) Modifica appsettings.json (porta, AllowedIps, Upstream)"
Write-Host "  3) SmtpRelay set-credential --name M365ClientSecret --value <secret>"
Write-Host "  4) SmtpRelay test-upstream"
Write-Host "  5) SmtpRelay install ; SmtpRelay start"
