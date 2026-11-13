using System.Globalization;
using System.Net;
using System.Text;
using SmtpRelay.Security;

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

/// <summary>What one client address did, according to the audit trail.</summary>
public sealed record SenderActivity(IPAddress Address, int Delivered, int Failed, DateTimeOffset LastSeen);

/// <summary>The activity of the clients over the days the audit trail is kept.</summary>
/// <param name="Since">Day of the oldest audit file that was read; null when there is none.</param>
public sealed record SenderReport(IReadOnlyList<SenderActivity> Senders, DateOnly? Since)
{
    public static SenderReport Empty { get; } = new(Array.Empty<SenderActivity>(), null);

    /// <summary>Adds up the clients that an allowlist entry (an address or a CIDR network) covers.</summary>
    public (int Delivered, int Failed, DateTimeOffset? LastSeen) For(string allowListEntry)
    {
        if (!IpAllowList.TryParse(allowListEntry, out var range))
            return (0, 0, null);

        int delivered = 0, failed = 0;
        DateTimeOffset? last = null;
        foreach (var sender in Senders)
        {
            if (!range.Contains(sender.Address))
                continue;
            delivered += sender.Delivered;
            failed += sender.Failed;
            if (last is null || sender.LastSeen > last)
                last = sender.LastSeen;
        }
        return (delivered, failed, last);
    }
}

/// <summary>Reads the delivery events of the audit trail (audit-yyyyMMdd.log) without locking the files.</summary>
public static class AuditTrail
{
    private const string DeliveredEvent = "DELIVERED ";
    private const string FailedEvent = "FAILED ";

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
    /// Reads every audit file that is still kept and adds up, for each client address, the messages it sent
    /// and the last time it was seen.
    /// </summary>
    public static SenderReport ReadSenders(string directory)
    {
        var senders = new Dictionary<IPAddress, (int Delivered, int Failed, DateTimeOffset Last)>();
        DateOnly? since = null;
        try
        {
            if (!Directory.Exists(directory))
                return SenderReport.Empty;

            foreach (var file in Directory.EnumerateFiles(directory, "audit-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length >= 14 && DateOnly.TryParseExact(name.AsSpan(6, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    (since is null || day < since))
                {
                    since = day;
                }

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (Classify(line) is not { } delivered || !TryReadClient(line, out var address, out var time))
                        continue;

                    senders.TryGetValue(address, out var entry);
                    senders[address] = (
                        entry.Delivered + (delivered ? 1 : 0),
                        entry.Failed + (delivered ? 0 : 1),
                        time > entry.Last ? time : entry.Last);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new SenderReport(
            senders.Select(p => new SenderActivity(p.Key, p.Value.Delivered, p.Value.Failed, p.Value.Last)).ToList(),
            since);
    }

    /// <summary>
    /// Counts the delivered and the failed messages in consecutive periods of the same length (for the chart
    /// of the console). Period i starts at <paramref name="start"/> + i * <paramref name="size"/>, local time.
    /// </summary>
    public static (int[] Delivered, int[] Failed) ReadBuckets(string directory, DateTime start, TimeSpan size, int count)
    {
        var delivered = new int[count];
        var failed = new int[count];
        try
        {
            if (!Directory.Exists(directory) || count <= 0 || size <= TimeSpan.Zero)
                return (delivered, failed);

            var firstDay = DateOnly.FromDateTime(start).AddDays(-1); // a file is named after the day it was started
            foreach (var file in Directory.EnumerateFiles(directory, "audit-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length < 14 ||
                    !DateOnly.TryParseExact(name.AsSpan(6, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                    day < firstDay)
                {
                    continue;
                }

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (Classify(line) is not { } ok || line.Length < 30 ||
                        !DateTimeOffset.TryParseExact(line.AsSpan(0, 30), "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    {
                        continue;
                    }

                    double position = (time.LocalDateTime - start) / size;
                    if (position < 0 || position >= count)
                        continue;
                    if (ok)
                        delivered[(int)position]++;
                    else
                        failed[(int)position]++;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return (delivered, failed);
    }

    // "yyyy-MM-dd HH:mm:ss.fff +zz:zz EVENT client=<address> ...": the client is the first field after the event.
    private static bool TryReadClient(string line, out IPAddress address, out DateTimeOffset time)
    {
        address = IPAddress.None;
        time = default;

        const string field = " client=";
        int start = line.IndexOf(field, StringComparison.Ordinal);
        if (start < 0 || line.Length < 30)
            return false;
        start += field.Length;
        int end = line.IndexOf(' ', start);
        if (end < 0)
            end = line.Length;

        if (!IPAddress.TryParse(line.AsSpan(start, end - start), out var parsed))
            return false;
        if (!DateTimeOffset.TryParseExact(line.AsSpan(0, 30), "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return false;

        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    /// <summary>
    /// True for a delivery, false for a failure, null for anything else. An audit line is
    /// "yyyy-MM-dd HH:mm:ss.fff +zz:zz EVENT client=...": the event is read at its fixed place, right after
    /// the time stamp, so a subject that contains one of the words cannot change the outcome.
    /// </summary>
    internal static bool? Classify(string line)
    {
        // Skip the three parts of the time stamp: date, time, offset.
        int start = 0;
        for (int i = 0; i < 3; i++)
        {
            start = line.IndexOf(' ', start);
            if (start < 0)
                return null;
            start++;
        }

        var rest = line.AsSpan(start);

        // The same event as the application log writes it: "[INF] SmtpRelay.Audit: EVENT ...".
        if (rest.StartsWith("[", StringComparison.Ordinal))
        {
            int colon = rest.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
                return null;
            rest = rest[(colon + 2)..];
        }

        if (rest.StartsWith(DeliveredEvent, StringComparison.Ordinal))
            return true;
        if (rest.StartsWith(FailedEvent, StringComparison.Ordinal))
            return false;
        return null;
    }
}
