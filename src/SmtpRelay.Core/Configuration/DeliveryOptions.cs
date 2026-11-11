namespace SmtpRelay.Configuration;

/// <summary>Outbound delivery settings.</summary>
public sealed class DeliveryOptions
{
    /// <summary>When true, a message that could not be delivered is saved as .eml for diagnostics.</summary>
    public bool SpoolFailedMessages { get; set; } = true;

    /// <summary>Folder of the failed-message spool. Empty = {DataDirectory}\failed.</summary>
    public string? FailedSpoolDirectory { get; set; }

    /// <summary>Spooled messages older than this many days are deleted. 0 = keep forever.</summary>
    public int FailedSpoolRetentionDays { get; set; } = 30;
}
