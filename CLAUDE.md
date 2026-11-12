# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NEXQ SMTP Relay: a Windows SMTP relay in C# / .NET 10, meant to replace the IIS 6 SMTP relay. It accepts mail from
IP-allowlisted internal senders and forwards it to a smart host: Microsoft 365 or Azure Communication Services via
OAuth2 client credentials (XOAUTH2, client secret or certificate), any SMTP server via Basic auth, or an anonymous
connector. Windows-only (`net10.0-windows`, DPAPI,
Windows service APIs, WPF). Source-available under PolyForm Shield 1.0.0; the repository is `NEXQsrl/SMTP-Relay`.

**Language convention.** Everything the administrator reads is in **English**: console UI, CLI output, log
messages, SMTP replies, code comments. User documentation is kept in **English and Italian** in parallel
(`README.md` / `README_IT.md`, `SECURITY.md` / `SECURITY_IT.md`): change both. Do not add PowerShell scripts to the
repository; build logic lives in `build/Release.proj` (MSBuild) and in the workflows.

## Commands

```powershell
dotnet build SMTPRelay.slnx
dotnet test SMTPRelay.slnx
dotnet test tests/SmtpRelay.Tests --filter "FullyQualifiedName~RelayEndToEndTests.MessageIsForwarded"   # one test

dotnet run --project src/SmtpRelay.Service      # relay in the console, port 2525 (appsettings.Development.json)
dotnet run --project src/SmtpRelay.Manager      # management console; Debug builds are not elevated

# README pictures: rendered from the real UI with sample data (Debug only)
dotnet run --project src/SmtpRelay.Manager -- --screenshots assets\screenshots

# Release: self-contained publish, signing, Inno Setup installer, zip, SHA256SUMS -> artifacts\release
dotnet msbuild build\Release.proj -restore -t:Release -p:SignThumbprint=<thumbprint in CurrentUser\My>
```

The release build needs Inno Setup 6 (`ISCC.exe`); `signtool.exe` comes from a NuGet `PackageDownload` in
`build/Release.proj`. The version is `<Version>` in `Directory.Build.props` and must be bumped for every release
(the console compares it with the GitHub release tag `v<Version>`).

Workflows: `.github/workflows/ci.yml` builds, tests and fails on vulnerable packages for every push and PR.
`.github/workflows/release.yml` is manual (`workflow_dispatch`) and refuses to publish without the signing secrets
`SIGN_PFX_BASE64` / `SIGN_PFX_PASSWORD`. Pushing to `master` does not publish anything.

## Architecture

Three projects share one installation folder (the release publishes them self-contained into the same directory):

- **`src/SmtpRelay.Core`** (library, root namespace `SmtpRelay`): `Configuration/` (`RelayOptions` and friends,
  `ConfigLoader` load + atomic save, `RelayOptionsValidator` errors and warnings), `Security/` (`SecretStore`,
  `SecretProtector`, `MachineKey`, `WindowsAcl`, `IpAllowList`, `LogSanitizer`, `CertificateFinder`), `Relay/`
  (`UpstreamSender`, `EntraTokenProvider`), `Monitoring/` (`DeliveryCounters`, `AuditTrail`, `SnmpResponder`,
  `RelayMib`). Anything both executables need goes here.
- **`src/SmtpRelay.Service`** (`SmtpRelay.exe`): dual-mode entry point. If `args[0]` is a CLI verb
  (`Cli/CommandLineHandler.cs`) it runs the command and exits; otherwise it validates the config fail-fast and runs
  `Smtp/SmtpRelayHostedService` under the generic host with `AddWindowsService`.
- **`src/SmtpRelay.Manager`** (`SmtpRelayManager.exe`, WPF, `requireAdministrator` in Release): one window,
  `ViewModels/MainViewModel.cs` over the Core types, `Services/` for service control, log reading, the manager's own
  activity log and updates.

Inbound message flow, wired in `SmtpRelayHostedService` on the `SmtpServer` library's component model:

1. `Smtp/AllowListEndpointListener.cs` wraps the socket listener. Connections outside `IpAllowList`, with an
   unknown remote address, or above `MaxConcurrentSessions` are closed before the SMTP banner. After dropping a
   connection **both** `EndpointListener.RemoteEndPointKey` and `LocalEndPointKey` must be removed from the session
   context, or the library throws on the next accept and that connection hangs.
2. `Smtp/RelayMailboxFilter.cs` enforces recipient and size limits and required STARTTLS.
3. `Smtp/RelayMessageStore.cs` hands the message to the sender, maps the outcome to the SMTP reply (250 / 451 /
   554), writes the audit line and spools failed messages as `.eml`. Unclassified exceptions are treated as
   transient so a defect never loses mail silently.
4. `Relay/UpstreamSender.cs` delivers via MailKit with linear back-off. Transient vs permanent is signalled with
   `UpstreamDeliveryException`. Credentials are sent only when `client.IsSecure` (or `AllowInsecureAuthentication`).

Upstream authentication: `UpstreamOptions.Mode` selects Basic, Anonymous, Microsoft365 or
AzureCommunicationServices. The two Microsoft modes share `EntraApplicationOptions` (tenant, client, `Credential` =
ClientSecret or Certificate from `LocalMachine\My`) and `EntraTokenProvider`; they differ only in the scope, the
default host and the XOAUTH2 user name (`UpstreamOptions.TokenUserName`: the mailbox UPN, or the ACS SMTP Username).

Monitoring: `RelayMessageStore` updates `DeliveryCounters` (today's delivered / failed, seeded from the audit trail
at start) next to every audit line. `Snmp/SnmpAgentService` (only when `Snmp.Enabled`) serves them over UDP with
the hand-written, read-only v1/v2c `SnmpResponder`; it has its own port (1161) because the Windows SNMP service
owns 161, and a failure there must never stop the relay. OIDs come from `RelayMib` and
`AppInfo.SnmpEnterpriseNumber` (a placeholder until IANA assigns one) and must match `mib/NEXQ-SMTP-RELAY-MIB.txt`.

Configuration: `appsettings.json` next to the executables -> `appsettings.{env}.json` -> env vars prefixed
`SMTPRELAY_` (`__` separator), bound to the `SmtpRelay` section. Read once at startup: changes need a service
restart. The one exception is secrets: `SecretStore` reloads `secrets.dat` when its timestamp changes, so a secret
stored from the console or the CLI reaches the running service.

Secrets are never in `appsettings.json`; config references them by name and `SecretStore` resolves them from
`{DataDirectory}\secrets.dat` (DPAPI LocalMachine + per-install entropy in `entropy.bin`). The data directory
(default `C:\ProgramData\SmtpRelay`) is hardened to SYSTEM/Administrators (+ the service account) by
`SmtpRelay.exe install` and at service start when running as LocalSystem.

Logging (`LoggingSetup.cs`, Serilog): application log `smtp-relay-*.log`, audit trail `audit-*.log` (events from
the `LoggingSetup.AuditSourceContext` logger only: use it for delivery events and nothing else), Windows event log
(Warning+, source `SmtpRelay`, only when running as a service). The console writes `manager-yyyyMM.log` via
`ManagerLog`. Any text that comes from the network goes through `LogSanitizer.Clean` before logging.

Updates (`Manager/Services/UpdateService.cs`): the console reads `releases/latest` from the GitHub API, downloads
`SmtpRelay-Setup-*.exe` only from this repository's release downloads into `{DataDirectory}\updates` (hardened),
then requires the SHA-256 published by GitHub and an intact Authenticode signature whose signer thumbprint is the
NEXQ certificate (`UpdateService.PublisherThumbprint`) or the signer of the running console
(`Services/Authenticode.cs`, WinVerifyTrust). The setup is started with `/SILENT /RELAUNCH=1`;
`installer/SmtpRelay.iss` stops the service, replaces the files, restarts the service if it was running, and reopens
the console.

## Things that are easy to get wrong

- The WPF project must **not** set `InvariantGlobalization` (WPF bindings throw at startup). The culture is forced
  to en-US in `App.OnStartup` instead.
- `SmtpRelay.exe status` exit codes are part of the installer contract: 0 = running, 3 = not running, 4 = not
  installed (`installer/SmtpRelay.iss` uses 0 to decide whether to restart the service after an upgrade).
- A service process that exits before the generic host has answered the service control manager is shown by
  Windows as "error 1053", with no reason. Every early exit in `Program.cs` therefore goes through
  `ServiceStartupFailure.ReportAsync` when running as a service (it logs, runs a minimal host, then stops).
- Folder names never contain the company name: the program is in `{autopf}\SMTP Relay`, the data in
  `C:\ProgramData\SmtpRelay`. `installer/SmtpRelay.iss` moves a 2.0.0 installation out of `{autopf}\NEXQ\SMTP Relay`.
- `appsettings.json` is installed with `onlyifdoesntexist uninsneveruninstall`: new settings must have sensible
  defaults in the options classes, because existing installations keep their old file.
- The tests (`tests/SmtpRelay.Tests`) run the real listener against a fake smart host on loopback; SMTP-path
  changes should be covered there. `SnmpTests` queries the real agent with SharpSnmpLib (a test-only dependency:
  the product has no SNMP library). `TheReleaseSetupPassesTheUpdateVerification` only acts when
  `SMTPRELAY_SIGNED_SETUP` points to a built setup.
