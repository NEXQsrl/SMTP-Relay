# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Windows SMTP relay service in C# / .NET 10 (single project: `src/SmtpRelay.Service`, assembly name `SmtpRelay`, root namespace `SmtpRelay`). It accepts mail from IP-allowlisted internal senders and forwards it to a smart host — Microsoft 365 via OAuth2 client credentials (XOAUTH2) or any SMTP server via Basic auth. Windows-only (`net10.0-windows`, DPAPI, Windows service APIs).

**Language convention: the entire repo is in Italian** — README, code comments, log messages, CLI output. Keep new comments and user-facing strings in Italian to match.

There is no test project; verification is done via the built-in CLI diagnostics (`test-upstream`, `show-config`).

## Commands

```powershell
# Build
dotnet build -c Release

# Publish (framework-dependent; add -SelfContained to bundle the runtime)
.\publish.ps1
# equivalent: dotnet publish src/SmtpRelay.Service -c Release -o publish -r win-x64 --self-contained false

# Run locally as a console app (listens on port 2525 by default)
dotnet run --project src/SmtpRelay.Service
```

The published `SmtpRelay.exe` doubles as a management CLI. Verbs (defined in `Cli/CommandLineHandler.cs`): `install` / `uninstall` / `start` / `stop` / `status` (Windows service lifecycle, require admin), `set-credential` / `remove-credential` / `list-credentials` (DPAPI secret store), `test-upstream` (connect + authenticate against the smart host), `show-config` (effective config + validation), `help`.

Other scripts: `SmtpRelayConfigurator.ps1` (admin GUI to edit `appsettings.json`, set secrets, and run CLI verbs), `FirmareDLL_EXE.ps1` (Authenticode-signs published exe/dll).

## Architecture

`Program.cs` is a dual-mode entry point: if `args[0]` is a CLI verb it dispatches to `CommandLineHandler` and exits; otherwise it validates config fail-fast (`RelayOptionsValidator`), builds a generic host with `AddWindowsService`, and runs `SmtpRelayHostedService`. `Cli/ConfigLoader.cs` builds configuration for both modes: `appsettings.json` next to the exe → `appsettings.{env}.json` → env vars prefixed `SMTPRELAY_` (`__` separator), all bound to the `SmtpRelay` section as `RelayOptions` (see `Configuration/`).

Inbound message flow, wired in `Smtp/SmtpRelayHostedService.cs` on the `SmtpServer` library's component model:

1. **`Smtp/AllowListEndpointListener.cs`** — wraps the socket listener; connections from IPs outside `Security/IpAllowList.cs` (CIDR, IPv4/IPv6) are closed *before* the SMTP banner. Fail-closed when the list is empty and `RejectWhenAllowListEmpty` is true.
2. **`Smtp/RelayMailboxFilter.cs`** — enforces recipient limits and rejects `MAIL FROM` on unencrypted sessions when TLS mode is `StartTlsRequired`.
3. **`Smtp/RelayMessageStore.cs`** — receives the message, hands it to the sender, maps outcomes to SMTP replies (250 delivered / 451 transient / 554 permanent), writes the audit log, and spools failed messages as `.eml` to `{DataDirectory}\failed` when enabled.
4. **`Relay/UpstreamSender.cs`** — delivers via MailKit with linear-backoff retries; transient vs permanent is signaled through `UpstreamDeliveryException`. In Microsoft365 mode, `Relay/Microsoft365TokenProvider.cs` acquires app-only tokens via MSAL.

Secrets are never in `appsettings.json` — config references them by name (e.g. `PasswordSecret`, `ClientSecretName`) and `Security/SecretStore.cs` resolves them from `secrets.dat`, encrypted with DPAPI LocalMachine plus per-install entropy (`Security/MachineKey.cs` → `entropy.bin`); both files get SYSTEM/Administrators-only ACLs via `Security/WindowsAcl.cs`. Blobs are machine-bound and cannot be copied between hosts.

Logging (`LoggingSetup.cs`) is Serilog with two sinks under `{DataDirectory}\logs` (default `C:\ProgramData\SmtpRelay`): the application log and a separate audit trail (`audit-*.log`) filtered by the `LoggingSetup.AuditSourceContext` source context — use that logger for delivery events only.

Note: `appsettings.json` at the repo root is a deployment sample; the one that ships is `src/SmtpRelay.Service/appsettings.json`. They are not kept in sync automatically.
