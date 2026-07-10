# SMTP Relay Service

Relay SMTP aziendale come **servizio Windows**, scritto in C# / .NET 10.

Riceve la posta da mittenti interni autorizzati (per IP) e la **inoltra** a uno smart host —
tipicamente **Microsoft 365** con **OAuth2 (client credentials, app-only)** oppure un server
SMTP qualsiasi con **autenticazione di base**. Pensato per scenari in cui applicazioni, stampanti
o apparati di rete non possono usare l'autenticazione moderna e devono appoggiarsi a un relay centrale.

---

## Caratteristiche

| Requisito | Implementazione |
|-----------|-----------------|
| Servizio Windows | Host generico .NET + `Microsoft.Extensions.Hosting.WindowsServices`, auto-restart su crash |
| Porta di ascolto configurabile | `SmtpRelay:Listener:Port` (default 2525) |
| Autenticazione in uscita | **Basic** (utente/password) **oppure Microsoft 365 OAuth2** (client credentials) |
| Allowlist IP | Filtro **CIDR** (IPv4/IPv6) applicato **a livello di socket**: gli IP non consentiti vengono chiusi prima del banner SMTP |
| Credenziali cifrate | **DPAPI** (scope LocalMachine) + entropia per-installazione, file con ACL ristretta a SYSTEM/Administrators |
| Log con retention | **Serilog**, rotazione giornaliera + per dimensione, retention per numero file e per giorni; **audit trail** separato |
| TLS in ingresso | **STARTTLS** opzionale/obbligatorio con certificato (file `.pfx` o store `LocalMachine\My`) |
| Extra enterprise | Validazione fail-fast della config, retry con backoff, spool diagnostico dei falliti, limiti dimensione/destinatari, CLI di gestione e diagnostica |

---

## Architettura in breve

```
  App/Device interni                 SMTP Relay (servizio Windows)                 Smart host
  ------------------                 -----------------------------                 ----------
        │  SMTP (2525)                                                                  
        ├───────────────►  [Gate IP/CIDR]  ──►  [Listener SMTP]  ──►  [Relay MailKit] ──► smtp.office365.com:587
        │                   scarta IP non         EHLO/MAIL/RCPT       Basic  ┌── STARTTLS
        │                   consentiti            DATA + limiti        M365   └── XOAUTH2 (token OAuth2)
                                                        │
                                                   [Audit + Log]  ── C:\ProgramData\SmtpRelay\logs
                                                   [Secrets DPAPI] ─ C:\ProgramData\SmtpRelay\secrets.dat
```

Librerie: `SmtpServer` (ricezione), `MailKit`/`MimeKit` (invio), `Microsoft.Identity.Client` (MSAL, token M365), `Serilog` (log).

---

## Requisiti

- Windows (10/11, Server 2016+)
- **.NET 10 Runtime** sul server di destinazione (oppure pubblicare *self-contained*, vedi sotto)
- Privilegi di **amministratore** per installare il servizio e impostare le credenziali

---

## Build e pubblicazione

Dalla cartella della soluzione:

```powershell
# Compilazione
dotnet build -c Release

# Pubblicazione framework-dependent (richiede il runtime .NET 10 sul server)
dotnet publish src/SmtpRelay.Service -c Release -o publish

# Oppure self-contained (nessun runtime da installare sul server)
dotnet publish src/SmtpRelay.Service -c Release -r win-x64 --self-contained true -o publish
```

Nella cartella `publish` troverai `SmtpRelay.exe` e `appsettings.json`.
È disponibile anche lo script `publish.ps1`.

Copia la cartella `publish` sul server, ad esempio in `C:\Program Files\SmtpRelay`.

---

## Interfaccia grafica di configurazione (opzionale)

Per non modificare a mano il JSON è disponibile un configuratore grafico in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\SmtpRelayConfigurator.ps1
```

Permette di: scegliere **quale** `appsettings.json` modificare (importante: quello **accanto a
`SmtpRelay.exe`**), editare tutte le sezioni con menu/checkbox, **salvare i segreti cifrati** (valore
passato via stdin, non finisce nella cronologia) e lanciare `test-upstream` / `show-config` /
`install` / `start`. Esegui la GUI **come amministratore**.

## Configurazione (`appsettings.json`)

Tutta la configurazione è nella sezione `SmtpRelay`. I **segreti non stanno qui**: sono cifrati
nello store (`secrets.dat`) e referenziati per nome.

```jsonc
{
  "SmtpRelay": {
    "ServiceName": "SmtpRelay",
    "DataDirectory": "C:\\ProgramData\\SmtpRelay",

    "Listener": {
      "IpAddress": "0.0.0.0",        // interfaccia di ascolto
      "Port": 2525,                  // porta di ascolto
      "ServerName": "smtp-relay",
      "MaxMessageSizeBytes": 26214400,
      "MaxRecipients": 100,
      "CommandTimeoutSeconds": 120
    },

    "Tls": {
      "Mode": "None",                // None | StartTlsOptional | StartTlsRequired
      "CertificatePath": "",         // percorso .pfx (alternativa a Thumbprint)
      "CertificatePasswordSecret": "TlsCertPassword",
      "CertificateThumbprint": "",   // certificato in LocalMachine\My
      "MinimumProtocol": "Tls12"     // Tls12 | Tls13
    },

    "Security": {
      "AllowedIps": [ "127.0.0.1", "::1", "192.168.10.0/24" ],
      "RejectWhenAllowListEmpty": true   // fail-closed se la lista è vuota
    },

    "Upstream": {
      "Mode": "Microsoft365",        // Basic | Microsoft365
      "Host": "smtp.office365.com",
      "Port": 587,
      "Security": "StartTls",        // None | Auto | StartTls | StartTlsWhenAvailable | SslOnConnect
      "TimeoutSeconds": 100,
      "MaxAttempts": 3,
      "RetryBaseDelaySeconds": 5,
      "OverrideFromAddress": "",     // forza il return-path (opzionale)
      "Basic": {
        "Username": "relay@contoso.com",
        "PasswordSecret": "UpstreamPassword"
      },
      "Microsoft365": {
        "TenantId": "<tenant-guid>",
        "ClientId": "<app-guid>",
        "ClientSecretName": "M365ClientSecret",
        "SenderUsername": "relay@contoso.com",
        "Scope": "https://outlook.office365.com/.default"
      }
    },

    "Logging": {
      "Directory": "",               // vuoto => {DataDirectory}\logs
      "RetainedFileCountLimit": 31,
      "RetainedFileTimeLimitDays": 31,
      "FileSizeLimitBytes": 52428800,
      "MinimumLevel": "Information",
      "Console": true,
      "AuditTrail": true
    },

    "Delivery": {
      "SpoolFailedMessages": true,
      "FailedSpoolDirectory": ""     // vuoto => {DataDirectory}\failed
    }
  }
}
```

> Ogni valore può essere sovrascritto anche via variabile d'ambiente con prefisso `SMTPRELAY_` e
> separatore `__`, es. `SMTPRELAY_SmtpRelay__Listener__Port=25`.

Verifica in qualsiasi momento la configurazione effettiva:

```powershell
SmtpRelay show-config
```

---

## Gestione delle credenziali (cifrate)

I segreti sono cifrati con **DPAPI (LocalMachine)** più un'entropia casuale per-installazione
conservata in `entropy.bin` (ACL: solo SYSTEM/Administrators). Il blob risultante è legato alla
macchina: copiato altrove non è decifrabile.

```powershell
# Imposta un segreto (verrà chiesto in modo mascherato se ometti --value)
SmtpRelay set-credential --name M365ClientSecret --value "il-client-secret"
SmtpRelay set-credential --name UpstreamPassword           # prompt mascherato
SmtpRelay set-credential --name UpstreamPassword --stdin < pwd.txt

SmtpRelay list-credentials       # elenca i nomi (mai i valori)
SmtpRelay remove-credential --name M365ClientSecret
```

> Esegui `set-credential` **sullo stesso server** dove gira il servizio (la cifratura è per-macchina)
> e con un utente amministratore, così il servizio (LocalSystem) potrà decifrare.

---

## Configurare Microsoft 365 (OAuth2 client credentials)

1. **Entra ID → App registrations → New registration** (single tenant). Annota **Directory (tenant) ID** e **Application (client) ID**.
2. **Certificates & secrets → New client secret**. Copia il valore.
3. **API permissions → Add a permission → APIs my organization uses →** *Office 365 Exchange Online* → **Application permissions** → aggiungi **`SMTP.SendAsApp`** → **Grant admin consent**.
4. Abilita **SMTP AUTH** sulla casella mittente (Interfaccia di amministrazione Exchange → destinatari → cassetta postale → *Posta* → *Gestisci impostazioni app di posta* → attiva *SMTP autenticato*). In alternativa via PowerShell:
   ```powershell
   Set-CASMailbox -Identity relay@contoso.com -SmtpClientAuthenticationDisabled $false
   ```
5. Configura `appsettings.json` (`Mode: "Microsoft365"`, `TenantId`, `ClientId`, `SenderUsername`) e salva il secret:
   ```powershell
   SmtpRelay set-credential --name M365ClientSecret --value "<secret>"
   SmtpRelay test-upstream
   ```

`SenderUsername` è l'UPN della casella da cui il relay invia (usato per XOAUTH2 e come mittente
predefinito quando il messaggio non ne indica uno).

## Configurare l'autenticazione di base

```jsonc
"Upstream": { "Mode": "Basic", "Host": "smtp.tuoprovider.it", "Port": 587, "Security": "StartTls",
              "Basic": { "Username": "relay@contoso.com", "PasswordSecret": "UpstreamPassword" } }
```
```powershell
SmtpRelay set-credential --name UpstreamPassword --value "<password>"
SmtpRelay test-upstream
```

---

## TLS in ingresso (STARTTLS)

- `Mode: "None"` — traffico in chiaro (solo reti interne fidate).
- `Mode: "StartTlsOptional"` — il relay offre STARTTLS; i client possono cifrare.
- `Mode: "StartTlsRequired"` — `MAIL FROM` è rifiutato finché la sessione non è cifrata.

Certificato da file:
```powershell
SmtpRelay set-credential --name TlsCertPassword --value "<password-pfx>"
# appsettings: Tls.Mode="StartTlsOptional", Tls.CertificatePath="C:\\certs\\relay.pfx"
```
oppure dallo store: imposta `Tls.CertificateThumbprint` con un certificato presente in `LocalMachine\My`.

---

## Allowlist IP

Solo gli IP/reti elencati in `Security.AllowedIps` possono contattare il relay. Supporta IP singoli
e CIDR, IPv4 e IPv6 (`192.168.10.0/24`, `10.0.0.0/8`, `2001:db8::/32`, `127.0.0.1`, `::1`).
Le connessioni da IP non consentiti vengono **chiuse immediatamente**, prima di ricevere il banner SMTP,
e registrate come `Connessione RIFIUTATA`. Con lista vuota e `RejectWhenAllowListEmpty=true` il relay
è **fail-closed** (rifiuta tutto).

---

## Installazione come servizio Windows

Da un terminale **come amministratore**, nella cartella pubblicata:

```powershell
.\SmtpRelay.exe install          # crea il servizio (avvio automatico, restart su crash)
.\SmtpRelay.exe start
.\SmtpRelay.exe status
.\SmtpRelay.exe stop
.\SmtpRelay.exe uninstall
```

Account del servizio: default **LocalSystem** (può decifrare i segreti e accedere a `ProgramData`).
Per usare un account dedicato / **gMSA**:

```powershell
.\SmtpRelay.exe install --account "CONTOSO\svc-smtprelay$"        # gMSA (senza password)
.\SmtpRelay.exe install --account "CONTOSO\svc-smtprelay" --password "<pwd>"
```
> Con un account dedicato, imposta le credenziali **dopo** aver deciso l'account e assicurati che
> abbia accesso a `DataDirectory`.

Apri la porta nel firewall se necessario:
```powershell
New-NetFirewallRule -DisplayName "SMTP Relay 2525" -Direction Inbound -Protocol TCP -LocalPort 2525 -Action Allow
```

---

## Log e retention

- Cartella: `{DataDirectory}\logs` (default `C:\ProgramData\SmtpRelay\logs`).
- `smtp-relay-YYYYMMDD.log` — log applicativo. `audit-YYYYMMDD.log` — solo eventi di consegna
  (`DELIVERED` / `FAILED` con IP client, mittente, destinatari, dimensione, oggetto, durata, esito).
- Rotazione **giornaliera** e **per dimensione** (`FileSizeLimitBytes`); retention per numero file
  (`RetainedFileCountLimit`) e per giorni (`RetainedFileTimeLimitDays`).

---

## Diagnostica

```powershell
SmtpRelay show-config       # configurazione effettiva + validazione
SmtpRelay test-upstream     # verifica connessione + autenticazione verso l'upstream
SmtpRelay help
```

In caso di fallimento definitivo di consegna, se `Delivery.SpoolFailedMessages=true`, una copia
`.eml` viene salvata in `{DataDirectory}\failed` a scopo diagnostico (non viene reinviata).

---

## Note di sicurezza

- I segreti non compaiono mai nei log né in `appsettings.json`; sono cifrati con DPAPI per-macchina.
- `entropy.bin` e `secrets.dat` hanno ACL ristretta a SYSTEM/Administrators.
- La cifratura è **legata alla macchina**: per migrare il relay, reimposta le credenziali sul nuovo host.
- In produzione usa `Tls.Mode` almeno `StartTlsOptional` e mantieni l'allowlist il più stretta possibile.
- Verso Microsoft 365 il canale è sempre cifrato (STARTTLS) e l'autenticazione usa token OAuth2 a breve durata.

---

## Comportamento SMTP

- Consegna riuscita → `250`.
- Errore **transitorio** upstream (rete/connessione, 4xx) → `451` (il client può ritentare); retry
  interni con backoff lineare fino a `MaxAttempts`.
- Errore **permanente** (auth/5xx) → `554`.
- Messaggio oltre il limite dichiarato (`SIZE=` in `MAIL FROM`) → `552`; il limite è comunque annunciato in EHLO. Troppi destinatari → `RCPT` rifiutato.
