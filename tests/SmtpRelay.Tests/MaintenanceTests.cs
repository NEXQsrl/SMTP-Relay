using System.Net;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Relay;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

/// <summary>Activity of the senders, backup of the settings and the spool of undelivered messages.</summary>
public sealed class MaintenanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smtprelay-maint-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ----- senders ---------------------------------------------------------------------------------------

    [Fact]
    public void TheAuditTrailTellsWhoSentHowMuchAndWhen()
    {
        var logs = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "audit-20261110.log"), new[]
        {
            "2026-11-10 08:00:00.000 +01:00 DELIVERED client=192.168.10.44 user=- from=a@x.example to=b@y.example size=10 subject=One durationMs=5",
            "2026-11-10 09:30:00.000 +01:00 FAILED client=192.168.10.44 user=- from=a@x.example to=b@y.example size=10 subject=client=10.9.9.9 in the subject permanent=False reason=4.4.1"
        });
        File.WriteAllLines(Path.Combine(logs, "audit-20261112.log"), new[]
        {
            "2026-11-12 16:35:57.077 +01:00 DELIVERED client=192.168.10.44 user=- from=a@x.example to=b@y.example size=264 subject=Two durationMs=1205",
            "2026-11-12 17:00:00.000 +01:00 DELIVERED client=192.168.10.61 user=nas from=n@x.example to=b@y.example size=10 subject=Three durationMs=5",
            "2026-11-12 17:05:00.000 +01:00 DELIVERED client=10.20.4.12 user=- from=e@x.example to=b@y.example size=10 subject=Four durationMs=5",
            "2026-11-12 17:06:00.000 +01:00 DELIVERED client=::ffff:10.20.4.12 user=- from=e@x.example to=b@y.example size=10 subject=Five durationMs=5",
            "2026-11-12 17:07:00.000 +01:00 DELIVERED client=unknown user=- from=e@x.example to=b@y.example size=10 subject=Six durationMs=5",
            "not an audit line"
        });
        File.WriteAllText(Path.Combine(logs, "smtp-relay-20261112.log"), "2026-11-12 17:00:00.000 +01:00 [INF] SmtpRelay.Audit: DELIVERED client=172.16.0.1 user=- from=a to=b\n");

        var report = AuditTrail.ReadSenders(logs);

        Assert.Equal(new DateOnly(2026, 11, 10), report.Since);
        Assert.Equal(3, report.Senders.Count); // the application log and the line without an address are not counted

        var scanner = report.For("192.168.10.44");
        Assert.Equal((2, 1), (scanner.Delivered, scanner.Failed));
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T16:35:57.077+01:00"), scanner.LastSeen);

        var network = report.For("192.168.10.0/24"); // a network adds up its clients
        Assert.Equal((3, 1), (network.Delivered, network.Failed));
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T17:00:00+01:00"), network.LastSeen);

        Assert.Equal(2, report.For("10.20.4.12").Delivered); // also when seen through IPv6
        Assert.Equal((0, 0, null), report.For("192.168.99.1"));
        Assert.Equal((0, 0, null), report.For("not an address"));
        Assert.Empty(AuditTrail.ReadSenders(Path.Combine(_dir, "missing")).Senders);
    }

    // ----- backup ----------------------------------------------------------------------------------------

    private (string Config, RelayOptions Options) NewInstallation(string name)
    {
        var config = Path.Combine(_dir, name);
        Directory.CreateDirectory(config);
        var options = new RelayOptions
        {
            DataDirectory = Path.Combine(_dir, name + "-data"),
            Listener = { Port = 2525, ServerName = "relay.contoso.local" },
            Security = { AllowedIps = { "192.168.10.0/24", "10.20.4.12" }, Authentication = { Mode = InboundAuthMode.Optional, AllowWithoutTls = true } },
            Upstream = { Mode = UpstreamAuthMode.Basic, Host = "smtp.example.org", Basic = { Username = "relay@example.org" } }
        };
        ConfigLoader.Save(config, options);
        return (config, options);
    }

    [Fact]
    public void ABackupMovesSettingsSecretsAndUsersToAnotherInstallation()
    {
        var (sourceConfig, source) = NewInstallation("source");
        SecretStore.Open(source).Set("UpstreamPassword", "smart-host-p@ss");
        SecretStore.Open(source).Set("SnmpCommunity", "s3cret-community");
        RelayUserStore.Open(source).Set("scanner", "correct horse 1");
        RelayUserStore.Open(source).Set("old-device", "another password");
        RelayUserStore.Open(source).SetEnabled("old-device", false);

        var backup = Path.Combine(_dir, "relay" + SettingsBackup.FileExtension);
        var created = SettingsBackup.Create(backup, sourceConfig, "backup-password");
        Assert.Equal((2, 2, false), (created.Secrets, created.Users, created.UsesCertificates));

        // Nothing readable in the file.
        var text = File.ReadAllText(backup);
        Assert.DoesNotContain("smart-host-p@ss", text);
        Assert.DoesNotContain("s3cret-community", text);
        Assert.DoesNotContain("smtp.example.org", text);
        Assert.DoesNotContain("scanner", text);

        // The target starts with its own, different, content; everything of it is replaced.
        var targetConfig = Path.Combine(_dir, "target");
        Directory.CreateDirectory(targetConfig);
        var stale = new RelayOptions { DataDirectory = source.DataDirectory, Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "old.example.org" } };
        ConfigLoader.Save(targetConfig, stale);
        SecretStore.Open(source).Set("LeftOver", "must go away");

        var described = SettingsBackup.Describe(backup);
        Assert.Equal(Environment.MachineName, described.Computer);

        var restored = SettingsBackup.Restore(backup, targetConfig, "backup-password");
        Assert.Equal((2, 2), (restored.Secrets, restored.Users));

        var target = ConfigLoader.Load(targetConfig);
        Assert.Equal("smtp.example.org", target.Upstream.Host);
        Assert.Equal(2525, target.Listener.Port);
        Assert.Equal(new[] { "192.168.10.0/24", "10.20.4.12" }, target.Security.AllowedIps);
        Assert.Equal(InboundAuthMode.Optional, target.Security.Authentication.Mode);

        var secrets = SecretStore.Open(target);
        Assert.Equal("smart-host-p@ss", secrets.Get("UpstreamPassword"));
        Assert.Equal("s3cret-community", secrets.Get("SnmpCommunity"));
        Assert.False(secrets.Contains("LeftOver"));

        var users = RelayUserStore.Open(target);
        Assert.Equal("scanner", users.Verify("scanner", "correct horse 1"));
        Assert.Null(users.Verify("old-device", "another password")); // still disabled
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public void AWrongPasswordOrADamagedBackupChangesNothing()
    {
        var (sourceConfig, source) = NewInstallation("source");
        SecretStore.Open(source).Set("UpstreamPassword", "smart-host-p@ss");
        var backup = Path.Combine(_dir, "relay.srbackup");
        SettingsBackup.Create(backup, sourceConfig, "backup-password");

        var (targetConfig, target) = NewInstallation("target");
        var before = File.ReadAllText(Path.Combine(targetConfig, ConfigLoader.FileName));

        Assert.Throws<InvalidDataException>(() => SettingsBackup.Restore(backup, targetConfig, "not the password"));

        var damaged = Path.Combine(_dir, "damaged.srbackup");
        File.WriteAllText(damaged, File.ReadAllText(backup).Replace("\"Computer\": \"", "\"Computer\": \"x"));
        Assert.NotNull(SettingsBackup.Describe(damaged)); // the description is not protected...
        var tampered = File.ReadAllText(backup);
        int data = tampered.IndexOf("\"Data\": \"", StringComparison.Ordinal) + 12;
        tampered = tampered[..data] + (tampered[data] == 'A' ? 'B' : 'A') + tampered[(data + 1)..];
        File.WriteAllText(damaged, tampered);
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Restore(damaged, targetConfig, "backup-password")); // ...the content is

        File.WriteAllText(damaged, "{ \"Format\": \"something else\" }");
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Restore(damaged, targetConfig, "backup-password"));
        File.WriteAllText(damaged, "not json");
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Describe(damaged));

        Assert.Equal(before, File.ReadAllText(Path.Combine(targetConfig, ConfigLoader.FileName)));
        Assert.False(SecretStore.Open(target).Contains("UpstreamPassword"));
    }

    [Fact]
    public void ABackupNeedsARealPasswordAndSaysWhenCertificatesAreNeeded()
    {
        var (config, options) = NewInstallation("source");
        Assert.Throws<ArgumentException>(() => SettingsBackup.Create(Path.Combine(_dir, "x.srbackup"), config, "short"));
        Assert.False(File.Exists(Path.Combine(_dir, "x.srbackup")));

        options.Tls.Mode = InboundTlsMode.StartTlsOptional;
        options.Tls.CertificateThumbprint = "3F2A9C41D07B5E86A1C4F0B92D7E6A5538C1D4E9";
        ConfigLoader.Save(config, options);
        Assert.True(SettingsBackup.Create(Path.Combine(_dir, "x.srbackup"), config, "backup-password").UsesCertificates);
    }

    // ----- spool -----------------------------------------------------------------------------------------

    private static MimeMessage NewMessage(string id, string subject = "Scan")
    {
        var message = new MimeMessage { MessageId = id, Subject = subject };
        message.From.Add(MailboxAddress.Parse("scanner@corp.example"));
        message.To.Add(MailboxAddress.Parse("alice@example.org"));
        message.Body = new TextPart("plain") { Text = "Scanned document.\r\n" };
        return message;
    }

    private static SpoolRecord NewRecord(string reason = "4.4.1 Smart host not available", bool permanent = false) => new()
    {
        TimeUtc = DateTime.UtcNow,
        Client = "192.168.10.44",
        User = "-",
        From = "scanner@corp.example",
        Recipients = { "alice@example.org", "hidden@example.org" },
        Subject = "Scan",
        Permanent = permanent,
        Reason = reason
    };

    [Fact]
    public async Task AnUndeliveredMessageIsKeptWithItsEnvelopeAndReason()
    {
        var spool = new FailedSpool(Path.Combine(_dir, "failed"));
        Assert.Empty(spool.List());

        using (var message = NewMessage("one@corp.example"))
            await spool.SaveAsync(message, NewRecord());

        var listed = Assert.Single(spool.List());
        Assert.True(listed.HasEnvelope);
        Assert.Equal("192.168.10.44", listed.Record.Client);
        Assert.Equal(new[] { "alice@example.org", "hidden@example.org" }, listed.Record.Recipients); // the envelope, not only the headers
        Assert.Equal("4.4.1 Smart host not available", listed.Record.Reason);
        Assert.False(listed.Record.Permanent);
        Assert.Equal(1, spool.Count);

        using var loaded = FailedSpool.Load(listed);
        Assert.Equal("Scan", loaded.Subject);

        spool.Delete(listed);
        Assert.Empty(spool.List());
        Assert.Empty(Directory.GetFiles(spool.Directory));
    }

    [Fact]
    public async Task ARetryReplacesTheCopyAndADeliveryRemovesIt()
    {
        var spool = new FailedSpool(Path.Combine(_dir, "failed"));
        using var first = NewMessage("one@corp.example");
        using var second = NewMessage("two@corp.example");

        await spool.SaveAsync(first, NewRecord("first attempt"));
        await spool.SaveAsync(first, NewRecord("second attempt")); // the client tried again
        await spool.SaveAsync(second, NewRecord());
        Assert.Equal(2, spool.Count);
        Assert.Contains(spool.List(), m => m.Record.Reason == "second attempt");
        Assert.DoesNotContain(spool.List(), m => m.Record.Reason == "first attempt");

        Assert.Equal(1, spool.RemoveDelivered("one@corp.example")); // the third attempt went through
        Assert.Equal("two@corp.example", Assert.Single(spool.List()).Record.MessageId);
        Assert.Equal(0, spool.RemoveDelivered("one@corp.example"));
        Assert.Equal(0, spool.RemoveDelivered(null));
    }

    [Fact]
    public async Task ACopyOfAnOlderVersionIsListedFromItsHeadersAndOldCopiesExpire()
    {
        var folder = Path.Combine(_dir, "failed");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "failed-20261101-0011223344556677.eml");
        using (var message = NewMessage("legacy@corp.example", "Legacy"))
        {
            message.Cc.Add(MailboxAddress.Parse("bob@example.org"));
            await using var stream = File.Create(old);
            await message.WriteToAsync(stream);
        }
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));

        var spool = new FailedSpool(folder);
        using (var recent = NewMessage("recent@corp.example"))
            await spool.SaveAsync(recent, NewRecord());

        var legacy = Assert.Single(spool.List(), m => !m.HasEnvelope);
        Assert.Equal("scanner@corp.example", legacy.Record.From);
        Assert.Equal(new[] { "alice@example.org", "bob@example.org" }, legacy.Record.Recipients);
        Assert.Equal("Legacy", legacy.Record.Subject);

        Assert.Equal(1, spool.Cleanup(retentionDays: 30));
        Assert.True(Assert.Single(spool.List()).HasEnvelope);
        Assert.Equal(0, spool.Cleanup(retentionDays: 0)); // 0 keeps everything
    }

    [Fact]
    public void TheClientAddressIsReadOnlyFromItsOwnField()
        => Assert.Equal(IPAddress.Parse("192.168.10.44"), Assert.Single(ReadOne(
            "2026-11-10 09:30:00.000 +01:00 FAILED client=192.168.10.44 user=- from=a@x.example to=b@y.example size=10 subject=client=10.9.9.9 permanent=False reason=x")).Address);

    private IReadOnlyList<SenderActivity> ReadOne(string line)
    {
        var logs = Path.Combine(_dir, "one");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "audit-20261110.log"), line + "\n");
        return AuditTrail.ReadSenders(logs).Senders;
    }
}
