namespace SmtpRelay.Monitoring;

/// <summary>
/// The objects the built-in SNMP agent answers for. The numbers here and in mib\NEXQ-SMTP-RELAY-MIB.txt
/// must stay the same (a test compares them).
/// </summary>
public static class RelayMib
{
    /// <summary>File name of the MIB module shipped next to the executables (folder "mib").</summary>
    public const string MibFileName = "NEXQ-SMTP-RELAY-MIB.txt";

    /// <summary>iso.org.dod.internet.private.enterprises.nexq.nexqProducts.nexqSmtpRelayMIB</summary>
    public static uint[] Module { get; } = { 1, 3, 6, 1, 4, 1, AppInfo.SnmpEnterpriseNumber, 1, 1 };

    /// <summary>smtpRelayDeliveredToday.0 (Gauge32)</summary>
    public static uint[] DeliveredToday { get; } = Child(Module, 1, 1, 0);

    /// <summary>smtpRelayFailedToday.0 (Gauge32)</summary>
    public static uint[] FailedToday { get; } = Child(Module, 1, 2, 0);

    private static readonly uint[] System = { 1, 3, 6, 1, 2, 1, 1 };

    public static string ToText(IEnumerable<uint> oid) => string.Join('.', oid);

    /// <summary>
    /// The scalars of the agent: the two delivery gauges, plus the few objects of the standard system group
    /// that monitoring tools read to recognise a device (sysDescr, sysObjectID, sysUpTime, sysName).
    /// </summary>
    public static IReadOnlyList<SnmpScalar> CreateObjects(DeliveryCounters counters, Func<TimeSpan> upTime)
    {
        return new[]
        {
            new SnmpScalar(Child(System, 1, 0), () => SnmpValue.DisplayString($"{AppInfo.ProductName} {AppInfo.Version}")),
            new SnmpScalar(Child(System, 2, 0), () => SnmpValue.ObjectId(Module)),
            new SnmpScalar(Child(System, 3, 0), () => SnmpValue.TimeTicks((long)(upTime().TotalMilliseconds / 10))),
            new SnmpScalar(Child(System, 5, 0), () => SnmpValue.DisplayString(Environment.MachineName)),
            new SnmpScalar(DeliveredToday, () => SnmpValue.Gauge32(counters.Today.Delivered)),
            new SnmpScalar(FailedToday, () => SnmpValue.Gauge32(counters.Today.Failed)),
        };
    }

    private static uint[] Child(uint[] parent, params uint[] arcs) => parent.Concat(arcs).ToArray();
}
