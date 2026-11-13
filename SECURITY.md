<p align="center"><b>English</b> | <a href="SECURITY_IT.md">Italiano</a></p>

# Security policy

## Reporting a vulnerability

Please report vulnerabilities **privately**. Do not open a public issue, do not publish details before a fix is
available, and do not test against systems that you do not own or are not authorized to test.

**How to report**: use GitHub private vulnerability reporting for this repository: open the
[Security tab](https://github.com/NEXQsrl/SMTP-Relay/security) and choose **Report a vulnerability**
([direct link](https://github.com/NEXQsrl/SMTP-Relay/security/advisories/new)). The report is visible only to the
maintainers until an advisory is published. If you cannot use GitHub, contact NEXQ through
[www.nexq.it](https://www.nexq.it) and ask for a private channel, without including the details in the first message.

Please include:

- the affected version, and how the relay is configured (authentication mode, inbound TLS, service account);
- the steps to reproduce it, and the impact you observed or expect;
- whether credentials, tokens, message content or audit data may be exposed or altered.

Please give the maintainers reasonable time to investigate and publish a fix before any public disclosure.

### Scope

**In scope**: the service, the management console, the setup, the update mechanism, the handling of secrets, the IP
allowlist and the SMTP handling.

**Out of scope**:

- vulnerabilities in Windows or in third-party dependencies (report them upstream, and tell us if SMTP Relay is
  affected);
- denial of service that needs network-level flooding, or that comes from an address you put in the allowlist;
- findings that need local administrator rights on the server. An administrator is trusted by design: they can read
  the secrets and control the service.

## Supported versions

Security fixes are shipped in the next release; only the latest release receives them.

| Version | Security fixes |
|---|---|
| 2.x (latest) | Yes |
| 1.x | No: update to the latest release |

SMTP Relay **has not had an independent security review**. Do not expose the listening port to the Internet.

## Security model in brief

### Who may send

- The **IP allowlist** is the access control of the relay. It is applied when the connection is accepted: a client
  that is not allowed is disconnected before any SMTP greeting. A client whose address cannot be determined is
  refused.
- The default is **fail-closed**: an empty list refuses everyone. Accepting everyone needs two explicit choices
  (`RejectWhenAllowListEmpty = false` with an empty list, or a `/0` network), and both are flagged as *open relay* in
  the console, in `show-config` and in the log.
- By default clients are **not authenticated with a password**: anyone who can send from an allowed address can
  relay mail, as with the IIS SMTP relay. Keep the allowlist narrow, and use single addresses rather than whole
  networks where you can.
- **Sign-in (SMTP AUTH PLAIN / LOGIN) is optional and off by default.** The users belong to the relay; they are not
  Windows accounts. Each password is kept as a salted PBKDF2-SHA256 hash (200,000 iterations) in `users.json` in the
  data folder, never in clear text and never in the logs. A password is accepted only on a session encrypted with
  STARTTLS, unless you turn on `AllowWithoutTls`: then user names and passwords travel in clear text, and the
  console, `show-config` and the log say so.
- With `AllowFromAnyAddress` the allowlist no longer hides the relay: every computer that reaches the port gets
  the SMTP greeting and can try passwords. Guessing is slowed down (one second per wrong password, three attempts
  per connection, ten failures in ten minutes block the address for the rest of that time) but not made
  impossible: use long random passwords and keep the port off the Internet. The block is per address, so a device
  with a wrong password can lock out other devices behind the same address.
- Limits on message size, recipients, concurrent sessions and idle time bound what one client can consume. Messages
  are held in memory while they are forwarded: size the limits to the memory of the server.

### Towards the smart host

- **Encryption is enforced.** `StartTls` and `SslOnConnect` always encrypt; `Auto` means implicit TLS on port 465
  and required STARTTLS otherwise. `None` and `StartTlsWhenAvailable` are refused when credentials are configured,
  unless `AllowInsecureAuthentication` is set. Independently of the configuration, the sender checks that the
  channel is encrypted right before it authenticates, so a STARTTLS downgrade cannot leak a password or a token.
- The certificate of the smart host is validated (name, chain, revocation) with the Windows trust store.
- In Microsoft 365 mode the relay holds a **client secret**, not a mailbox password. Limit what the application can
  do: `SMTP.SendAsApp` only, and mailbox permission on the sending mailbox only.

### Secrets

- Passwords, client secrets and the password of the `.pfx` file are stored in `secrets.dat`, each one encrypted with
  **Windows DPAPI (machine scope)** plus 32 random bytes generated at installation (`entropy.bin`). The settings file
  contains only the *name* of each secret.
- The data folder (`C:\ProgramData\SmtpRelay`) is restricted to **SYSTEM and Administrators** (plus the service
  account, when it is not LocalSystem) by `SmtpRelay.exe install` and by the setup. This matters: by default any
  local user can read and create files under `C:\ProgramData`.
- If you move the log folder or the folder of undelivered messages outside the data folder, restrict its
  permissions yourself: only the data folder is protected automatically.
- DPAPI machine scope protects against copying the files to another computer and against users without
  administrator rights on the server. It does not protect against a local administrator.
- A damaged secret store is reported as an error and is never treated as empty.

### Logs and message data

- The **audit trail** records client address, envelope sender and recipients, size, subject and outcome. It never
  records the body. Text that comes from the network is cleaned of control characters and line breaks before it is
  written, so a sender cannot forge log or audit entries.
- **Undelivered messages** are saved in full (`failed\*.eml`) inside the protected data folder and deleted after the
  configured number of days. Turn the spool off if you must not keep message content on the server.
- Secrets and access tokens are never logged.

### Program files, setup and updates

- Every program file, the setup and the uninstaller are **Authenticode-signed by NEXQ S.r.l.** (self-signed
  certificate, thumbprint `989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8`) and time-stamped.
- The service is registered with a **quoted image path**, in `Program Files`, where only administrators can write.
- The console looks for updates only on `api.github.com`, in the releases of this repository. A setup is downloaded
  only from the release downloads of this repository, into a folder only administrators can write to, and is run
  only if **(a)** its SHA-256 matches the one GitHub publishes for the file and **(b)** its Authenticode signature is
  intact and was made with the NEXQ certificate (or with the certificate that signed the running console). A setup
  that fails any check is deleted.
- **No telemetry.** The service contacts the smart host and, in Microsoft 365 mode, `login.microsoftonline.com`.
  The console contacts `api.github.com` and `github.com` for updates, and you can turn the automatic check off.

### Monitoring (SNMP)

- The built-in SNMP agent is **off by default**. It is read-only: it exposes the number of messages delivered and
  failed today and four objects of the standard system group, and nothing can be written through it.
- It answers only addresses in `Snmp.AllowedManagers` (an empty list answers nobody) that present the community,
  which is kept in the encrypted secret store and compared in constant time. Any other request is dropped without
  an answer, so the agent does not reveal itself to a scan.
- An entry of `Snmp.AllowedManagers` can be a DNS name. The agent then trusts your DNS: whoever can change the
  record of that name can make the agent answer another address (the community is still required). The names are
  resolved at start and every five minutes; a name that no longer resolves keeps its last addresses until the
  service is restarted. Use IP addresses where the DNS is not under your control.
- SNMP v1 and v2c have no encryption: the community and the values travel in clear text, and the source address
  of a UDP request can be forged. Keep the agent on a management network. SNMPv3 is not implemented.
- Malformed requests are discarded; the size of a request and the number of variables are bounded.

### Known limits

- The service runs as **LocalSystem** by default, so that it can listen on port 25 and read the machine certificate
  store without further setup. You can run it under a dedicated account (`SmtpRelay.exe install --account`); the
  account then needs the right to log on as a service and access to the private key of the TLS certificate.
- There is **no queue**: when the smart host is not reachable the client receives `451` and must retry. Devices
  that do not retry lose the message, except for the copy in the `failed` folder, which an administrator can send
  again from the Undelivered page of the console.
- A **backup file** (`.srbackup`) holds every stored secret in clear text and the password hashes of the users,
  encrypted with AES-256-GCM under a key derived from the backup password (PBKDF2-SHA256, 600,000 iterations). Its
  protection is the password: choose a long one, and treat the file like the secrets it contains. The first lines
  of the file (date, computer name, number of secrets and users) are not encrypted.
- Inbound TLS is optional and off by default: on the first hop, device to relay, mail travels in clear text unless
  you configure a certificate.
- A self-signed code signing certificate gives no reputation with SmartScreen or antivirus products, and trusting it
  is a decision each administrator takes after checking the thumbprint.
