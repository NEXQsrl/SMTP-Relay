using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SmtpRelay.Security;

/// <summary>
/// Encrypts and decrypts secrets with Windows DPAPI (LocalMachine scope) plus a per-installation entropy
/// kept in a file with a restricted ACL. The resulting blob is bound to the machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretProtector
{
    private readonly byte[] _entropy;

    public SecretProtector(byte[] entropy) => _entropy = entropy;

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var blob = ProtectedData.Protect(data, _entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string Unprotect(string protectedBase64)
    {
        var blob = Convert.FromBase64String(protectedBase64);
        var data = ProtectedData.Unprotect(blob, _entropy, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
