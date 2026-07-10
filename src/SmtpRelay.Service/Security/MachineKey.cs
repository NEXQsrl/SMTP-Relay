using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace SmtpRelay.Security;

/// <summary>
/// Gestisce l'entropia per-installazione usata da <see cref="SecretProtector"/>.
/// Genera 32 byte casuali al primo utilizzo e li protegge con ACL NTFS.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineKey
{
    public static byte[] LoadOrCreate(string entropyFilePath)
    {
        var dir = Path.GetDirectoryName(entropyFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(entropyFilePath))
            return File.ReadAllBytes(entropyFilePath);

        var entropy = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(entropyFilePath, entropy);
        try { WindowsAcl.RestrictToSystemAndAdmins(entropyFilePath); } catch { /* best effort */ }
        return entropy;
    }
}
