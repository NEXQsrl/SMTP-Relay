namespace SmtpRelay.Configuration;

/// <summary>Metodo di autenticazione verso lo smart host / Microsoft 365.</summary>
public enum UpstreamAuthMode
{
    /// <summary>Autenticazione di base (username + password).</summary>
    Basic,

    /// <summary>Microsoft 365 con OAuth2 Client Credentials (app-only).</summary>
    Microsoft365
}

/// <summary>Modalita del canale sicuro verso l'upstream.</summary>
public enum UpstreamSecureMode
{
    None,
    Auto,
    StartTls,
    StartTlsWhenAvailable,
    SslOnConnect
}

/// <summary>Configurazione dell'inoltro verso il server SMTP di destinazione.</summary>
public sealed class UpstreamOptions
{
    public UpstreamAuthMode Mode { get; set; } = UpstreamAuthMode.Microsoft365;

    /// <summary>Host SMTP di destinazione (es. smtp.office365.com).</summary>
    public string Host { get; set; } = "smtp.office365.com";

    public int Port { get; set; } = 587;

    public UpstreamSecureMode Security { get; set; } = UpstreamSecureMode.StartTls;

    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>Numero massimo di tentativi di consegna (incluso il primo).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Ritardo base fra i tentativi (secondi); cresce linearmente col tentativo.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>Se valorizzato, forza il mittente di busta (return-path) usato verso l'upstream.</summary>
    public string? OverrideFromAddress { get; set; }

    public BasicAuthOptions Basic { get; set; } = new();
    public Microsoft365Options Microsoft365 { get; set; } = new();
}

public sealed class BasicAuthOptions
{
    public string Username { get; set; } = "";

    /// <summary>Nome del segreto (store cifrato) che contiene la password.</summary>
    public string PasswordSecret { get; set; } = "UpstreamPassword";
}

public sealed class Microsoft365Options
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";

    /// <summary>Nome del segreto (store cifrato) che contiene il client secret dell'app registration.</summary>
    public string ClientSecretName { get; set; } = "M365ClientSecret";

    /// <summary>UPN della casella mittente (usato per XOAUTH2 e come mittente predefinito).</summary>
    public string SenderUsername { get; set; } = "";

    public string Scope { get; set; } = "https://outlook.office365.com/.default";
}
