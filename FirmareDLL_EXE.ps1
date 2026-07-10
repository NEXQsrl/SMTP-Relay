# Firma gli eseguibili nella cartella del servizio
# Eseguire come Amministratore

$certStore = "Cert:\CurrentUser\My"   # oppure Cert:\LocalMachine\My
$exeFolder = "C:\SMTPRelay\Service"

# Trova il certificato di code signing
$cert = Get-ChildItem $certStore | Where-Object {
    $_.EnhancedKeyUsageList.FriendlyName -contains "Code Signing"
} | Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Error "Nessun certificato Code Signing trovato in $certStore"
    exit 1
}

Write-Host "Certificato trovato: $($cert.Subject)"
Write-Host "Scadenza: $($cert.NotAfter)"
Write-Host ""

# Firma tutti gli EXE e DLL nella cartella
$files = Get-ChildItem $exeFolder -Include "*.exe","*.dll" -Recurse

foreach ($file in $files) {
    $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer "http://timestamp.digicert.com"
    
    if ($result.Status -eq "Valid") {
        Write-Host "[OK]  $($file.Name)"
    } else {
        Write-Warning "[FAIL] $($file.Name) — $($result.StatusMessage)"
    }
}

Write-Host ""
Write-Host "Firma completata."