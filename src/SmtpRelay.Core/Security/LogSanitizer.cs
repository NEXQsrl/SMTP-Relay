using System.Text;

namespace SmtpRelay.Security;

/// <summary>
/// Makes text that comes from the network (subjects, addresses, server replies) safe to write on one log line:
/// control characters, line breaks included, are replaced so that a sender cannot forge log or audit entries.
/// </summary>
public static class LogSanitizer
{
    public static string Clean(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (sb.Length >= maxLength)
            {
                sb.Append('…');
                break;
            }
            // Control characters plus the Unicode line and paragraph separators.
            sb.Append(char.IsControl(c) || c is (char)0x2028 or (char)0x2029 ? ' ' : c);
        }
        return sb.ToString();
    }
}
