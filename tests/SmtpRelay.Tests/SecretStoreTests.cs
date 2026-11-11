using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smtprelay-test-").FullName;
    private RelayOptions Options => new() { DataDirectory = _dir };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripAndPersistence()
    {
        var store = SecretStore.Open(Options);
        store.Set("UpstreamPassword", "p@ss wörd \"quoted\"");
        Assert.True(store.Contains("upstreampassword")); // names are case-insensitive
        Assert.Equal("p@ss wörd \"quoted\"", store.Get("UpstreamPassword"));

        var reopened = SecretStore.Open(Options);
        Assert.Equal("p@ss wörd \"quoted\"", reopened.Get("UpstreamPassword"));
        Assert.Equal(new[] { "UpstreamPassword" }, reopened.Names);

        Assert.True(reopened.Remove("UpstreamPassword"));
        Assert.False(reopened.Remove("UpstreamPassword"));
        Assert.Null(reopened.Get("UpstreamPassword"));
    }

    [Fact]
    public void ValuesAreNotStoredInClearText()
    {
        SecretStore.Open(Options).Set("M365ClientSecret", "super-secret-value");
        Assert.DoesNotContain("super-secret-value", File.ReadAllText(Options.SecretsFilePath));
    }

    [Fact]
    public void ARunningInstanceSeesSecretsWrittenByAnotherProcess()
    {
        var service = SecretStore.Open(Options);
        Assert.Null(service.Get("M365ClientSecret"));

        var console = SecretStore.Open(Options);
        console.Set("M365ClientSecret", "first");
        Assert.Equal("first", service.Get("M365ClientSecret"));

        // Make sure the timestamp of the file moves on even on a coarse file-system clock.
        File.SetLastWriteTimeUtc(Options.SecretsFilePath, DateTime.UtcNow.AddSeconds(-5));
        Assert.Equal("first", service.Get("M365ClientSecret"));
        console.Set("M365ClientSecret", "rotated");
        Assert.Equal("rotated", service.Get("M365ClientSecret"));
    }

    [Fact]
    public void ADamagedFileIsNeverTreatedAsEmpty()
    {
        SecretStore.Open(Options).Set("A", "1");
        File.WriteAllText(Options.SecretsFilePath, "{ this is not json");
        Assert.Throws<InvalidDataException>(() => SecretStore.Open(Options));
    }

    [Fact]
    public void ADifferentEntropyCannotDecrypt()
    {
        SecretStore.Open(Options).Set("A", "1");
        var other = new SecretStore(Options.SecretsFilePath, new SecretProtector(new byte[32]));
        Assert.Null(other.Get("A"));
    }

    [Fact]
    public void DamagedEntropyIsReported()
    {
        File.WriteAllBytes(Options.EntropyFilePath, new byte[5]);
        Assert.Throws<InvalidDataException>(() => MachineKey.LoadOrCreate(Options.EntropyFilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/name")]
    [InlineData("bad\"name")]
    public void InvalidNamesAreRefused(string name)
        => Assert.Throws<ArgumentException>(() => SecretStore.Open(Options).Set(name, "x"));
}
