namespace SmtpRelay.Configuration;

/// <summary>Inbound access control: allowlist of IP addresses and CIDR networks.</summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// IP addresses or CIDR networks allowed to connect to the relay.
    /// Examples: "127.0.0.1", "::1", "192.168.1.0/24", "10.0.0.0/8", "2001:db8::/32".
    /// </summary>
    public List<string> AllowedIps { get; set; } = new();

    /// <summary>
    /// When true and the allowlist is empty, every connection is refused (fail-closed).
    /// When false and the allowlist is empty, every connection is accepted (open relay: not recommended).
    /// </summary>
    public bool RejectWhenAllowListEmpty { get; set; } = true;

    /// <summary>Sign-in of the clients with a user name and a password (off by default).</summary>
    public InboundAuthenticationOptions Authentication { get; set; } = new();
}
