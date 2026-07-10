using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SmtpRelay.Security;

/// <summary>
/// Cifra/decifra segreti con Windows DPAPI (scope LocalMachine) piu un'entropia aggiuntiva
/// per-installazione conservata in un file con ACL ristretta. Il blob risultante e legato alla macchina.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretProtector
{
    private readonly byte[] _entropy;

    public SecretProtector(byte[] entropy) => _entropy = entropy;

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var blob = ProtectedData.Protect(data, _entropy, DataProtectionScope.LocalMachine);
        CryptographicOperations.ZeroMemory(data);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string protectedBase64)
    {
        var blob = Convert.FromBase64String(protectedBase64);
        var data = ProtectedData.Unprotect(blob, _entropy, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
