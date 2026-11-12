using System.Text;

namespace SmtpRelay.Monitoring;

/// <summary>
/// Messages delivered and failed today (local time), as reported to monitoring. Thread-safe; the counters
/// go back to zero when the day changes.
/// </summary>
public sealed class DeliveryCounters
{
    private readonly Func<DateTime> _clock;
    private readonly object _sync = new();
    private DateOnly _day;
    private int _delivered;
    private int _failed;

    public DeliveryCounters(Func<DateTime>? clock = null, int delivered = 0, int failed = 0)
    {
        _clock = clock ?? (() => DateTime.Now);
        _day = DateOnly.FromDateTime(_clock());
        _delivered = delivered;
        _failed = failed;
    }

    /// <summary>
    /// Starts from what the audit trail already holds for today, so that a restart of the service does not
    /// reset the figures (and they match what the management console shows).
    /// </summary>
    public static DeliveryCounters FromAuditTrail(string logDirectory)
    {
        var (delivered, failed, _) = AuditTrail.ReadToday(logDirectory);
        return new DeliveryCounters(null, delivered, failed);
    }

    public void RecordDelivered()
    {
        lock (_sync)
        {
            Roll();
            _delivered++;
        }
    }

    public void RecordFailed()
    {
        lock (_sync)
        {
            Roll();
            _failed++;
        }
    }

    public (int Delivered, int Failed) Today
    {
        get
        {
            lock (_sync)
            {
                Roll();
                return (_delivered, _failed);
            }
        }
    }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(_clock());
        if (today == _day)
            return;
        _day = today;
        _delivered = 0;
        _failed = 0;
    }
}

/// <summary>Reads the delivery events of the audit trail (audit-yyyyMMdd.log) without locking the files.</summary>
public static class AuditTrail
{
    private const string DeliveredMarker = ": DELIVERED ";
    private const string FailedMarker = ": FAILED ";

    /// <summary>Counts today's delivered and failed messages and returns the latest failures, newest first.</summary>
    public static (int Delivered, int Failed, IReadOnlyList<string> RecentFailures) ReadToday(string directory, int maxFailures = 5)
    {
        int delivered = 0, failed = 0;
        var failures = new List<string>();
        try
        {
            if (!Directory.Exists(directory))
                return (0, 0, failures);

            // audit-yyyyMMdd.log, plus audit-yyyyMMdd_001.log... when the file was rotated by size.
            foreach (var file in Directory.EnumerateFiles(directory, $"audit-{DateTime.Now:yyyyMMdd}*.log").OrderBy(f => f, StringComparer.Ordinal))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    switch (Classify(line))
                    {
                        case true:
                            delivered++;
                            break;
                        case false:
                            failed++;
                            failures.Add(line);
                            break;
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (failures.Count > maxFailures)
            failures = failures.GetRange(failures.Count - maxFailures, maxFailures);
        failures.Reverse();
        return (delivered, failed, failures);
    }

    /// <summary>
    /// True for a delivery, false for a failure, null for anything else. The event name is the first thing
    /// after the logger name, so a subject that contains one of the words cannot change the outcome.
    /// </summary>
    internal static bool? Classify(string line)
    {
        int delivered = line.IndexOf(DeliveredMarker, StringComparison.Ordinal);
        int failed = line.IndexOf(FailedMarker, StringComparison.Ordinal);
        if (delivered < 0 && failed < 0)
            return null;
        if (failed < 0)
            return true;
        if (delivered < 0)
            return false;
        return delivered < failed;
    }
}
