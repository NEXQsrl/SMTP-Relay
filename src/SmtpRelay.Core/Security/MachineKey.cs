using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace SmtpRelay.Security;

/// <summary>
/// Per-installation entropy used by <see cref="SecretProtector"/>.
/// 32 random bytes are generated on first use and protected with an NTFS ACL.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineKey
{
    private const int Length = 32;

    public static byte[] LoadOrCreate(string entropyFilePath)
    {
        var dir = Path.GetDirectoryName(entropyFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(entropyFilePath))
        {
            var existing = File.ReadAllBytes(entropyFilePath);
            if (existing.Length != Length)
                throw new InvalidDataException(
                    $"The entropy file '{entropyFilePath}' is damaged ({existing.Length} bytes instead of {Length}). " +
                    "Restore it from a backup, or delete it together with secrets.dat and enter the secrets again.");
            return existing;
        }

        var entropy = RandomNumberGenerator.GetBytes(Length);
        // CreateNew: never overwrite a file that appeared in the meantime.
        using (var stream = new FileStream(entropyFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(entropy);
        try { WindowsAcl.RestrictToSystemAndAdmins(entropyFilePath); } catch (Exception) { /* best effort: the folder ACL still applies */ }
        return entropy;
    }
}
