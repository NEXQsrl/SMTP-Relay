using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

public class ConfigurationTests
{
    private static RelayOptions ValidM365() => new()
    {
        Security = { AllowedIps = { "10.0.0.0/8" } },
        Upstream =
        {
            Mode = UpstreamAuthMode.Microsoft365,
            Microsoft365 =
            {
                TenantId = "contoso.onmicrosoft.com",
                ClientId = "11111111-2222-3333-4444-555555555555",
                SenderUsername = "relay@contoso.com"
            }
        }
    };

    [Fact]
    public void ValidMicrosoft365ConfigurationPasses()
        => Assert.Empty(RelayOptionsValidator.Validate(ValidM365()));

    [Fact]
    public void DefaultsNeedTheMicrosoft365Fields()
    {
        var errors = RelayOptionsValidator.Validate(new RelayOptions());
        Assert.Contains(errors, e => e.Contains("TenantId"));
        Assert.Contains(errors, e => e.Contains("ClientId"));
        Assert.Contains(errors, e => e.Contains("SenderUsername"));
    }

    [Theory]
    [InlineData("contoso.com/evil")]
    [InlineData("../common")]
    [InlineData("a b")]
    [InlineData("")]
    public void TenantIdCannotInjectIntoTheAuthorityUrl(string tenant)
    {
        var o = ValidM365();
        o.Upstream.Microsoft365.TenantId = tenant;
        Assert.Contains(RelayOptionsValidator.Validate(o), e => e.Contains("TenantId"));
    }

    [Theory]
    [InlineData(UpstreamSecureMode.None)]
    [InlineData(UpstreamSecureMode.StartTlsWhenAvailable)]
    public void CredentialsOverClearTextNeedAnExplicitOptIn(UpstreamSecureMode mode)
    {
        var o = ValidM365();
        o.Upstream.Security = mode;
        Assert.Contains(RelayOptionsValidator.Validate(o), e => e.Contains("clear text"));

        o.Upstream.AllowInsecureAuthentication = true;
        Assert.Empty(RelayOptionsValidator.Validate(o));
        Assert.Contains(RelayOptionsValidator.Warnings(o), w => w.Contains("clear text"));
    }

    [Fact]
    public void AnonymousUpstreamMayUseClearText()
    {
        var o = new RelayOptions
        {
            Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "mail.internal", Port = 25, Security = UpstreamSecureMode.None }
        };
        Assert.Empty(RelayOptionsValidator.Validate(o));
    }

    [Fact]
    public void BasicModeNeedsAUserName()
    {
        var o = new RelayOptions { Upstream = { Mode = UpstreamAuthMode.Basic } };
        Assert.Contains(RelayOptionsValidator.Validate(o), e => e.Contains("Username"));
    }

    [Fact]
    public void InboundTlsNeedsACertificate()
    {
        var o = ValidM365();
        o.Tls.Mode = InboundTlsMode.StartTlsRequired;
        Assert.Contains(RelayOptionsValidator.Validate(o), e => e.Contains("certificate"));
        o.Tls.CertificateThumbprint = "AA BB";
        Assert.Empty(RelayOptionsValidator.Validate(o));
    }

    [Fact]
    public void InvalidValuesAreReported()
    {
        var o = ValidM365();
        o.Listener.Port = 70000;
        o.Listener.IpAddress = "nope";
        o.Security.AllowedIps.Add("300.1.1.1");
        o.Upstream.Host = "bad host";
        o.Upstream.OverrideFromAddress = "not-an-address";
        o.Logging.MinimumLevel = "Chatty";
        o.ServiceName = "bad\"name";
        o.DataDirectory = "relative";
        var errors = RelayOptionsValidator.Validate(o);
        Assert.Equal(8, errors.Count);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    [InlineData(@"\\server\share")]
    public void DataDirectoryCannotBeADriveRoot(string path)
    {
        var o = ValidM365();
        o.DataDirectory = path;
        Assert.Contains(RelayOptionsValidator.Validate(o), e => e.Contains("DataDirectory"));
    }

    [Fact]
    public void OpenRelayIsFlaggedAsAWarning()
    {
        var o = ValidM365();
        o.Security.AllowedIps = new() { "0.0.0.0/0" };
        Assert.Contains(RelayOptionsValidator.Warnings(o), w => w.Contains("open relay"));

        o.Security.AllowedIps.Clear();
        o.Security.RejectWhenAllowListEmpty = false;
        Assert.Contains(RelayOptionsValidator.Warnings(o), w => w.Contains("open relay"));
    }

    [Fact]
    public void SaveAndLoadRoundTripKeepsOtherSections()
    {
        var dir = Directory.CreateTempSubdirectory("smtprelay-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ConfigLoader.FileName),
                """
                {
                  // comments and trailing commas are accepted
                  "Other": { "Keep": 1 },
                  "SmtpRelay": { "Listener": { "Port": 2525 }, },
                }
                """);

            var o = ConfigLoader.Load(dir);
            Assert.Equal(2525, o.Listener.Port);

            o.Listener.Port = 587;
            o.Security.AllowedIps = new() { "10.1.0.0/16", "::1" };
            o.Upstream.Mode = UpstreamAuthMode.Basic;
            o.Upstream.Security = UpstreamSecureMode.SslOnConnect;
            o.Tls.Mode = InboundTlsMode.StartTlsOptional;
            o.Logging.RetainedFileTimeLimitDays = null;
            ConfigLoader.Save(dir, o);

            var text = File.ReadAllText(Path.Combine(dir, ConfigLoader.FileName));
            Assert.Contains("\"Keep\": 1", text);
            Assert.Contains("\"SslOnConnect\"", text);           // enums are written by name
            Assert.DoesNotContain("SecretsFilePath", text);      // computed paths are not written
            Assert.False(File.Exists(Path.Combine(dir, ConfigLoader.FileName + ".tmp")));

            var back = ConfigLoader.Load(dir);
            Assert.Equal(587, back.Listener.Port);
            Assert.Equal(new[] { "10.1.0.0/16", "::1" }, back.Security.AllowedIps);
            Assert.Equal(UpstreamAuthMode.Basic, back.Upstream.Mode);
            Assert.Equal(UpstreamSecureMode.SslOnConnect, back.Upstream.Security);
            Assert.Equal(InboundTlsMode.StartTlsOptional, back.Tls.Mode);
            Assert.Null(back.Logging.RetainedFileTimeLimitDays);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("line1\r\nFAILED forged=entry", "line1  FAILED forged=entry")]
    [InlineData("tab\there", "tab here")]
    [InlineData("zero\0byte", "zero byte")]
    [InlineData("plain", "plain")]
    [InlineData(null, "")]
    public void LogSanitizerRemovesControlCharacters(string? input, string expected)
        => Assert.Equal(expected, LogSanitizer.Clean(input));

    [Fact]
    public void LogSanitizerRemovesUnicodeLineSeparatorsAndTruncates()
    {
        Assert.Equal("a b c", LogSanitizer.Clean("a" + (char)0x2028 + "b" + (char)0x2029 + "c"));
        Assert.Equal(11, LogSanitizer.Clean(new string('x', 500), 10).Length);
    }
}
