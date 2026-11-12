<p align="center"><a href="SECURITY.md">English</a> | <b>Italiano</b></p>

# Criteri di sicurezza

## Segnalare una vulnerabilità

Segnala le vulnerabilità **in privato**. Non aprire una issue pubblica, non pubblicare i dettagli prima che sia
disponibile una correzione e non fare prove su sistemi che non sono tuoi o che non sei autorizzato a provare.

**Come segnalare**: usa la segnalazione privata delle vulnerabilità di GitHub per questo repository: apri la
[scheda Security](https://github.com/NEXQsrl/SMTP-Relay/security) e scegli **Report a vulnerability**
([link diretto](https://github.com/NEXQsrl/SMTP-Relay/security/advisories/new)). La segnalazione è visibile solo ai
maintainer finché non viene pubblicato un avviso. Se non puoi usare GitHub, contatta NEXQ tramite
[www.nexq.it](https://www.nexq.it) e chiedi un canale privato, senza includere i dettagli nel primo messaggio.

Indica:

- la versione interessata e come è configurato il relay (modalità di autenticazione, TLS in ingresso, account del
  servizio);
- i passi per riprodurre il problema e l'impatto osservato o atteso;
- se credenziali, token, contenuto dei messaggi o dati di audit possono essere esposti o alterati.

Lascia ai maintainer un tempo ragionevole per indagare e pubblicare una correzione prima di qualsiasi divulgazione.

### Ambito

**Incluso**: il servizio, la console di gestione, il setup, il meccanismo di aggiornamento, la gestione dei
segreti, l'elenco degli IP consentiti e la gestione di SMTP.

**Escluso**:

- vulnerabilità di Windows o delle dipendenze di terze parti (segnalale a chi le sviluppa, e avvisaci se SMTP Relay
  ne è colpito);
- negazione del servizio che richiede un flooding a livello di rete, o che proviene da un indirizzo che hai messo
  nell'elenco dei consentiti;
- problemi che richiedono diritti di amministratore locale sul server. Un amministratore è attendibile per
  progetto: può leggere i segreti e controllare il servizio.

## Versioni supportate

Le correzioni di sicurezza escono con la versione successiva; le riceve solo l'ultima versione.

| Versione | Correzioni di sicurezza |
|---|---|
| 2.x (ultima) | Sì |
| 1.x | No: aggiorna all'ultima versione |

SMTP Relay **non ha avuto una revisione di sicurezza indipendente**. Non esporre la porta di ascolto a Internet.

## Il modello di sicurezza in breve

### Chi può inviare

- L'**elenco degli IP consentiti** è il controllo di accesso del relay. Viene applicato quando la connessione è
  accettata: un client non consentito viene disconnesso prima di qualsiasi saluto SMTP. Un client di cui non si può
  determinare l'indirizzo viene rifiutato.
- L'impostazione predefinita è **fail-closed**: con l'elenco vuoto tutti sono rifiutati. Accettare tutti richiede
  due scelte esplicite (`RejectWhenAllowListEmpty = false` con l'elenco vuoto, oppure una rete `/0`), ed entrambe
  sono segnalate come *open relay* nella console, in `show-config` e nel log.
- Per impostazione predefinita i client **non si autenticano con una password**: chiunque possa inviare da un
  indirizzo consentito può usare il relay, come con il relay SMTP di IIS. Tieni l'elenco stretto e, dove puoi, usa
  indirizzi singoli invece di reti intere.
- **L'accesso (SMTP AUTH PLAIN / LOGIN) è facoltativo e disattivato per impostazione predefinita.** Gli utenti
  appartengono al relay; non sono account di Windows. Ogni password è conservata come hash PBKDF2-SHA256 con salt
  (200.000 iterazioni) in `users.json` nella cartella dati, mai in chiaro e mai nei log. Una password è accettata
  solo su una sessione cifrata con STARTTLS, a meno che tu non attivi `AllowWithoutTls`: in quel caso nomi utente e
  password viaggiano in chiaro, e la console, `show-config` e il log lo segnalano.
- Con `AllowFromAnyAddress` l'elenco non nasconde più il relay: ogni computer che raggiunge la porta riceve il
  saluto SMTP e può provare delle password. I tentativi sono rallentati (un secondo per ogni password sbagliata,
  tre tentativi per connessione, dieci errori in dieci minuti bloccano l'indirizzo per il resto di quel periodo)
  ma non resi impossibili: usa password lunghe e casuali e non esporre la porta a Internet. Il blocco è per
  indirizzo, quindi un apparato con la password sbagliata può bloccare gli altri apparati dietro lo stesso indirizzo.
- I limiti su dimensione dei messaggi, destinatari, sessioni contemporanee e inattività delimitano ciò che un
  client può consumare. I messaggi restano in memoria mentre vengono inoltrati: dimensiona i limiti sulla memoria
  del server.

### Verso lo smart host

- **La cifratura è imposta.** `StartTls` e `SslOnConnect` cifrano sempre; `Auto` significa TLS implicito sulla porta
  465 e STARTTLS obbligatorio altrimenti. `None` e `StartTlsWhenAvailable` vengono rifiutati quando sono configurate
  credenziali, a meno che non sia impostato `AllowInsecureAuthentication`. Indipendentemente dalla configurazione,
  il relay controlla che il canale sia cifrato subito prima di autenticarsi: un downgrade di STARTTLS non può far
  uscire una password o un token.
- Il certificato dello smart host viene validato (nome, catena, revoca) con l'archivio di attendibilità di Windows.
- In modalità Microsoft 365 il relay possiede un **client secret**, non la password di una casella. Limita ciò che
  l'applicazione può fare: solo `SMTP.SendAsApp`, e autorizzazione solo sulla casella mittente.

### Segreti

- Password, client secret e password del file `.pfx` sono salvati in `secrets.dat`, ciascuno cifrato con
  **Windows DPAPI (ambito macchina)** più 32 byte casuali generati all'installazione (`entropy.bin`). Il file delle
  impostazioni contiene solo il *nome* di ogni segreto.
- La cartella dati (`C:\ProgramData\SmtpRelay`) è riservata a **SYSTEM e Administrators** (più l'account del
  servizio, quando non è LocalSystem) da `SmtpRelay.exe install` e dal setup. È importante: per impostazione
  predefinita qualsiasi utente locale può leggere e creare file sotto `C:\ProgramData`.
- Se sposti la cartella dei log o quella dei messaggi non consegnati fuori dalla cartella dati, limita tu i suoi
  permessi: solo la cartella dati viene protetta automaticamente.
- DPAPI in ambito macchina protegge dalla copia dei file su un altro computer e dagli utenti senza diritti di
  amministratore sul server. Non protegge da un amministratore locale.
- Uno store dei segreti danneggiato viene segnalato come errore e non viene mai trattato come vuoto.

### Log e dati dei messaggi

- L'**audit** registra indirizzo del client, mittente e destinatari di busta, dimensione, oggetto ed esito. Non
  registra mai il corpo. Il testo che arriva dalla rete viene ripulito da caratteri di controllo e a capo prima di
  essere scritto: un mittente non può falsificare righe di log o di audit.
- I **messaggi non consegnati** vengono salvati per intero (`failed\*.eml`) dentro la cartella dati protetta ed
  eliminati dopo il numero di giorni configurato. Disattiva lo spool se non devi conservare il contenuto dei
  messaggi sul server.
- Segreti e token di accesso non vengono mai scritti nei log.

### File del programma, setup e aggiornamenti

- Ogni file del programma, il setup e il programma di disinstallazione hanno una **firma Authenticode di
  NEXQ S.r.l.** (certificato autofirmato, thumbprint `989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8`) con marca temporale.
- Il servizio è registrato con il **percorso dell'eseguibile tra virgolette**, in `Program Files`, dove possono
  scrivere solo gli amministratori.
- La console cerca gli aggiornamenti solo su `api.github.com`, tra le release di questo repository. Un setup viene
  scaricato solo dai download delle release di questo repository, in una cartella in cui possono scrivere solo gli
  amministratori, e viene eseguito solo se **(a)** il suo SHA-256 corrisponde a quello che GitHub pubblica per il
  file e **(b)** la sua firma Authenticode è integra ed è stata fatta con il certificato NEXQ (o con il certificato
  che ha firmato la console in esecuzione). Un setup che non supera un controllo viene eliminato.
- **Nessuna telemetria.** Il servizio contatta lo smart host e, in modalità Microsoft 365,
  `login.microsoftonline.com`. La console contatta `api.github.com` e `github.com` per gli aggiornamenti, e il
  controllo automatico si può disattivare.

### Monitoraggio (SNMP)

- L'agente SNMP integrato è **spento per impostazione predefinita**. È in sola lettura: espone il numero di
  messaggi consegnati e falliti oggi e quattro oggetti del gruppo system standard, e non permette di scrivere nulla.
- Risponde solo agli indirizzi in `Snmp.AllowedManagers` (un elenco vuoto non risponde a nessuno) che presentano
  la community, conservata nell'archivio cifrato dei segreti e confrontata in tempo costante. Ogni altra richiesta
  è scartata senza risposta, così l'agente non si rivela a una scansione.
- SNMP v1 e v2c non hanno cifratura: community e valori viaggiano in chiaro, e l'indirizzo di origine di una
  richiesta UDP può essere falsificato. Tieni l'agente su una rete di gestione. SNMPv3 non è implementato.
- Le richieste malformate sono scartate; la dimensione di una richiesta e il numero di variabili sono limitati.

### Limiti noti

- Il servizio gira come **LocalSystem** per impostazione predefinita, per poter ascoltare sulla porta 25 e leggere
  l'archivio certificati del computer senza altre configurazioni. Puoi eseguirlo con un account dedicato
  (`SmtpRelay.exe install --account`); all'account servono il diritto di accesso come servizio e l'accesso alla
  chiave privata del certificato TLS.
- **Non c'è una coda**: quando lo smart host non è raggiungibile il client riceve `451` e deve riprovare. Gli
  apparati che non riprovano perdono il messaggio, salvo la copia nella cartella `failed`.
- Il TLS in ingresso è facoltativo e disattivato per impostazione predefinita: nel primo tratto, dall'apparato al
  relay, la posta viaggia in chiaro se non configuri un certificato.
- Un certificato di firma del codice autofirmato non dà reputazione presso SmartScreen o gli antivirus, e
  considerarlo attendibile è una decisione che ogni amministratore prende dopo averne controllato il thumbprint.
