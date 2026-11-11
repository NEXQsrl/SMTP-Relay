using System.IO;
using System.Text;

namespace SmtpRelay.Manager.Services;

/// <summary>Reads the log files written by the service without locking them.</summary>
public static class LogReader
{
    private const int TailBytes = 512 * 1024;

    /// <summary>Log files of the folder, newest first.</summary>
    public static IReadOnlyList<string> ListFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.Name)
                    .ToList()
                : Array.Empty<string>();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Last lines of a file (at most <paramref name="maxLines"/>), optionally only those that contain a text.</summary>
    public static IReadOnlyList<string> Tail(string path, int maxLines, string? filter = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool truncated = stream.Length > TailBytes;
            if (truncated)
                stream.Seek(-TailBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            if (truncated)
                reader.ReadLine(); // drop the partial first line

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrEmpty(filter) || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    lines.Add(line);
            }

            return lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return new[] { "Access to the log file was denied. Run the console as administrator." }; }
    }

    /// <summary>Counts today's delivered and failed messages from the audit trail and returns the latest failures.</summary>
    public static (int Delivered, int Failed, IReadOnlyList<string> RecentFailures) ReadTodayStats(string directory)
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
                    if (line.Contains(" DELIVERED ", StringComparison.Ordinal))
                        delivered++;
                    else if (line.Contains(" FAILED ", StringComparison.Ordinal))
                    {
                        failed++;
                        failures.Add(line);
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (failures.Count > 5)
            failures = failures.GetRange(failures.Count - 5, 5);
        failures.Reverse();
        return (delivered, failed, failures);
    }
}
