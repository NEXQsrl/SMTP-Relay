namespace SmtpRelay.Configuration;

/// <summary>Whether clients sign in to the relay with a user name and a password (SMTP AUTH PLAIN / LOGIN).</summary>
public enum InboundAuthMode
{
    /// <summary>Nobody signs in: the IP allowlist alone decides who may send.</summary>
    Disabled,

    /// <summary>Clients may sign in. Addresses of the allowlist can still send without signing in.</summary>
    Optional,

    /// <summary>Every client must sign in before it can send.</summary>
    Required
}

/// <summary>
/// Sign-in of the clients with the users kept by the relay itself (see <c>RelayUserStore</c>): they are not
/// Windows accounts and have nothing to do with the credentials the relay presents to the smart host.
/// </summary>
public sealed class InboundAuthenticationOptions
{
    /// <summary>Off by default: existing installations keep working on the allowlist alone.</summary>
    public InboundAuthMode Mode { get; set; } = InboundAuthMode.Disabled;

    /// <summary>
    /// When true, addresses outside the allowlist may connect, but can send only after signing in.
    /// When false (default) the allowlist applies to everyone, signed in or not.
    /// </summary>
    public bool AllowFromAnyAddress { get; set; }

    /// <summary>
    /// When false (default) a password is accepted only on a session encrypted with STARTTLS.
    /// Set to true for devices that cannot use TLS: user names and passwords then travel in clear text.
    /// </summary>
    public bool AllowWithoutTls { get; set; }
}
