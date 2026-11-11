using SmtpRelay.Manager.Services;

namespace SmtpRelay.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v2.0.0", "2.0.0.0")]
    [InlineData("2.1", "2.1.0.0")]
    [InlineData("V3.4.5.6", "3.4.5.6")]
    [InlineData("v2.1.0-rc1", "2.1.0.0")]
    [InlineData("v2.1.0+build7", "2.1.0.0")]
    [InlineData("7", "7.0.0.0")]
    public void ParsesVersions(string text, string expected)
        => Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("1.2.3.4.5")]
    public void RejectsWhatIsNotAVersion(string? text)
        => Assert.Null(UpdateService.ParseVersion(text));

    [Fact]
    public void ThreeAndFourPartVersionsCompareAsEqual()
    {
        Assert.Equal(UpdateService.ParseVersion("v2.0.0"), UpdateService.ParseVersion("2.0.0.0"));
        Assert.True(UpdateService.ParseVersion("v2.0.1") > UpdateService.ParseVersion("2.0.0.9"));
        Assert.True(UpdateService.ParseVersion("v2.10.0") > UpdateService.ParseVersion("v2.9.0"));
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v9.8.7",
          "html_url": "https://github.com/NEXQsrl/SMTP-Relay/releases/tag/v9.8.7",
          "body": "Notes",
          "assets": [
            { "name": "SHA256SUMS.txt", "size": 10, "browser_download_url": "https://github.com/NEXQsrl/SMTP-Relay/releases/download/v9.8.7/SHA256SUMS.txt" },
            { "name": "SmtpRelay-Setup-9.8.7.exe", "size": 1234, "digest": "sha256:abcdef0123",
              "browser_download_url": "https://github.com/NEXQsrl/SMTP-Relay/releases/download/v9.8.7/SmtpRelay-Setup-9.8.7.exe" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheSetupOfARelease()
    {
        var release = UpdateService.ParseRelease(ReleaseJson)!;
        Assert.Equal(new Version(9, 8, 7, 0), release.Version);
        Assert.Equal("SmtpRelay-Setup-9.8.7.exe", release.InstallerName);
        Assert.Equal(1234, release.InstallerSize);
        Assert.Equal("ABCDEF0123", release.InstallerSha256);
        Assert.True(UpdateService.IsNewer(release));
    }

    [Theory]
    [InlineData("https://evil.example/SmtpRelay-Setup-9.8.7.exe")]
    [InlineData("https://github.com/someone-else/SMTP-Relay/releases/download/v9.8.7/SmtpRelay-Setup-9.8.7.exe")]
    [InlineData("http://github.com/NEXQsrl/SMTP-Relay/releases/download/v9.8.7/SmtpRelay-Setup-9.8.7.exe")]
    [InlineData("https://github.com.evil.example/NEXQsrl/SMTP-Relay/releases/download/v9.8.7/SmtpRelay-Setup-9.8.7.exe")]
    public void ASetupHostedElsewhereIsIgnored(string url)
    {
        var json = ReleaseJson.Replace(
            "https://github.com/NEXQsrl/SMTP-Relay/releases/download/v9.8.7/SmtpRelay-Setup-9.8.7.exe", url);
        var release = UpdateService.ParseRelease(json)!;
        Assert.Null(release.InstallerUrl);
    }

    [Fact]
    public void AnAssetNameCannotEscapeTheDownloadFolder()
    {
        var json = ReleaseJson.Replace("\"name\": \"SmtpRelay-Setup-9.8.7.exe\"", "\"name\": \"SmtpRelay-Setup-..\\\\..\\\\evil.exe\"");
        var release = UpdateService.ParseRelease(json)!;
        Assert.Equal("evil.exe", release.InstallerName);
    }

    [Fact]
    public void AReleaseWithoutAVersionTagIsIgnored()
        => Assert.Null(UpdateService.ParseRelease("""{ "tag_name": "nightly", "assets": [] }"""));

    [Fact]
    public void SignatureOfAMicrosoftSignedFileIsVerified()
    {
        // A library signed by Microsoft that ships with the tests.
        var signed = Path.Combine(AppContext.BaseDirectory, "Microsoft.Identity.Client.dll");
        var info = Authenticode.Verify(signed);
        Assert.True(info.IsIntact);
        Assert.True(info.IsChainTrusted);
        Assert.Contains("Microsoft", info.SignerSubject);
        Assert.NotEqual(UpdateService.PublisherThumbprint, info.SignerThumbprint);
    }

    [Fact]
    public void ATamperedFileIsRejected()
    {
        var signed = Path.Combine(AppContext.BaseDirectory, "Microsoft.Identity.Client.dll");
        var copy = Path.Combine(Path.GetTempPath(), "smtprelay-tamper-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            var bytes = File.ReadAllBytes(signed);
            bytes[bytes.Length / 2] ^= 0xFF; // one byte of code changed, signature left in place
            File.WriteAllBytes(copy, bytes);

            var info = Authenticode.Verify(copy);
            Assert.False(info.IsIntact);
            Assert.Null(info.SignerThumbprint);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void AnUnsignedFileIsRejected()
    {
        var unsigned = Path.Combine(Path.GetTempPath(), "smtprelay-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(unsigned, new byte[2048]);
            Assert.False(Authenticode.Verify(unsigned).IsIntact);
        }
        finally
        {
            File.Delete(unsigned);
        }
    }

    /// <summary>
    /// Release check: set SMTPRELAY_SIGNED_SETUP to the setup built by build\Release.proj. The setup must pass
    /// exactly the verification the console applies before installing an update. Without the variable it does nothing.
    /// </summary>
    [Fact]
    public void TheReleaseSetupPassesTheUpdateVerification()
    {
        var setup = Environment.GetEnvironmentVariable("SMTPRELAY_SIGNED_SETUP");
        if (string.IsNullOrEmpty(setup))
            return;

        var info = Authenticode.Verify(setup);
        Assert.True(info.IsIntact);
        Assert.Equal(UpdateService.PublisherThumbprint, info.SignerThumbprint);

        var copy = Path.Combine(Path.GetTempPath(), "smtprelay-tamper-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var bytes = File.ReadAllBytes(setup);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);
            Assert.False(Authenticode.Verify(copy).IsIntact);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void TheNexqCertificateIsAlwaysATrustedPublisher()
        => Assert.Contains(UpdateService.PublisherThumbprint, UpdateService.TrustedPublishers());
}
