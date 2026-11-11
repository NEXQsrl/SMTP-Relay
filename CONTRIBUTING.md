# Contributing

Thank you for your interest in NEXQ SMTP Relay. Issues and pull requests are welcome.

By contributing you agree that your contribution is licensed to NEXQ S.r.l. under the terms of the project
([PolyForm Shield License 1.0.0](LICENSE)) and that NEXQ may also distribute it under other terms.

## Before you start

- For a bug, open an issue with the version, the relevant lines of the log (remove addresses and subjects you do not
  want to publish) and the steps to reproduce it.
- For a new feature, open an issue first and describe the use case. The relay is deliberately small.
- For a vulnerability, **do not open an issue**: follow [SECURITY.md](SECURITY.md).

## Rules

1. **Never weaken a fail-closed default.** An empty allowlist refuses everyone; credentials do not travel in clear
   text; an update that cannot be verified is not installed. A change that relaxes one of these needs an explicit
   setting, off by default, and a warning in the console and in `show-config`.
2. **Never log a secret, a token or a message body.** Text that comes from the network goes through
   `LogSanitizer.Clean` before it reaches a log.
3. **Report only what was verified.** The console and the command line must not show a success that was not
   observed (for example "running" right after asking the service to start, without checking).
4. **English for everything the administrator reads**: interface, command line, log messages, SMTP replies. Plain
   words, complete sentences, no jargon that the Windows administrator of a small company would not know.
   Documentation for users is kept in English and Italian (`README.md` / `README_IT.md`, `SECURITY.md` /
   `SECURITY_IT.md`).
5. **Add a test** for what you change. `tests/SmtpRelay.Tests` runs the real listener and the real sender against a
   test smart host: use it for anything that touches the SMTP path.
6. **No new dependency without a reason.** Every package is one more thing to audit and to keep updated.

## Building and testing

```powershell
dotnet build SMTPRelay.slnx
dotnet test SMTPRelay.slnx
dotnet run --project src/SmtpRelay.Service      # console mode, port 2525 (appsettings.Development.json)
dotnet run --project src/SmtpRelay.Manager      # the management console, not elevated in Debug
```

The pictures in the README are rendered from the real interface with sample data (Debug build only):

```powershell
dotnet run --project src/SmtpRelay.Manager -- --screenshots assets\screenshots
```

A release (self-contained binaries, signatures, setup, zip, checksums) is built by `build\Release.proj`; see the
comment at the top of that file.
