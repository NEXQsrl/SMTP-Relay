namespace SmtpRelay.Configuration;

/// <summary>Update check settings (used by the management console only; the service never goes online for this).</summary>
public sealed class UpdateOptions
{
    /// <summary>When true, the management console asks GitHub for the latest release each time it is opened.</summary>
    public bool CheckOnStartup { get; set; } = true;
}
