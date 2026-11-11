namespace SmtpRelay.Configuration;

/// <summary>Inbound SMTP listener settings.</summary>
public sealed class ListenerOptions
{
    /// <summary>IP address to listen on. "0.0.0.0" = every IPv4 interface, "::" = every IPv6 interface.</summary>
    public string IpAddress { get; set; } = "0.0.0.0";

    /// <summary>Listening port.</summary>
    public int Port { get; set; } = 25;

    /// <summary>Name announced in the banner and in the EHLO reply.</summary>
    public string ServerName { get; set; } = "smtp-relay";

    /// <summary>Maximum message size in bytes.</summary>
    public long MaxMessageSizeBytes { get; set; } = 26_214_400; // 25 MB

    /// <summary>Maximum number of recipients per message.</summary>
    public int MaxRecipients { get; set; } = 100;

    /// <summary>How long to wait for the next command from a client (seconds).</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum number of client sessions served at the same time. Further connections are closed.</summary>
    public int MaxConcurrentSessions { get; set; } = 50;
}
