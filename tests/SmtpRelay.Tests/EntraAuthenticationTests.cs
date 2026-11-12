using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

public sealed class EntraAuthenticationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smtprelay-entra-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RelayOptions ValidAcs() => new()
    {
        Security = { AllowedIps = { "10.0.0.0/8" } },
        Upstream =
        {
            Mode = UpstreamAuthMode.AzureCommunicationServices,
            Host = AzureCommunicationOptions.DefaultHost,
            AzureCommunication =
            {
                TenantId = "11111111-aaaa-bbbb-cccc-222222222222",
                ClientId = "11111111-2222-3333-4444-555555555555",
                SmtpUsername = "relay-smtp-user"
            }
        }
    };

    [Fact]
    public void AzureCommunicationServicesConfigurationPasses()
    {
        var o = ValidAcs();
        Assert.Empty(RelayOptionsValidator.Validate(o));
        Assert.Equal("https://communication.azure.com/.default", o.Upstream.AzureCommunication.Scope);
        Assert.Equal("AcsClientSecret", o.Upstream.AzureCommunication.ClientSecretName);
        Assert.Same(o.Upstream.AzureCommunication, o.Upstream.EntraApplication);
        Assert.Equal("relay-smtp-user", o.Upstream.TokenUserName);
    }

    [Fact]
    public void AzureCommunicationServicesNeedsItsFields()
    {
        var o = ValidAcs();
        o.Upstream.AzureCommunication.SmtpUsername = " ";
        o.Upstream.AzureCommunication.ClientId = "not-a-guid";
        var errors = RelayOptionsValidator.Validate(o);
        Assert.Contains(errors, e => e.Contains("AzureCommunication.SmtpUsername"));
        Assert.Contains(errors, e => e.Contains("AzureCommunication.ClientId"));
        // The Microsoft 365 section is not in use and must not be asked for.
        Assert.DoesNotContain(errors, e => e.Contains("Microsoft365"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("1234", false)]
    [InlineData("989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8", true)]
    [InlineData("98 9a 5c 82 20 3a b1 9b 6d b2 9f c4 47 e8 cb 5e be eb 60 d8", true)]
    public void ACertificateCredentialNeedsAThumbprintAndNoSecret(string thumbprint, bool valid)
    {
        var o = ValidAcs();
        o.Upstream.AzureCommunication.Credential = EntraCredentialType.Certificate;
        o.Upstream.AzureCommunication.CertificateThumbprint = thumbprint;
        o.Upstream.AzureCommunication.ClientSecretName = ""; // not used with a certificate

        var errors = RelayOptionsValidator.Validate(o);
        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
            Assert.Contains(errors, e => e.Contains("CertificateThumbprint"));
    }

    [Fact]
    public void ThumbprintsCopiedFromTheCertificateDialogAreAccepted()
    {
        // The dialog puts an invisible left-to-right mark in front of the value.
        Assert.Equal("989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8",
            CertificateFinder.Normalize((char)0x200E + "98 9a 5c 82 20 3a b1 9b 6d b2 9f c4 47 e8 cb 5e be eb 60 d8"));
        Assert.Equal("", CertificateFinder.Normalize(null));
    }

    [Fact]
    public void TheNewSettingsSurviveSaveAndLoadAndOldFilesKeepWorking()
    {
        var o = ValidAcs();
        o.DataDirectory = _dir;
        o.Upstream.AzureCommunication.Credential = EntraCredentialType.Certificate;
        o.Upstream.AzureCommunication.CertificateThumbprint = "989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8";
        o.Snmp.Enabled = true;
        o.Snmp.AllowedManagers.Add("192.168.20.15");
        ConfigLoader.Save(_dir, o);

        var back = ConfigLoader.Load(_dir);
        Assert.Equal(UpstreamAuthMode.AzureCommunicationServices, back.Upstream.Mode);
        Assert.Equal(EntraCredentialType.Certificate, back.Upstream.AzureCommunication.Credential);
        Assert.Equal("989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8", back.Upstream.AzureCommunication.CertificateThumbprint);
        Assert.Equal("relay-smtp-user", back.Upstream.AzureCommunication.SmtpUsername);
        Assert.True(back.Snmp.Enabled);
        Assert.Equal(new[] { "192.168.20.15" }, back.Snmp.AllowedManagers);
        // Computed properties are not written to the file.
        Assert.DoesNotContain("EntraApplication", File.ReadAllText(Path.Combine(_dir, ConfigLoader.FileName)));
        Assert.DoesNotContain("TokenUserName", File.ReadAllText(Path.Combine(_dir, ConfigLoader.FileName)));

        // A settings file written by version 2.0 knows nothing about credentials, ACS or SNMP.
        File.WriteAllText(Path.Combine(_dir, ConfigLoader.FileName), """
            { "SmtpRelay": { "Upstream": { "Mode": "Microsoft365", "Microsoft365": {
                "TenantId": "contoso.onmicrosoft.com", "ClientId": "11111111-2222-3333-4444-555555555555",
                "ClientSecretName": "M365ClientSecret", "SenderUsername": "relay@contoso.com",
                "Scope": "https://outlook.office365.com/.default" } } } }
            """);
        var old = ConfigLoader.Load(_dir);
        Assert.Empty(RelayOptionsValidator.Validate(old));
        Assert.Equal(EntraCredentialType.ClientSecret, old.Upstream.Microsoft365.Credential);
        Assert.False(old.Snmp.Enabled);
        Assert.Equal(1161, old.Snmp.Port);
    }

    [Fact]
    public async Task AMissingCertificateIsAPermanentErrorThatNamesIt()
    {
        var o = ValidAcs();
        o.DataDirectory = _dir;
        o.Upstream.AzureCommunication.Credential = EntraCredentialType.Certificate;
        o.Upstream.AzureCommunication.CertificateThumbprint = "0000000000000000000000000000000000000001";

        using var provider = new EntraTokenProvider(o.Upstream, SecretStore.Open(o), NullLogger<EntraTokenProvider>.Instance);
        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.True(ex.IsPermanent);
        Assert.Contains("0000000000000000000000000000000000000001", ex.Message);
    }

    [Fact]
    public async Task AMissingClientSecretIsAPermanentErrorThatNamesIt()
    {
        var o = ValidAcs();
        o.DataDirectory = _dir;
        using var provider = new EntraTokenProvider(o.Upstream, SecretStore.Open(o), NullLogger<EntraTokenProvider>.Instance);
        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.True(ex.IsPermanent);
        Assert.Contains("AcsClientSecret", ex.Message);
    }

    [Theory]
    [InlineData(UpstreamAuthMode.AzureCommunicationServices, "relay-smtp-user")]
    [InlineData(UpstreamAuthMode.Microsoft365, "relay@contoso.com")]
    public async Task TheTokenIsPresentedWithTheUserNameOfTheMode(UpstreamAuthMode mode, string expectedUser)
    {
        using var server = new XOAuth2Server();
        var o = ValidAcs();
        o.DataDirectory = _dir;
        o.Upstream.Mode = mode;
        o.Upstream.Microsoft365.SenderUsername = "relay@contoso.com";
        o.Upstream.Host = "127.0.0.1";
        o.Upstream.Port = server.Port;
        // The fake server has no TLS: the opt-in a real installation should never need.
        o.Upstream.Security = UpstreamSecureMode.None;
        o.Upstream.AllowInsecureAuthentication = true;
        o.Upstream.TimeoutSeconds = 10;

        var sender = new UpstreamSender(o.Upstream, SecretStore.Open(o), new FixedToken("token-123"), NullLogger<UpstreamSender>.Instance);
        await sender.VerifyConnectionAsync(CancellationToken.None);

        Assert.Equal($"user={expectedUser}\u0001auth=Bearer token-123\u0001\u0001", await server.Credentials);
    }

    private sealed class FixedToken(string token) : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    /// <summary>Just enough of an SMTP server to accept AUTH XOAUTH2 and keep what the client presented.</summary>
    private sealed class XOAuth2Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _credentials = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public XOAuth2Server()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }
        public Task<string> Credentials => _credentials.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

                await writer.WriteLineAsync("220 fake ESMTP");
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 AUTH XOAUTH2");
                    }
                    else if (line.StartsWith("AUTH XOAUTH2 ", StringComparison.OrdinalIgnoreCase))
                    {
                        _credentials.TrySetResult(Encoding.UTF8.GetString(Convert.FromBase64String(line["AUTH XOAUTH2 ".Length..])));
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("502 not implemented");
                    }
                }
            }
            catch (Exception ex)
            {
                _credentials.TrySetException(ex);
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
