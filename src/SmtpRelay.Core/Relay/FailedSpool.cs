using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace SmtpRelay.Relay;

/// <summary>What is known about a message that was not delivered, saved next to its copy.</summary>
public sealed class SpoolRecord
{
    public DateTime TimeUtc { get; set; }
    public string Client { get; set; } = "";
    public string User { get; set; } = "";
    public string From { get; set; } = "";
    public List<string> Recipients { get; set; } = new();
    public string Subject { get; set; } = "";
    public string? MessageId { get; set; }
    public bool Permanent { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>A message of the spool, as listed by the console.</summary>
/// <param name="HasEnvelope">
/// False for a copy saved by a version that did not record the SMTP envelope: sender and recipients are then
/// taken from the headers, which do not include hidden (Bcc) recipients.
/// </param>
public sealed record SpooledMessage(string Path, SpoolRecord Record, long SizeBytes, bool HasEnvelope)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The folder of the messages that could not be delivered: each one is an .eml file with a .json file of the
/// same name that holds the SMTP envelope and the reason. The relay has no queue: the client that received a
/// temporary error is expected to send again, and these copies let an administrator look at a message, or send
/// it again by hand. A copy is removed when the same message is delivered later.
/// </summary>
public sealed class FailedSpool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public FailedSpool(string directory) => _directory = directory;

    public string Directory => _directory;

    /// <summary>Saves a copy of the message. The same message sent again by a client replaces its previous copy.</summary>
    public async Task<string> SaveAsync(MimeMessage message, SpoolRecord record, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(_directory);

        record.MessageId = message.MessageId;
        var hash = HashOf(message.MessageId ?? Guid.NewGuid().ToString("N"));
        RemoveByHash(hash);

        var path = System.IO.Path.Combine(_directory, $"failed-{record.TimeUtc:yyyyMMdd}-{hash}.eml");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            await message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(RecordPath(path), JsonSerializer.Serialize(record, JsonOptions), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Removes the copies of a message that has now been delivered (a client that retried after a temporary
    /// error), so that the spool only holds what is still undelivered. Returns how many copies were removed.
    /// </summary>
    public int RemoveDelivered(string? messageId)
        => string.IsNullOrEmpty(messageId) ? 0 : RemoveByHash(HashOf(messageId));

    /// <summary>The messages of the spool, newest first.</summary>
    public IReadOnlyList<SpooledMessage> List(int max = 500)
    {
        var result = new List<SpooledMessage>();
        if (!System.IO.Directory.Exists(_directory))
            return result;

        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_directory).GetFiles("failed-*.eml").OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList();
        }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }

        foreach (var file in files)
        {
            try
            {
                var record = ReadRecord(file.FullName);
                result.Add(record is not null
                    ? new SpooledMessage(file.FullName, record, file.Length, HasEnvelope: true)
                    : new SpooledMessage(file.FullName, RecordFromHeaders(file), file.Length, HasEnvelope: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Being written or removed right now, or not a message: leave it out of the list.
            }
        }
        return result.OrderByDescending(m => m.Record.TimeUtc).ToList();
    }

    public int Count
    {
        get
        {
            try
            {
                return System.IO.Directory.Exists(_directory) ? System.IO.Directory.EnumerateFiles(_directory, "failed-*.eml").Count() : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }
    }

    public static MimeMessage Load(SpooledMessage message)
    {
        using var stream = new FileStream(message.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return MimeMessage.Load(stream);
    }

    public void Delete(SpooledMessage message) => DeleteFiles(message.Path);

    /// <summary>Deletes the copies older than the retention. Returns how many were deleted.</summary>
    public int Cleanup(int retentionDays)
    {
        if (retentionDays <= 0 || !System.IO.Directory.Exists(_directory))
            return 0;

        var limit = DateTime.UtcNow.AddDays(-retentionDays);
        int removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "failed-*.eml"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    DeleteFiles(file);
                    removed++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private int RemoveByHash(string hash)
    {
        int removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(_directory))
                return 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, $"failed-*-{hash}.eml"))
            {
                DeleteFiles(file);
                removed++;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return removed;
    }

    private static void DeleteFiles(string emlPath)
    {
        File.Delete(emlPath);
        File.Delete(RecordPath(emlPath));
    }

    private static string RecordPath(string emlPath) => System.IO.Path.ChangeExtension(emlPath, ".json");

    private static string HashOf(string messageId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)), 0, 8).ToLowerInvariant();

    private static SpoolRecord? ReadRecord(string emlPath)
    {
        var path = RecordPath(emlPath);
        if (!File.Exists(path))
            return null;
        try { return JsonSerializer.Deserialize<SpoolRecord>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    private static SpoolRecord RecordFromHeaders(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var headers = new MimeParser(stream, MimeFormat.Entity).ParseHeaders();

        var record = new SpoolRecord
        {
            TimeUtc = file.LastWriteTimeUtc,
            Subject = headers[HeaderId.Subject] ?? "",
            MessageId = headers[HeaderId.MessageId]
        };
        record.From = Addresses(headers[HeaderId.From]).FirstOrDefault() ?? "";
        record.Recipients = Addresses(headers[HeaderId.To])
            .Concat(Addresses(headers[HeaderId.Cc]))
            .Concat(Addresses(headers[HeaderId.Bcc]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return record;
    }

    private static IEnumerable<string> Addresses(string? header)
        => !string.IsNullOrWhiteSpace(header) && InternetAddressList.TryParse(header, out var list)
            ? list.Mailboxes.Select(m => m.Address).Where(a => !string.IsNullOrWhiteSpace(a))
            : Enumerable.Empty<string>();
}
