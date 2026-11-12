using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Security;
using SmtpRelay.Snmp;

namespace SmtpRelay.Tests;

/// <summary>DNS names in the settings of the SNMP agent: the monitoring systems and the address to listen on.</summary>
public sealed class SnmpDnsNameTests : IAsyncLifetime
{
    private const string Community = "s3cret-community";

    private readonly string _dataDir = Directory.CreateTempSubdirectory("smtprelay-snmpdns-").FullName;
    private readonly DeliveryCounters _counters = new(null, delivered: 7, failed: 1);
    private SnmpAgentService? _agent;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_agent is not null)
            await _agent.StopAsync(CancellationToken.None);
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private async Task<IPEndPoint> StartAsync(string listenOn, string[] managers, Func<string, IPAddress[]>? resolve = null, TimeSpan? refresh = null)
    {
        var options = new RelayOptions
        {
            DataDirectory = _dataDir,
            Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "mail.example.org" },
            Snmp = { Enabled = true, IpAddress = listenOn, Port = 0, AllowedManagers = managers.ToList() }
        };
        Assert.DoesNotContain(RelayOptionsValidator.Validate(options), e => !e.StartsWith("Snmp.Port")); // port 0: any free port, for the test

        var secrets = SecretStore.Open(options);
        secrets.Set(options.Snmp.CommunitySecret, Community);

        _agent = new SnmpAgentService(options, secrets, _counters, NullLogger<SnmpAgentService>.Instance,
            resolve is null ? null : new SnmpManagerList(managers, resolve), refresh);
        await _agent.StartAsync(CancellationToken.None);
        for (int i = 0; i < 100 && _agent.BoundPort == 0; i++)
            await Task.Delay(20);
        Assert.NotEqual(0, _agent.BoundPort);
        return new IPEndPoint(IPAddress.Loopback, _agent.BoundPort);
    }

    private static bool Answers(IPEndPoint endpoint)
    {
        try
        {
            var result = Messenger.Get(VersionCode.V2, endpoint, new OctetString(Community),
                new List<Variable> { new(new ObjectIdentifier(RelayMib.DeliveredToday)) }, 700);
            return result.Count == 1 && result[0].Data.Equals(new Gauge32(7));
        }
        catch (Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            return false;
        }
    }

    [Fact]
    public async Task AMonitoringSystemGivenByNameIsAnswered()
    {
        var endpoint = await StartAsync("127.0.0.1", new[] { "nms01.contoso.local" },
            name => name == "nms01.contoso.local" ? new[] { IPAddress.Loopback } : throw new SocketException((int)SocketError.HostNotFound));

        Assert.True(Answers(endpoint));
    }

    [Fact]
    public async Task ANameThatStandsForAnotherAddressIsNotAnswered()
    {
        var endpoint = await StartAsync("127.0.0.1", new[] { "nms01.contoso.local" }, _ => new[] { IPAddress.Parse("192.0.2.10") });

        Assert.False(Answers(endpoint));
    }

    [Fact]
    public async Task TheRealResolverAndANameToListenOnWork()
    {
        // "localhost" always resolves, without a DNS server: it is both the interface and the monitoring system.
        var endpoint = await StartAsync("localhost", new[] { "localhost" });

        Assert.True(Answers(endpoint));
    }

    [Fact]
    public async Task AnAddressThatChangesInDnsIsFollowedWithoutRestarting()
    {
        var current = IPAddress.Parse("192.0.2.10");
        var endpoint = await StartAsync("127.0.0.1", new[] { "nms01.contoso.local" }, _ => new[] { current }, TimeSpan.FromMilliseconds(150));
        Assert.False(Answers(endpoint));

        current = IPAddress.Loopback; // the monitoring system moved
        bool answered = false;
        for (int i = 0; i < 20 && !answered; i++)
        {
            await Task.Delay(100);
            answered = Answers(endpoint);
        }
        Assert.True(answered);
    }

    [Fact]
    public void ANameThatStopsResolvingKeepsItsLastAddress()
    {
        bool dnsDown = false;
        var list = new SnmpManagerList(new[] { "nms01.contoso.local", "10.20.9.0/24" },
            _ => dnsDown ? throw new SocketException((int)SocketError.HostNotFound) : new[] { IPAddress.Parse("192.168.20.15") });

        Assert.False(list.IsAllowed(IPAddress.Parse("192.168.20.15"))); // not resolved yet
        Assert.Empty(list.Refresh());
        Assert.True(list.IsAllowed(IPAddress.Parse("192.168.20.15")));
        Assert.True(list.IsAllowed(IPAddress.Parse("10.20.9.4")));       // networks keep working next to names
        Assert.False(list.IsAllowed(IPAddress.Parse("192.168.20.16")));

        dnsDown = true;
        var failure = Assert.Single(list.Refresh());
        Assert.Equal("nms01.contoso.local", failure.Name);
        Assert.True(list.IsAllowed(IPAddress.Parse("192.168.20.15")));
    }

    [Fact]
    public void ANameThatNeverResolvedAllowsNobody()
    {
        var list = new SnmpManagerList(new[] { "ghost.contoso.local" }, _ => throw new SocketException((int)SocketError.HostNotFound));

        Assert.Single(list.Refresh());
        Assert.Equal(1, list.Count);
        Assert.False(list.IsAllowed(IPAddress.Loopback));
        Assert.False(list.IsAllowed(null));
    }

    [Fact]
    public void AnIPv4AddressSeenThroughIPv6MatchesItsName()
    {
        var list = new SnmpManagerList(new[] { "nms01" }, _ => new[] { IPAddress.Parse("192.168.20.15") });
        list.Refresh();

        Assert.True(list.IsAllowed(IPAddress.Parse("::ffff:192.168.20.15")));
    }

    [Theory]
    [InlineData("nms01", true)]
    [InlineData("zabbix.contoso.local", true)]
    [InlineData("192.168.20.15", true)]
    [InlineData("10.20.9.0/24", true)]
    [InlineData("2001:db8::15", true)]
    [InlineData("192.168.1", false)]       // a mistyped address is not looked up as a name
    [InlineData("10.20.9.0/33", false)]
    [InlineData("nms 01", false)]
    [InlineData("nms01/24", false)]
    [InlineData("", false)]
    public void EntriesAreAddressesNetworksOrNames(string entry, bool valid)
    {
        Assert.Equal(valid, SnmpManagerList.IsValidEntry(entry));

        var options = new RelayOptions
        {
            Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "mail.example.org" },
            Snmp = { AllowedManagers = { entry } }
        };
        Assert.Equal(valid, !RelayOptionsValidator.Validate(options).Any(e => e.StartsWith("Snmp.AllowedManagers")));
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("relay01.contoso.local", true)]
    [InlineData("relay 01", false)]
    [InlineData("10.0.0.300", false)]
    public void TheAddressToListenOnIsAnAddressOrAName(string value, bool valid)
    {
        var options = new RelayOptions
        {
            Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "mail.example.org" },
            Snmp = { Enabled = true, IpAddress = value }
        };
        Assert.Equal(valid, !RelayOptionsValidator.Validate(options).Any(e => e.StartsWith("Snmp.IpAddress")));
    }
}
