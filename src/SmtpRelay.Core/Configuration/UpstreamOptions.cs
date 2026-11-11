namespace SmtpRelay.Configuration;

/// <summary>How the relay authenticates to the smart host.</summary>
public enum UpstreamAuthMode
{
    /// <summary>User name and password.</summary>
    Basic,

    /// <summary>Microsoft 365 with OAuth2 client credentials (app-only, XOAUTH2).</summary>
    Microsoft365,

    /// <summary>No authentication (for example an Exchange Online connector that trusts the public IP).</summary>
    Anonymous
}

/// <summary>Transport security of the connection to the smart host.</summary>
public enum UpstreamSecureMode
{
    /// <summary>Clear text.</summary>
    None,

    /// <summary>Implicit TLS on port 465, required STARTTLS on any other port.</summary>
    Auto,

    /// <summary>STARTTLS required.</summary>
    StartTls,

    /// <summary>STARTTLS if the server offers it, clear text otherwise (can be downgraded by an attacker).</summary>
    StartTlsWhenAvailable,

    /// <summary>Implicit TLS (usually port 465).</summary>
    SslOnConnect
}

/// <summary>Settings of the server the mail is forwarded to.</summary>
public sealed class UpstreamOptions
{
    public UpstreamAuthMode Mode { get; set; } = UpstreamAuthMode.Microsoft365;

    /// <summary>Destination SMTP host (for example smtp.office365.com).</summary>
    public string Host { get; set; } = "smtp.office365.com";

    public int Port { get; set; } = 587;

    public UpstreamSecureMode Security { get; set; } = UpstreamSecureMode.StartTls;

    /// <summary>
    /// Allows sending credentials or tokens over a connection that is not guaranteed to be encrypted
    /// (Security = None or StartTlsWhenAvailable). Off by default.
    /// </summary>
    public bool AllowInsecureAuthentication { get; set; }

    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>Maximum number of delivery attempts (the first one included).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base delay between attempts (seconds); it grows linearly with the attempt number.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>When set, replaces the envelope sender (return-path) used towards the smart host.</summary>
    public string? OverrideFromAddress { get; set; }

    public BasicAuthOptions Basic { get; set; } = new();
    public Microsoft365Options Microsoft365 { get; set; } = new();
}

public sealed class BasicAuthOptions
{
    public string Username { get; set; } = "";

    /// <summary>Name of the secret (encrypted store) that holds the password.</summary>
    public string PasswordSecret { get; set; } = "UpstreamPassword";
}

public sealed class Microsoft365Options
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";

    /// <summary>Name of the secret (encrypted store) that holds the client secret of the app registration.</summary>
    public string ClientSecretName { get; set; } = "M365ClientSecret";

    /// <summary>UPN of the sending mailbox (used for XOAUTH2 and as the default sender).</summary>
    public string SenderUsername { get; set; } = "";

    public string Scope { get; set; } = "https://outlook.office365.com/.default";
}
