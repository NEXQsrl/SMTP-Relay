namespace SmtpRelay.Configuration;

/// <summary>
/// Built-in SNMP agent (v1 / v2c, read-only) that reports the messages delivered and failed today.
/// It is separate from the Windows SNMP service, which keeps UDP port 161: this agent listens on its own port.
/// </summary>
public sealed class SnmpOptions
{
    /// <summary>Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Address to listen on (0.0.0.0 = every IPv4 interface).</summary>
    public string IpAddress { get; set; } = "0.0.0.0";

    /// <summary>UDP port. Not 161 by default, because the Windows SNMP service normally owns it.</summary>
    public int Port { get; set; } = 1161;

    /// <summary>Name of the secret (encrypted store) that holds the community the monitoring system must present.</summary>
    public string CommunitySecret { get; set; } = "SnmpCommunity";

    /// <summary>
    /// IP addresses or CIDR networks of the monitoring systems allowed to query the agent.
    /// When empty nobody is answered.
    /// </summary>
    public List<string> AllowedManagers { get; set; } = new();
}
