using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpRelay.Smtp;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpResponse = SmtpServer.Protocol.SmtpResponse;
using SmtpServer.Storage;

namespace SmtpRelay.Tests;

/// <summary>
/// Runs the real listener and the real sender against a fake smart host on the loopback interface:
/// client -> relay -> smart host.
/// </summary>
public sealed class RelayEndToEndTests : IAsyncLifetime
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("smtprelay-e2e-").FullName;
    private readonly FakeSmartHost _smartHost = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly DeliveryCounters _counters = new();
    private SmtpRelayHostedService? _relay;
    private int _relayPort;

    public Task InitializeAsync() => _smartHost.StartAsync();

    public async Task DisposeAsync()
    {
        if (_relay is not null)
            await _relay.StopAsync(CancellationToken.None);
        await _smartHost.StopAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private async Task StartRelayAsync(Action<RelayOptions>? configure = null)
    {
        _relayPort = FreePort();
        var options = new RelayOptions
        {
            DataDirectory = _dataDir,
            Listener = { IpAddress = "127.0.0.1", Port = _relayPort },
            Security = { AllowedIps = { "127.0.0.1" } },
            Upstream =
            {
                Mode = UpstreamAuthMode.Anonymous,
                Host = "127.0.0.1",
                Port = _smartHost.Port,
                Security = UpstreamSecureMode.None,
                MaxAttempts = 1,
                TimeoutSeconds = 10
            }
        };
        configure?.Invoke(options);
        Assert.Empty(RelayOptionsValidator.Validate(options));

        var secrets = SecretStore.Open(options);
        var sender = new UpstreamSender(
            options.Upstream, secrets,
            new EntraTokenProvider(options.Upstream, secrets, NullLogger<EntraTokenProvider>.Instance),
            NullLogger<UpstreamSender>.Instance);

        _relay = new SmtpRelayHostedService(
            options, sender, secrets,
            new IpAllowList(options.Security.AllowedIps, options.Security.RejectWhenAllowListEmpty),
            _logs, NullLogger<SmtpRelayHostedService>.Instance, new FakeLifetime(), _counters);
        await _relay.StartAsync(CancellationToken.None);
        await WaitForPortAsync(_relayPort);
    }

    private static MimeMessage NewMessage(string subject = "Hello")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("printer@corp.example"));
        message.To.Add(MailboxAddress.Parse("alice@example.org"));
        message.Cc.Add(MailboxAddress.Parse("bob@example.org"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "Scanned document attached.\r\n.leading dot line\r\n" };
        return message;
    }

    private async Task<SmtpClient> ConnectAsync()
    {
        var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", _relayPort, SecureSocketOptions.None);
        return client;
    }

    [Fact]
    public async Task MessageIsForwardedWithItsEnvelopeAndWrittenToTheAuditTrail()
    {
        await StartRelayAsync();

        using (var client = await ConnectAsync())
        {
            // No authentication is offered: the relay trusts the allowlist, not a password.
            Assert.False(client.Capabilities.HasFlag(SmtpCapabilities.Authentication));
            await client.SendAsync(NewMessage("Scan from printer"));
            await client.DisconnectAsync(true);
        }

        var received = Assert.Single(_smartHost.Messages);
        Assert.Equal("printer@corp.example", received.From);
        Assert.Equal(new[] { "alice@example.org", "bob@example.org" }, received.To);
        Assert.Equal("Scan from printer", received.Message.Subject);
        Assert.Contains(".leading dot line", received.Message.TextBody);

        var audit = Assert.Single(_logs.Entries, e => e.Category == LoggingSetup.AuditSourceContext);
        Assert.StartsWith("DELIVERED client=127.0.0.1 user=- from=printer@corp.example to=alice@example.org,bob@example.org", audit.Message);
        Assert.Equal((1, 0), _counters.Today); // one message, whatever the number of recipients
    }

    [Fact]
    public async Task SeveralMessagesOnOneConnectionAreAllForwarded()
    {
        await StartRelayAsync();

        using var client = await ConnectAsync();
        for (int i = 0; i < 5; i++)
            await client.SendAsync(NewMessage("Message " + i));
        await client.DisconnectAsync(true);

        Assert.Equal(5, _smartHost.Messages.Count);
    }

    [Fact]
    public async Task AddressOutsideTheAllowlistIsDisconnectedBeforeTheBanner()
    {
        await StartRelayAsync(o => o.Security.AllowedIps = new() { "10.99.99.0/24" });

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _relayPort);
        tcp.ReceiveTimeout = 5000;
        var buffer = new byte[64];
        int read;
        try { read = await tcp.GetStream().ReadAsync(buffer); }
        catch (IOException) { read = 0; }

        Assert.Equal(0, read); // closed without a single byte: no banner, no information
        Assert.Empty(_smartHost.Messages);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("REFUSED"));
    }

    [Fact]
    public async Task EmptyAllowlistRefusesEveryone()
    {
        await StartRelayAsync(o => o.Security.AllowedIps.Clear());

        using var client = new SmtpClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync("127.0.0.1", _relayPort, SecureSocketOptions.None));
    }

    [Fact]
    public async Task PermanentRefusalFromTheSmartHostBecomes554AndTheMessageIsSpooled()
    {
        _smartHost.RejectRecipients = true;
        await StartRelayAsync();

        using var client = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(NewMessage()));
        Assert.Equal(554, (int)ex.StatusCode);

        var spooled = Assert.Single(Directory.GetFiles(Path.Combine(_dataDir, "failed"), "*.eml"));
        Assert.Contains("Scanned document attached.", File.ReadAllText(spooled));
        Assert.Contains(_logs.Entries, e => e.Category == LoggingSetup.AuditSourceContext && e.Message.StartsWith("FAILED") && e.Message.Contains("permanent=True"));
        Assert.Equal((0, 1), _counters.Today);
    }

    [Fact]
    public async Task SmartHostDownBecomes451SoThatTheClientRetries()
    {
        await StartRelayAsync(o => o.Upstream.Port = FreePort()); // nothing listens there

        using var client = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(NewMessage()));
        Assert.Equal(451, (int)ex.StatusCode);
        Assert.Contains(_logs.Entries, e => e.Category == LoggingSetup.AuditSourceContext && e.Message.Contains("permanent=False"));
    }

    [Fact]
    public async Task TransientErrorIsRetriedAndThenDelivered()
    {
        _smartHost.TransientFailures = 1;
        await StartRelayAsync(o => { o.Upstream.MaxAttempts = 3; o.Upstream.RetryBaseDelaySeconds = 0; });

        using var client = await ConnectAsync();
        await client.SendAsync(NewMessage());

        Assert.Single(_smartHost.Messages);
    }

    [Fact]
    public async Task TooManyRecipientsAreRefused()
    {
        await StartRelayAsync(o => o.Listener.MaxRecipients = 1);

        using var client = await ConnectAsync();
        await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(NewMessage())); // two recipients
        Assert.Empty(_smartHost.Messages);
    }

    [Fact]
    public async Task OversizedMessageIsRefused()
    {
        await StartRelayAsync(o => o.Listener.MaxMessageSizeBytes = 2048);

        var message = NewMessage();
        message.Body = new TextPart("plain") { Text = new string('x', 20_000) };

        using var client = await ConnectAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(message));
        Assert.Empty(_smartHost.Messages);
    }

    [Fact]
    public async Task SubjectCannotForgeAuditEntries()
    {
        await StartRelayAsync();

        var message = NewMessage();
        // An encoded word that decodes to a line break followed by a fake audit entry.
        message.Headers.Replace(HeaderId.Subject, "=?utf-8?B?" + Convert.ToBase64String("ok\r\n2026-01-01 DELIVERED client=6.6.6.6"u8.ToArray()) + "?=");

        using var client = await ConnectAsync();
        await client.SendAsync(message);

        var audit = Assert.Single(_logs.Entries, e => e.Category == LoggingSetup.AuditSourceContext);
        Assert.DoesNotContain('\n', audit.Message);
        Assert.DoesNotContain('\r', audit.Message);
    }

    [Fact]
    public async Task SessionLimitClosesExtraConnections()
    {
        await StartRelayAsync(o => o.Listener.MaxConcurrentSessions = 1);

        using var first = await ConnectAsync(); // holds the only slot
        using (var second = new SmtpClient())
            await Assert.ThrowsAnyAsync<Exception>(() => second.ConnectAsync("127.0.0.1", _relayPort, SecureSocketOptions.None));

        await first.DisconnectAsync(true);

        // The slot is released when the first session ends.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var third = await ConnectAsync();
                await third.DisconnectAsync(true);
                break;
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task CredentialsAreNeverSentOverAClearTextConnection()
    {
        // StartTlsWhenAvailable against a server without STARTTLS falls back to clear text:
        // even with a stored password, the sender must refuse to authenticate.
        var options = new RelayOptions
        {
            DataDirectory = _dataDir,
            Upstream =
            {
                Mode = UpstreamAuthMode.Basic,
                Host = "127.0.0.1",
                Port = _smartHost.Port,
                Security = UpstreamSecureMode.StartTlsWhenAvailable,
                Basic = { Username = "relay@example.org" }
            }
        };
        var secrets = SecretStore.Open(options);
        secrets.Set(options.Upstream.Basic.PasswordSecret, "secret");
        var sender = new UpstreamSender(
            options.Upstream, secrets,
            new EntraTokenProvider(options.Upstream, secrets, NullLogger<EntraTokenProvider>.Instance),
            NullLogger<UpstreamSender>.Instance);

        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() => sender.VerifyConnectionAsync(CancellationToken.None));
        Assert.True(ex.IsPermanent);
        Assert.Contains("not encrypted", ex.Message);
    }

    [Theory]
    [InlineData(UpstreamSecureMode.Auto, 465, SecureSocketOptions.SslOnConnect)]
    [InlineData(UpstreamSecureMode.Auto, 587, SecureSocketOptions.StartTls)]
    [InlineData(UpstreamSecureMode.Auto, 25, SecureSocketOptions.StartTls)]
    [InlineData(UpstreamSecureMode.None, 25, SecureSocketOptions.None)]
    public void AutoNeverFallsBackToClearText(UpstreamSecureMode mode, int port, SecureSocketOptions expected)
        => Assert.Equal(expected, UpstreamSender.MapSecure(new UpstreamOptions { Security = mode, Port = port }));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                // Connects without sending anything; the relay may drop it, which is fine.
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }
        throw new TimeoutException("The relay did not start listening.");
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Entries);
        public void Dispose() { }

        private sealed class Capturing(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }

    private sealed record Received(string From, string[] To, MimeMessage Message);

    /// <summary>Minimal smart host: accepts anonymous mail and keeps what it receives.</summary>
    private sealed class FakeSmartHost
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _run;

        public int Port { get; } = FreePort();
        public ConcurrentQueue<Received> Messages { get; } = new();
        public bool RejectRecipients { get; set; }
        public int TransientFailures;

        public async Task StartAsync()
        {
            var options = new SmtpServerOptionsBuilder()
                .ServerName("fake-smarthost")
                .Endpoint(e => e.Endpoint(new IPEndPoint(IPAddress.Loopback, Port)))
                .Build();
            var services = new SmtpServer.ComponentModel.ServiceProvider();
            services.Add(new Store(this));
            services.Add(new Filter(this));
            _run = new SmtpServer.SmtpServer(options, services).StartAsync(_cts.Token);
            await WaitForPortAsync(Port);
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            try { if (_run is not null) await _run; } catch (OperationCanceledException) { }
        }

        private sealed class Filter(FakeSmartHost owner) : MailboxFilter
        {
            public override Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
                => Task.FromResult(!owner.RejectRecipients);
        }

        private sealed class Store(FakeSmartHost owner) : MessageStore
        {
            public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
            {
                if (Interlocked.Decrement(ref owner.TransientFailures) >= 0)
                    return new SmtpResponse((SmtpReplyCode)451, "4.3.0 try again");

                using var stream = new MemoryStream(buffer.ToArray());
                var message = await MimeMessage.LoadAsync(stream, cancellationToken);
                owner.Messages.Enqueue(new Received(
                    transaction.From.User + "@" + transaction.From.Host,
                    transaction.To.Select(t => t.User + "@" + t.Host).ToArray(),
                    message));
                return SmtpResponse.Ok;
            }
        }
    }
}
