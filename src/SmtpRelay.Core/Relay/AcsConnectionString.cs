using System.Diagnostics.CodeAnalysis;

namespace SmtpRelay.Relay;

/// <summary>
/// The connection string of an Azure Communication Services resource, as the Azure portal shows it under
/// Settings > Keys: <c>endpoint=https://name.region.communication.azure.com/;accesskey=Base64</c>.
/// It holds the access key, so it lives in the secret store and never in the settings file.
/// </summary>
public sealed class AcsConnectionString
{
    private AcsConnectionString(Uri endpoint, byte[] accessKey)
    {
        Endpoint = endpoint;
        AccessKey = accessKey;
    }

    /// <summary>Address of the resource. Not a secret: it can be shown and logged.</summary>
    public Uri Endpoint { get; }

    /// <summary>The key that signs every request. It never leaves this assembly.</summary>
    internal byte[] AccessKey { get; }

    /// <summary>Reads a connection string. On failure <paramref name="error"/> says what is wrong, without repeating the value.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AcsConnectionString? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The connection string is empty.";
            return false;
        }

        string? endpoint = null, accessKey = null;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Only the first '=' separates the name: the access key ends with '=' padding of its own.
            int separator = part.IndexOf('=');
            if (separator <= 0)
                continue;
            var name = part[..separator].Trim();
            var content = part[(separator + 1)..].Trim();
            if (name.Equals("endpoint", StringComparison.OrdinalIgnoreCase))
                endpoint = content;
            else if (name.Equals("accesskey", StringComparison.OrdinalIgnoreCase))
                accessKey = content;
        }

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(accessKey))
        {
            error = "This is not a connection string: it must look like 'endpoint=https://...;accesskey=...'. " +
                    "Copy the whole \"Connection string\" value from the Azure portal (Communication Services resource > Settings > Keys), not the key alone.";
            return false;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            error = "The endpoint of the connection string must be an https address, for example https://name.europe.communication.azure.com/.";
            return false;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(accessKey);
        }
        catch (FormatException)
        {
            key = Array.Empty<byte>();
        }
        if (key.Length == 0)
        {
            error = "The access key of the connection string is not valid: copy the connection string again from the Azure portal.";
            return false;
        }

        result = new AcsConnectionString(uri, key);
        error = null;
        return true;
    }
}
