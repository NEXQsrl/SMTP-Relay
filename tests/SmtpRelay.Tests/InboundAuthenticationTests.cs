using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
using SmtpServer.Storage;
using SmtpResponse = SmtpServer.Protocol.SmtpResponse;

namespace SmtpRelay.Tests;

public sealed class RelayUserStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smtprelay-users-").FullName;
    private string FilePath => Path.Combine(_dir, "users.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AUserSignsInOnlyWithItsOwnPassword()
    {
        var store = new RelayUserStore(FilePath);
        store.Set("Scanner", "correct horse 1");
        store.Set("erp", "another secret");

        Assert.Equal("Scanner", store.Verify("Scanner", "correct horse 1"));
        Assert.Equal("Scanner", store.Verify("SCANNER", "correct horse 1")); // names are not case-sensitive
        Assert.Null(store.Verify("Scanner", "Correct horse 1"));             // passwords are
        Assert.Null(store.Verify("Scanner", "another secret"));
        Assert.Null(store.Verify("nobody", "correct horse 1"));
        Assert.Null(store.Verify("Scanner", ""));
        Assert.Null(store.Verify(null, null));
    }

    [Fact]
    public void ThePasswordIsNotWrittenToTheFile()
    {
        var store = new RelayUserStore(FilePath);
        store.Set("scanner", "correct horse 1");

        var text = File.ReadAllText(FilePath);
        Assert.Contains("scanner", text);
        Assert.DoesNotContain("correct horse", text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("correct horse 1")), text);
    }

    [Fact]
    public void TwoUsersWithTheSamePasswordHaveDifferentHashes()
    {
        var store = new RelayUserStore(FilePath);
        store.Set("a", "same password");
        store.Set("b", "same password");

        var hashes = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement
            .GetProperty("Users").EnumerateArray().Select(u => u.GetProperty("Hash").GetString()).ToList();
        Assert.Equal(2, hashes.Distinct().Count());
    }

    [Fact]
    public void ADisabledOrRemovedUserCannotSignIn()
    {
        var store = new RelayUserStore(FilePath);
        store.Set("scanner", "correct horse 1");

        Assert.True(store.SetEnabled("scanner", false));
        Assert.Null(store.Verify("scanner", "correct horse 1"));
        Assert.False(Assert.Single(store.Users).Enabled);

        Assert.True(store.SetEnabled("scanner", true));
        Assert.NotNull(store.Verify("scanner", "correct horse 1"));

        // Changing the password keeps the state of the user.
        store.SetEnabled("scanner", false);
        store.Set("scanner", "a new password");
        Assert.Null(store.Verify("scanner", "a new password"));

        Assert.True(store.Remove("scanner"));
        Assert.False(store.Remove("scanner"));
        Assert.Empty(store.Users);
    }

    [Fact]
    public void AChangeMadeByAnotherProcessIsSeenWithoutReopening()
    {
        var service = new RelayUserStore(FilePath);
        Assert.Null(service.Verify("scanner", "correct horse 1"));

        var console = new RelayUserStore(FilePath);
        console.Set("scanner", "correct horse 1");
        Assert.NotNull(service.Verify("scanner", "correct horse 1"));

        console.Remove("scanner");
        Assert.Null(service.Verify("scanner", "correct horse 1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("with\nnewline")]
    public void AWeakPasswordIsRefused(string password)
    {
        var store = new RelayUserStore(FilePath);
        Assert.Throws<ArgumentException>(() => store.Set("scanner", password));
        Assert.Empty(store.Users);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("-dash")]
    [InlineData("a\"b")]
    public void AnInvalidUserNameIsRefused(string name)
    {
        var store = new RelayUserStore(FilePath);
        Assert.Throws<ArgumentException>(() => store.Set(name, "correct horse 1"));
    }

    [Fact]
    public void ADamagedFileIsNotTreatedAsEmpty()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        Assert.Throws<InvalidDataException>(() => new RelayUserStore(FilePath));
    }

    [Fact]
    public void TooManyFailuresBlockTheAddressForAWhile()
    {
        var throttle = new FailedSignInThrottle(maxFailures: 3, window: TimeSpan.FromMilliseconds(300));
        throttle.RecordFailure("10.0.0.1");
        throttle.RecordFailure("10.0.0.1");
        Assert.False(throttle.IsBlocked("10.0.0.1"));
        throttle.RecordFailure("10.0.0.1");
        Assert.True(throttle.IsBlocked("10.0.0.1"));
        Assert.False(throttle.IsBlocked("10.0.0.2"));

        Thread.Sleep(400);
        Assert.False(throttle.IsBlocked("10.0.0.1"));

        throttle.RecordFailure("10.0.0.1");
        throttle.RecordFailure("10.0.0.1");
        throttle.Reset("10.0.0.1"); // a successful sign-in
        throttle.RecordFailure("10.0.0.1");
        Assert.False(throttle.IsBlocked("10.0.0.1"));
    }
}

/// <summary>Sign-in of the clients against the real listener, with a fake smart host behind it.</summary>
public sealed class InboundAuthenticationTests : IAsyncLifetime
{
    private const string Password = "correct horse 1";

    private readonly string _dataDir = Directory.CreateTempSubdirectory("smtprelay-auth-").FullName;
    private readonly CountingSmartHost _smartHost = new();
    private readonly AuditCapture _logs = new();
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

    private async Task<RelayUserStore> StartRelayAsync(Action<RelayOptions> configure)
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _relayPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

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
        configure(options);
        Assert.Empty(RelayOptionsValidator.Validate(options));

        var users = RelayUserStore.Open(options);
        users.Set("scanner", Password);

        var secrets = SecretStore.Open(options);
        var sender = new UpstreamSender(
            options.Upstream, secrets,
            new EntraTokenProvider(options.Upstream, secrets, NullLogger<EntraTokenProvider>.Instance),
            NullLogger<UpstreamSender>.Instance);

        _relay = new SmtpRelayHostedService(
            options, sender, secrets,
            new IpAllowList(options.Security.AllowedIps, options.Security.RejectWhenAllowListEmpty),
            _logs, NullLogger<SmtpRelayHostedService>.Instance, new NoLifetime(), new DeliveryCounters());
        await _relay.StartAsync(CancellationToken.None);

        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, _relayPort);
                return users;
            }
            catch (SocketException) { await Task.Delay(50); }
        }
        throw new InvalidOperationException("The relay did not start listening.");
    }

    private static MimeMessage NewMessage()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("scanner@corp.example"));
        message.To.Add(MailboxAddress.Parse("alice@example.org"));
        message.Subject = "Scan";
        message.Body = new TextPart("plain") { Text = "Scanned document.\r\n" };
        return message;
    }

    private async Task<SmtpClient> ConnectAsync()
    {
        var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", _relayPort, SecureSocketOptions.None);
        return client;
    }

    private static void ClearText(InboundAuthMode mode, RelayOptions o)
    {
        o.Security.Authentication.Mode = mode;
        o.Security.Authentication.AllowWithoutTls = true;
    }

    [Fact]
    public async Task WhenSignInIsRequiredAnAnonymousClientCannotSend()
    {
        await StartRelayAsync(o => ClearText(InboundAuthMode.Required, o));

        using var client = await ConnectAsync();
        Assert.True(client.Capabilities.HasFlag(SmtpCapabilities.Authentication));
        Assert.Contains("PLAIN", client.AuthenticationMechanisms);
        Assert.Contains("LOGIN", client.AuthenticationMechanisms);

        // 530: MailKit reports it as "not authenticated".
        var error = await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() => client.SendAsync(NewMessage()));
        Assert.Contains("Authentication required", error.Message);
        Assert.Empty(_smartHost.Messages);
    }

    [Fact]
    public async Task ASignedInUserSendsAndIsNamedInTheAuditTrail()
    {
        await StartRelayAsync(o => ClearText(InboundAuthMode.Required, o));

        using var client = await ConnectAsync();
        await client.AuthenticateAsync("SCANNER", Password);
        await client.SendAsync(NewMessage());
        await client.DisconnectAsync(true);

        Assert.Single(_smartHost.Messages);
        var audit = Assert.Single(_logs.Audit);
        Assert.StartsWith("DELIVERED client=127.0.0.1 user=scanner from=scanner@corp.example", audit);
    }

    [Theory]
    [InlineData("scanner", "not-the-right-one")]
    [InlineData("nobody", Password)]
    public async Task AWrongUserOrPasswordIsRefused(string user, string password)
    {
        await StartRelayAsync(o => ClearText(InboundAuthMode.Required, o));

        using var client = await ConnectAsync();
        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync(user, password));
        if (client.IsConnected)
            await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() => client.SendAsync(NewMessage()));
        Assert.Empty(_smartHost.Messages);
        Assert.Contains(_logs.Messages, m => m.Contains("Sign-in FAILED") && m.Contains(user));
        Assert.DoesNotContain(_logs.Messages, m => m.Contains(password)); // a password is never logged
    }

    [Fact]
    public async Task ADisabledUserIsRefusedWithoutRestartingTheRelay()
    {
        var users = await StartRelayAsync(o => ClearText(InboundAuthMode.Required, o));

        using (var client = await ConnectAsync())
        {
            await client.AuthenticateAsync("scanner", Password);
            await client.DisconnectAsync(true);
        }

        // What the console does while the service runs.
        new RelayUserStore(Path.Combine(_dataDir, "users.json")).SetEnabled("scanner", false);
        Assert.False(Assert.Single(users.Users).Enabled);

        using var second = await ConnectAsync();
        await Assert.ThrowsAsync<AuthenticationException>(() => second.AuthenticateAsync("scanner", Password));
    }

    [Fact]
    public async Task WhenSignInIsOptionalAnAllowedAddressStillSendsWithoutIt()
    {
        await StartRelayAsync(o => ClearText(InboundAuthMode.Optional, o));

        using (var anonymous = await ConnectAsync())
        {
            await anonymous.SendAsync(NewMessage());
            await anonymous.DisconnectAsync(true);
        }
        using (var signedIn = await ConnectAsync())
        {
            await signedIn.AuthenticateAsync("scanner", Password);
            await signedIn.SendAsync(NewMessage());
            await signedIn.DisconnectAsync(true);
        }

        Assert.Equal(2, _smartHost.Messages.Count);
        Assert.Contains(_logs.Audit, a => a.StartsWith("DELIVERED client=127.0.0.1 user=- "));
        Assert.Contains(_logs.Audit, a => a.StartsWith("DELIVERED client=127.0.0.1 user=scanner "));
    }

    [Fact]
    public async Task AnAddressOutsideTheAllowlistMustSignIn()
    {
        await StartRelayAsync(o =>
        {
            ClearText(InboundAuthMode.Optional, o);
            o.Security.AllowedIps = new() { "10.99.99.0/24" };
            o.Security.Authentication.AllowFromAnyAddress = true;
        });

        using (var anonymous = await ConnectAsync())
        {
            var error = await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() => anonymous.SendAsync(NewMessage()));
            Assert.Contains("Authentication required", error.Message);
        }
        Assert.Empty(_smartHost.Messages);

        using var signedIn = await ConnectAsync();
        await signedIn.AuthenticateAsync("scanner", Password);
        await signedIn.SendAsync(NewMessage());
        await signedIn.DisconnectAsync(true);
        Assert.Single(_smartHost.Messages);
    }

    [Fact]
    public async Task WithoutTheOptionAnAddressOutsideTheAllowlistIsStillDisconnected()
    {
        await StartRelayAsync(o =>
        {
            ClearText(InboundAuthMode.Required, o);
            o.Security.AllowedIps = new() { "10.99.99.0/24" };
        });

        using var client = new SmtpClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync("127.0.0.1", _relayPort, SecureSocketOptions.None));
    }

    [Fact]
    public void SignInWithoutInboundTlsMustBeAskedForExplicitly()
    {
        var options = new RelayOptions
        {
            Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "mail.example.org" },
            Security = { Authentication = { Mode = InboundAuthMode.Required } }
        };
        Assert.Contains(RelayOptionsValidator.Validate(options), e => e.Contains("Security.Authentication"));

        options.Security.Authentication.AllowWithoutTls = true;
        Assert.Empty(RelayOptionsValidator.Validate(options));
        Assert.Contains(RelayOptionsValidator.Warnings(options), w => w.Contains("clear text") && w.Contains("relay users"));

        options.Security.Authentication.AllowFromAnyAddress = true;
        Assert.Contains(RelayOptionsValidator.Warnings(options), w => w.Contains("from any address"));
    }

    [Fact]
    public void SignInIsOffUnlessConfigured()
    {
        var options = new RelayOptions();
        Assert.Equal(InboundAuthMode.Disabled, options.Security.Authentication.Mode);
        Assert.False(options.Security.Authentication.AllowFromAnyAddress);
        Assert.False(options.Security.Authentication.AllowWithoutTls);
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class AuditCapture : ILoggerFactory
    {
        private readonly ConcurrentQueue<(string Category, string Message)> _entries = new();

        public IEnumerable<string> Audit => _entries.Where(e => e.Category == LoggingSetup.AuditSourceContext).Select(e => e.Message);
        public IEnumerable<string> Messages => _entries.Select(e => e.Message);

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Capturing(string category, ConcurrentQueue<(string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((category, formatter(state, exception)));
        }
    }

    /// <summary>Minimal smart host: accepts anonymous mail and counts what it receives.</summary>
    private sealed class CountingSmartHost
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _run;

        public int Port { get; private set; }
        public ConcurrentQueue<long> Messages { get; } = new();

        public async Task StartAsync()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var options = new SmtpServerOptionsBuilder()
                .ServerName("fake-smarthost")
                .Endpoint(e => e.Endpoint(new IPEndPoint(IPAddress.Loopback, Port)))
                .Build();
            var services = new SmtpServer.ComponentModel.ServiceProvider();
            services.Add(new Store(this));
            _run = new SmtpServer.SmtpServer(options, services).StartAsync(_cts.Token);

            for (int i = 0; i < 100; i++)
            {
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, Port);
                    return;
                }
                catch (SocketException) { await Task.Delay(50); }
            }
            throw new InvalidOperationException("The fake smart host did not start listening.");
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            try { if (_run is not null) await _run; } catch (OperationCanceledException) { }
        }

        private sealed class Store(CountingSmartHost owner) : MessageStore
        {
            public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
            {
                owner.Messages.Enqueue(buffer.Length);
                return Task.FromResult(SmtpResponse.Ok);
            }
        }
    }
}
