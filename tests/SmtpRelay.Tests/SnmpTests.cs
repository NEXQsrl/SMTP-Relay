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

/// <summary>
/// Runs the real SNMP agent on a loopback UDP port and queries it with an independent SNMP client library,
/// so that the encoding is checked against someone else's implementation.
/// </summary>
public sealed class SnmpTests : IAsyncLifetime
{
    private const string Community = "s3cret-community";
    private const int Timeout = 3000;

    private readonly string _dataDir = Directory.CreateTempSubdirectory("smtprelay-snmp-").FullName;
    private readonly DeliveryCounters _counters = new(null, delivered: 1284, failed: 3);
    private SnmpAgentService? _agent;
    private IPEndPoint _endpoint = new(IPAddress.Loopback, 0);

    private static readonly ObjectIdentifier Delivered = new(RelayMib.DeliveredToday);
    private static readonly ObjectIdentifier Failed = new(RelayMib.FailedToday);

    public Task InitializeAsync() => StartAsync(new[] { "127.0.0.1" });

    public async Task DisposeAsync()
    {
        if (_agent is not null)
            await _agent.StopAsync(CancellationToken.None);
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private async Task StartAsync(string[] managers, bool storeCommunity = true)
    {
        if (_agent is not null)
            await _agent.StopAsync(CancellationToken.None);

        var options = new RelayOptions
        {
            DataDirectory = _dataDir,
            Snmp = { Enabled = true, IpAddress = "127.0.0.1", Port = 0, AllowedManagers = managers.ToList() }
        };
        var secrets = SecretStore.Open(options);
        if (storeCommunity)
            secrets.Set(options.Snmp.CommunitySecret, Community);
        else
            secrets.Remove(options.Snmp.CommunitySecret);

        _agent = new SnmpAgentService(options, secrets, _counters, NullLogger<SnmpAgentService>.Instance);
        await _agent.StartAsync(CancellationToken.None);
        for (int i = 0; i < 100 && _agent.BoundPort == 0; i++)
            await Task.Delay(20);
        Assert.NotEqual(0, _agent.BoundPort);
        _endpoint = new IPEndPoint(IPAddress.Loopback, _agent.BoundPort);
    }

    private IList<Variable> Get(VersionCode version, string community, params ObjectIdentifier[] oids)
        => Messenger.Get(version, _endpoint, new OctetString(community), oids.Select(o => new Variable(o)).ToList(), Timeout);

    [Theory]
    [InlineData(VersionCode.V1)]
    [InlineData(VersionCode.V2)]
    public void ReturnsTodaysFigures(VersionCode version)
    {
        var result = Get(version, Community, Delivered, Failed);

        Assert.Equal(2, result.Count);
        Assert.Equal(Delivered, result[0].Id);
        Assert.Equal(new Gauge32(1284), Assert.IsType<Gauge32>(result[0].Data));
        Assert.Equal(Failed, result[1].Id);
        Assert.Equal(new Gauge32(3), Assert.IsType<Gauge32>(result[1].Data));
    }

    [Fact]
    public void FiguresFollowTheCounters()
    {
        _counters.RecordDelivered();
        _counters.RecordFailed();
        _counters.RecordFailed();

        var result = Get(VersionCode.V2, Community, Delivered, Failed);
        Assert.Equal(new Gauge32(1285), result[0].Data);
        Assert.Equal(new Gauge32(5), result[1].Data);
    }

    [Fact]
    public void AWalkListsEveryObjectInOrder()
    {
        var found = new List<Variable>();
        Messenger.Walk(VersionCode.V2, _endpoint, new OctetString(Community), new ObjectIdentifier("1.3"), found, Timeout, WalkMode.WithinSubtree);

        Assert.Equal(
            new[] { "1.3.6.1.2.1.1.1.0", "1.3.6.1.2.1.1.2.0", "1.3.6.1.2.1.1.3.0", "1.3.6.1.2.1.1.5.0", Delivered.ToString(), Failed.ToString() },
            found.Select(v => v.Id.ToString()).ToArray());
        Assert.Contains("NEXQ SMTP Relay", found[0].Data.ToString());
        Assert.Equal(new ObjectIdentifier(RelayMib.Module), found[1].Data);
        Assert.IsType<TimeTicks>(found[2].Data);
        Assert.Equal(Environment.MachineName, found[3].Data.ToString());
    }

    [Fact]
    public void ABulkWalkOfTheModuleReturnsTheTwoGauges()
    {
        var found = new List<Variable>();
        Messenger.BulkWalk(VersionCode.V2, _endpoint, new OctetString(Community), OctetString.Empty,
            new ObjectIdentifier(RelayMib.Module), found, Timeout, 10, WalkMode.WithinSubtree, null, null);

        Assert.Equal(new[] { Delivered, Failed }, found.Select(v => v.Id).ToArray());
    }

    [Fact]
    public void AWalkWithVersion1EndsCleanly()
    {
        var found = new List<Variable>();
        Messenger.Walk(VersionCode.V1, _endpoint, new OctetString(Community), new ObjectIdentifier(RelayMib.Module), found, Timeout, WalkMode.WithinSubtree);
        Assert.Equal(new[] { Delivered, Failed }, found.Select(v => v.Id).ToArray());
    }

    [Fact]
    public void UnknownObjectsAreReportedTheWayEachVersionExpects()
    {
        var unknown = new ObjectIdentifier("1.3.6.1.4.1.1.2.3.0");
        var withoutInstance = new ObjectIdentifier(RelayMib.DeliveredToday[..^1]);

        var v2 = Get(VersionCode.V2, Community, unknown, withoutInstance, Failed);
        Assert.IsType<NoSuchObject>(v2[0].Data);
        Assert.IsType<NoSuchInstance>(v2[1].Data);
        Assert.Equal(new Gauge32(3), v2[2].Data); // the other variables are still answered

        var error = Assert.Throws<ErrorException>(() => Get(VersionCode.V1, Community, Failed, unknown));
        Assert.Equal(ErrorCode.NoSuchName, error.Body!.Pdu().ErrorStatus.ToErrorCode());
        Assert.Equal(2, error.Body!.Pdu().ErrorIndex.ToInt32());
    }

    [Fact]
    public void NothingCanBeWritten()
    {
        var error = Assert.Throws<ErrorException>(() => Messenger.Set(
            VersionCode.V2, _endpoint, new OctetString(Community),
            new List<Variable> { new(Failed, new Gauge32(0)) }, Timeout));
        Assert.Equal(ErrorCode.NotWritable, error.Body!.Pdu().ErrorStatus.ToErrorCode());
        Assert.Equal(3, _counters.Today.Failed);
    }

    [Fact]
    public void AWrongCommunityGetsNoAnswer()
    {
        Assert.Throws<Lextm.SharpSnmpLib.Messaging.TimeoutException>(() => Messenger.Get(
            VersionCode.V2, _endpoint, new OctetString("public"), new List<Variable> { new(Delivered) }, 700));
        Assert.Single(Get(VersionCode.V2, Community, Delivered)); // and the agent is still there
    }

    [Fact]
    public async Task WithoutAStoredCommunityNobodyIsAnswered()
    {
        await StartAsync(new[] { "127.0.0.1" }, storeCommunity: false);
        Assert.Throws<Lextm.SharpSnmpLib.Messaging.TimeoutException>(() => Messenger.Get(
            VersionCode.V2, _endpoint, new OctetString(""), new List<Variable> { new(Delivered) }, 700));
    }

    [Fact]
    public async Task AManagerOutsideTheListGetsNoAnswer()
    {
        await StartAsync(new[] { "192.0.2.10" });
        Assert.Throws<Lextm.SharpSnmpLib.Messaging.TimeoutException>(() => Messenger.Get(
            VersionCode.V2, _endpoint, new OctetString(Community), new List<Variable> { new(Delivered) }, 700));

        await StartAsync(Array.Empty<string>()); // an empty list refuses everyone
        Assert.Throws<Lextm.SharpSnmpLib.Messaging.TimeoutException>(() => Messenger.Get(
            VersionCode.V2, _endpoint, new OctetString(Community), new List<Variable> { new(Delivered) }, 700));
    }

    [Fact]
    public async Task GarbageIsIgnoredAndTheAgentKeepsAnswering()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var random = new Random(20261112);
        var valid = new GetRequestMessage(1, VersionCode.V2, new OctetString(Community), new List<Variable> { new(Delivered) }).ToBytes();

        for (int i = 0; i < 300; i++)
        {
            byte[] datagram;
            switch (i % 4)
            {
                case 0: // random bytes
                    datagram = new byte[random.Next(0, 200)];
                    random.NextBytes(datagram);
                    break;
                case 1: // a valid request cut short
                    datagram = valid[..random.Next(0, valid.Length)];
                    break;
                case 2: // a valid request with one byte changed
                    datagram = (byte[])valid.Clone();
                    datagram[random.Next(datagram.Length)] = (byte)random.Next(256);
                    break;
                default: // lengths that point outside the datagram
                    datagram = new byte[] { 0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF, 0x02, 0x01, 0x01 };
                    break;
            }
            await udp.SendAsync(datagram, datagram.Length, _endpoint);
        }

        Assert.Equal(new Gauge32(1284), Get(VersionCode.V2, Community, Delivered)[0].Data);
    }

    [Fact]
    public void ResponderRefusesOversizedAndUnsupportedRequests()
    {
        var responder = new SnmpResponder(RelayMib.CreateObjects(_counters, () => TimeSpan.Zero), () => Community);
        var valid = new GetRequestMessage(7, VersionCode.V2, new OctetString(Community), new List<Variable> { new(Delivered) }).ToBytes();

        Assert.NotNull(responder.Handle(valid));
        Assert.Null(responder.Handle(new byte[5000]));
        Assert.Null(responder.Handle(ReadOnlySpan<byte>.Empty));

        // SNMPv3 (version 3) is not spoken: no answer rather than a wrong one.
        var v3 = (byte[])valid.Clone();
        v3[4] = 3;
        Assert.Null(responder.Handle(v3));

        // Too many variables in one request.
        var many = new GetRequestMessage(8, VersionCode.V2, new OctetString(Community),
            Enumerable.Range(0, 40).Select(_ => new Variable(Delivered)).ToList()).ToBytes();
        Assert.Null(responder.Handle(many));
        Assert.Equal(0, responder.BadCommunityCount);
    }

    [Fact]
    public void TheMibFileUsesTheNumbersOfTheAgent()
    {
        var mib = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mib", RelayMib.MibFileName));

        Assert.Contains($"nexq          OBJECT IDENTIFIER ::= {{ enterprises {AppInfo.SnmpEnterpriseNumber} }}", mib);
        Assert.Contains("nexqProducts  OBJECT IDENTIFIER ::= { nexq 1 }", mib);
        Assert.Contains("::= { nexqProducts 1 }", mib);
        Assert.Contains("smtpRelayObjects      OBJECT IDENTIFIER ::= { nexqSmtpRelayMIB 1 }", mib);
        Assert.Contains("::= { smtpRelayObjects 1 }", mib);
        Assert.Contains("::= { smtpRelayObjects 2 }", mib);

        Assert.Equal($"1.3.6.1.4.1.{AppInfo.SnmpEnterpriseNumber}.1.1.1.1.0", RelayMib.ToText(RelayMib.DeliveredToday));
        Assert.Equal($"1.3.6.1.4.1.{AppInfo.SnmpEnterpriseNumber}.1.1.1.2.0", RelayMib.ToText(RelayMib.FailedToday));
        Assert.All(mib, c => Assert.True(c is '\r' or '\n' or (>= ' ' and <= '~'), "The MIB must be plain ASCII."));
    }
}

public class DeliveryCountersTests
{
    [Fact]
    public void CountersGoBackToZeroWhenTheDayChanges()
    {
        var now = new DateTime(2026, 11, 12, 23, 59, 50);
        var counters = new DeliveryCounters(() => now);

        counters.RecordDelivered();
        counters.RecordDelivered();
        counters.RecordFailed();
        Assert.Equal((2, 1), counters.Today);

        now = now.AddSeconds(15); // past midnight
        Assert.Equal((0, 0), counters.Today);
        counters.RecordFailed();
        Assert.Equal((0, 1), counters.Today);
    }

    [Fact]
    public void ARestartContinuesFromTheAuditTrail()
    {
        var dir = Directory.CreateTempSubdirectory("smtprelay-audit-").FullName;
        try
        {
            // The layout of the audit file: time stamp, then the event.
            const string prefix = "2026-11-12 10:00:00.000 +01:00 ";
            File.WriteAllLines(Path.Combine(dir, $"audit-{DateTime.Now:yyyyMMdd}.log"), new[]
            {
                prefix + "DELIVERED client=10.0.0.1 from=a@x.example to=b@y.example size=10 subject=Hello durationMs=5",
                prefix + "DELIVERED client=10.0.0.1 from=a@x.example to=b@y.example size=10 subject=Report: FAILED jobs durationMs=5",
                prefix + "FAILED client=10.0.0.1 from=a@x.example to=b@y.example size=10 subject=Was: DELIVERED yesterday permanent=False reason=4.4.1",
                "2026-11-12 10:00:00.000 +01:00 [INF] SmtpRelay.Smtp: something else",
                "2026-11-12 10:00:00.000 +01:00 [INF] SmtpRelay.Smtp: Sign-in FAILED for user 'x' from 10.0.0.1",
                "2026-11-12 10:00:00.000 +01:00 [INF] SmtpRelay.Audit: DELIVERED client=10.0.0.3 user=- from=a@x.example to=b@y.example size=10 subject=As the application log writes it durationMs=5",
                "not a log line"
            });
            // Rotated by size: the second file of the day counts too. Yesterday's does not.
            File.WriteAllLines(Path.Combine(dir, $"audit-{DateTime.Now:yyyyMMdd}_001.log"), new[] { prefix + "DELIVERED client=10.0.0.2 from=a@x.example to=b@y.example size=1 subject=x durationMs=1" });
            File.WriteAllLines(Path.Combine(dir, $"audit-{DateTime.Now.AddDays(-1):yyyyMMdd}.log"), new[] { prefix + "FAILED client=10.0.0.2 from=a@x.example to=b@y.example size=1 subject=x permanent=True reason=old" });

            Assert.Equal((4, 1), DeliveryCounters.FromAuditTrail(dir).Today);
            Assert.Equal((0, 0), DeliveryCounters.FromAuditTrail(Path.Combine(dir, "missing")).Today);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheAuditFileTheServiceWritesIsCountedByTheConsole()
    {
        // The real logger writes the file and the real reader counts it: the two must agree on the layout.
        var dir = Directory.CreateTempSubdirectory("smtprelay-auditfile-").FullName;
        try
        {
            var options = new RelayOptions { DataDirectory = dir };
            using (var logger = LoggingSetup.CreateLogger(options, runningAsService: false))
            {
                var audit = logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, LoggingSetup.AuditSourceContext);
                audit.Information("DELIVERED client={ClientIp} user={User} from={From} to={To} size={Size} subject={Subject} durationMs={Duration}",
                    "192.168.1.63", "-", "donotreply@contoso.com", "a@example.org", 264, "Report: FAILED jobs", 1205);
                audit.Information("DELIVERED client={ClientIp} user={User} from={From} to={To} size={Size} subject={Subject} durationMs={Duration}",
                    "192.168.1.63", "scanner", "scanner@contoso.com", "a@example.org", 9000, "Scan", 800);
                audit.Warning("FAILED client={ClientIp} user={User} from={From} to={To} size={Size} subject={Subject} permanent={Permanent} reason={Reason}",
                    "192.168.1.63", "-", "erp@contoso.com", "a@example.org", 10, "Was DELIVERED yesterday", false, "4.4.1 Smart host not available");
                logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "SmtpRelay.Smtp").Warning("Connection REFUSED: not a delivery");
            }

            var (delivered, failed, failures) = AuditTrail.ReadToday(options.LogDirectory);
            Assert.Equal((2, 1), (delivered, failed));
            Assert.Contains("FAILED client=192.168.1.63", Assert.Single(failures));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
