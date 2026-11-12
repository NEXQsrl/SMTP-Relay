using SmtpRelay.Migration;

namespace SmtpRelay.Tests;

/// <summary>Import of the "Relay restrictions" of the IIS 6 SMTP service into the allowlist.</summary>
public sealed class Iis6RelayListTests
{
    private const uint Ref = 0x80000000;

    // The list as IIS stores it when "Only the list below" contains the single computer 127.0.0.1
    // (the block that the well-known administration scripts for RelayIpList start from).
    private static readonly byte[] OnlyLoopback =
    {
        24, 0, 0, 128,   32, 0, 0, 128,   60, 0, 0, 128,   68, 0, 0, 128,
        1, 0, 0, 0,      76, 0, 0, 0,
        0, 0, 0, 0,      0, 0, 0, 0,
        1, 0, 0, 0,      0, 0, 0, 0,
        2, 0, 0, 0,      1, 0, 0, 0,      4, 0, 0, 0,      0, 0, 0, 0,      76, 0, 0, 128,
        0, 0, 0, 0,      0, 0, 0, 0,
        0, 0, 0, 0,      0, 0, 0, 0,
        255, 255, 255, 255,
        127, 0, 0, 1
    };

    [Fact]
    public void TheSingleComputerOfTheKnownBlockIsRead()
    {
        var import = Iis6RelayList.ParseBlob(OnlyLoopback);

        Assert.Equal(new[] { "127.0.0.1" }, import.Entries);
        Assert.False(import.AllExceptList);
        Assert.Equal(0, import.SkippedNames);
    }

    [Fact]
    public void SingleComputersAndNetworksAreReadAsAddressesAndCidr()
    {
        var blob = Build(
            granted: new[]
            {
                (Mask: new byte[] { 255, 255, 255, 255 }, Addresses: new[] { new byte[] { 127, 0, 0, 1 }, new byte[] { 192, 168, 10, 25 } }),
                (Mask: new byte[] { 255, 255, 255, 0 }, Addresses: new[] { new byte[] { 10, 1, 2, 0 } }),
                (Mask: new byte[] { 255, 255, 240, 0 }, Addresses: new[] { new byte[] { 172, 16, 37, 9 } })
            },
            denied: Array.Empty<(byte[], byte[][])>());

        var import = Iis6RelayList.ParseBlob(blob);

        // A host inside a network entry is normalized to the network address.
        Assert.Equal(new[] { "127.0.0.1", "192.168.10.25", "10.1.2.0/24", "172.16.32.0/20" }, import.Entries);
        Assert.False(import.AllExceptList);
    }

    [Fact]
    public void AllExceptTheListHasNothingToImport()
    {
        var blob = Build(
            granted: Array.Empty<(byte[], byte[][])>(),
            denied: new[] { (new byte[] { 255, 255, 255, 255 }, new[] { new byte[] { 10, 9, 9, 9 } }) });

        var import = Iis6RelayList.ParseBlob(blob);

        Assert.Empty(import.Entries);
        Assert.True(import.AllExceptList);
    }

    [Fact]
    public void AnEmptyListGivesNothing()
    {
        Assert.Empty(Iis6RelayList.ParseBlob(Array.Empty<byte>()).Entries);
        Assert.Empty(Iis6RelayList.ParseBlob(Build(Array.Empty<(byte[], byte[][])>(), Array.Empty<(byte[], byte[][])>())).Entries);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void DataThatIsNotARelayListIsRefusedInsteadOfGuessed(byte[] blob)
        => Assert.Throws<InvalidDataException>(() => Iis6RelayList.ParseBlob(blob));

    [Fact]
    public void ATruncatedListIsRefused()
        => Assert.Throws<InvalidDataException>(() => Iis6RelayList.ParseBlob(OnlyLoopback[..82]));

    [Fact]
    public void TheListIsFoundInTheMetabaseFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "smtprelay-metabase-" + Guid.NewGuid().ToString("N") + ".xml");
        var second = Build(
            granted: new[] { (new byte[] { 255, 255, 255, 0 }, new[] { new byte[] { 192, 168, 20, 0 } }), (new byte[] { 255, 255, 255, 255 }, new[] { new byte[] { 127, 0, 0, 1 } }) },
            denied: Array.Empty<(byte[], byte[][])>());
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <configuration xmlns="urn:microsoft-catalog:XML_Metabase_V64_0">
              <MBProperty>
                <IIsSmtpService Location="/LM/SmtpSvc" MaxConnections="2000000000" />
                <IIsSmtpServer Location="/LM/SmtpSvc/1" ServerComment="Default SMTP Virtual Server"
                               RelayIpList="{Convert.ToHexString(OnlyLoopback).ToLowerInvariant()}" />
                <IIsSmtpServer Location="/LM/SmtpSvc/2" ServerComment="Second" RelayIpList="{Convert.ToHexString(second)}" />
                <IIsSmtpDomain Location="/LM/SmtpSvc/1/Domain/contoso.local" RouteAction="16" />
              </MBProperty>
            </configuration>
            """);
        try
        {
            var import = Iis6RelayList.ReadMetabase(path);

            Assert.NotNull(import);
            Assert.Equal(new[] { "127.0.0.1", "192.168.20.0/24" }, import.Entries); // both virtual servers, no duplicates
            Assert.Equal(path, import.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMetabaseWithoutSmtpServersMeansThatIisSmtpIsNotInstalled()
    {
        var path = Path.Combine(Path.GetTempPath(), "smtprelay-metabase-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, """<configuration><MBProperty><IIsWebService Location="/LM/W3SVC" /></MBProperty></configuration>""");
        try
        {
            Assert.Null(Iis6RelayList.ReadMetabase(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("10.0.0.5, 255.255.255.255", "10.0.0.5")]
    [InlineData("192.168.10.0, 255.255.255.0", "192.168.10.0/24")]
    [InlineData(" 10.20.33.7 ,255.255.0.0 ", "10.20.0.0/16")]
    public void EntriesReturnedByIisAreConverted(string value, string expected)
    {
        Assert.True(Iis6RelayList.TryParseAdsiEntry(value, out var entry));
        Assert.Equal(expected, entry);
        Assert.True(SmtpRelay.Security.IpAllowList.TryParse(entry, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contoso.local")]
    [InlineData("10.0.0.1, 255.0.255.0")]
    [InlineData("10.0.0.1, 255.255.255.0, extra")]
    public void EntriesThatAreNotAddressesAreSkipped(string value)
        => Assert.False(Iis6RelayList.TryParseAdsiEntry(value, out _));

    /// <summary>Builds the binary list with the layout IIS uses: four offsets, flags, then the lists.</summary>
    private static byte[] Build((byte[] Mask, byte[][] Addresses)[] granted, (byte[] Mask, byte[][] Addresses)[] denied)
    {
        const int header = 24;
        int deniedSize = 8 + denied.Length * 20;
        int grantedSize = 8 + granted.Length * 20;
        int deniedAt = header;
        int grantedAt = deniedAt + deniedSize;
        int deniedNamesAt = grantedAt + grantedSize;
        int grantedNamesAt = deniedNamesAt + 8;
        int data = grantedNamesAt + 8;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)deniedAt | Ref);
        writer.Write((uint)grantedAt | Ref);
        writer.Write((uint)deniedNamesAt | Ref);
        writer.Write((uint)grantedNamesAt | Ref);
        writer.Write(1u);
        writer.Write((uint)data);

        var payload = new MemoryStream();
        foreach (var list in new[] { denied, granted })
        {
            writer.Write((uint)list.Length);
            writer.Write((uint)list.Sum(group => group.Addresses.Length));
            foreach (var (mask, addresses) in list)
            {
                int ones = mask.Sum(b => System.Numerics.BitOperations.PopCount(b));
                writer.Write(2u);                       // AF_INET
                writer.Write((uint)addresses.Length);
                writer.Write((uint)(ones / 8));         // full bytes of the mask
                writer.Write((uint)(ones % 8 == 0 ? 0 : mask[ones / 8]));
                writer.Write((uint)(data + payload.Length) | Ref);
                payload.Write(mask);
                foreach (var address in addresses)
                    payload.Write(address);
            }
        }
        writer.Write(0u); writer.Write(0u);             // denied names
        writer.Write(0u); writer.Write(0u);             // granted names
        writer.Write(payload.ToArray());
        writer.Flush();
        return stream.ToArray();
    }
}
