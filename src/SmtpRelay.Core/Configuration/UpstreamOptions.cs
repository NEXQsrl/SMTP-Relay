using System.Text.Json.Serialization;

namespace SmtpRelay.Configuration;

/// <summary>How the relay authenticates to the smart host.</summary>
public enum UpstreamAuthMode
{
    /// <summary>User name and password.</summary>
    Basic,

    /// <summary>Microsoft 365 with OAuth2 client credentials (app-only, XOAUTH2).</summary>
    Microsoft365,

    /// <summary>No authentication (for example an Exchange Online connector that trusts the public IP).</summary>
    Anonymous,

    /// <summary>Azure Communication Services Email over SMTP, with a Microsoft Entra application (XOAUTH2).</summary>
    AzureCommunicationServices
}

/// <summary>How a Microsoft Entra application proves its identity when it asks for a token.</summary>
public enum EntraCredentialType
{
    /// <summary>A client secret kept in the encrypted secret store.</summary>
    ClientSecret,

    /// <summary>A certificate with its private key in the LocalMachine\My store.</summary>
    Certificate
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
    public AzureCommunicationOptions AzureCommunication { get; set; } = new();

    /// <summary>The Entra application of the current mode, or null when the mode does not use one.</summary>
    [JsonIgnore]
    public EntraApplicationOptions? EntraApplication => Mode switch
    {
        UpstreamAuthMode.Microsoft365 => Microsoft365,
        UpstreamAuthMode.AzureCommunicationServices => AzureCommunication,
        _ => null
    };

    /// <summary>User name sent with the access token (XOAUTH2), or null when the mode does not use a token.</summary>
    [JsonIgnore]
    public string? TokenUserName => Mode switch
    {
        UpstreamAuthMode.Microsoft365 => Microsoft365.SenderUsername,
        UpstreamAuthMode.AzureCommunicationServices => AzureCommunication.SmtpUsername,
        _ => null
    };
}

public sealed class BasicAuthOptions
{
    public string Username { get; set; } = "";

    /// <summary>Name of the secret (encrypted store) that holds the password.</summary>
    public string PasswordSecret { get; set; } = "UpstreamPassword";
}

/// <summary>Microsoft Entra application (app registration) used to get an access token with the client credentials flow.</summary>
public abstract class EntraApplicationOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";

    /// <summary>Client secret (default) or certificate.</summary>
    public EntraCredentialType Credential { get; set; } = EntraCredentialType.ClientSecret;

    /// <summary>Name of the secret (encrypted store) that holds the client secret of the app registration.</summary>
    public string ClientSecretName { get; set; } = "";

    /// <summary>
    /// Thumbprint of the certificate in LocalMachine\My whose public part was uploaded to the app registration.
    /// Used when <see cref="Credential"/> is Certificate. The service account must be able to read the private key.
    /// </summary>
    public string CertificateThumbprint { get; set; } = "";

    public string Scope { get; set; } = "";
}

public sealed class Microsoft365Options : EntraApplicationOptions
{
    public Microsoft365Options()
    {
        ClientSecretName = "M365ClientSecret";
        Scope = "https://outlook.office365.com/.default";
    }

    /// <summary>UPN of the sending mailbox (used for XOAUTH2 and as the default sender).</summary>
    public string SenderUsername { get; set; } = "";
}

public sealed class AzureCommunicationOptions : EntraApplicationOptions
{
    public const string DefaultHost = "smtp.azurecomm.net";

    public AzureCommunicationOptions()
    {
        ClientSecretName = "AcsClientSecret";
        Scope = "https://communication.azure.com/.default";
    }

    /// <summary>
    /// The SMTP Username created on the Communication Services resource and linked to the Entra application
    /// (Azure portal, resource, "SMTP Usernames").
    /// </summary>
    public string SmtpUsername { get; set; } = "";
}
