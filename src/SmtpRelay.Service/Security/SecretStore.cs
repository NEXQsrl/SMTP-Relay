using System.Runtime.Versioning;
using System.Text.Json;

namespace SmtpRelay.Security;

/// <summary>
/// Archivio di segreti nominati (es. password upstream, client secret M365, password del .pfx).
/// Ogni valore e cifrato individualmente con <see cref="SecretProtector"/> e salvato in un file JSON
/// con ACL ristretta a SYSTEM/Administrators.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SecretProtector _protector;
    private readonly object _sync = new();
    private Dictionary<string, string> _protectedValues;

    public SecretStore(string path, SecretProtector protector)
    {
        _path = path;
        _protector = protector;
        _protectedValues = Load(path);
    }

    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return data is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Restituisce il segreto in chiaro o null se assente/indecifrabile.</summary>
    public string? Get(string name)
    {
        lock (_sync)
        {
            if (!_protectedValues.TryGetValue(name, out var blob))
                return null;
            try { return _protector.Unprotect(blob); }
            catch { return null; }
        }
    }

    public bool Contains(string name)
    {
        lock (_sync) { return _protectedValues.ContainsKey(name); }
    }

    public IReadOnlyCollection<string> Names
    {
        get { lock (_sync) { return _protectedValues.Keys.ToArray(); } }
    }

    /// <summary>Cifra e salva un segreto (sovrascrive se esistente).</summary>
    public void Set(string name, string plaintext)
    {
        lock (_sync)
        {
            _protectedValues[name] = _protector.Protect(plaintext);
            Save();
        }
    }

    public bool Remove(string name)
    {
        lock (_sync)
        {
            if (!_protectedValues.Remove(name))
                return false;
            Save();
            return true;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_protectedValues, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
        try { WindowsAcl.RestrictToSystemAndAdmins(_path); } catch { /* best effort */ }
    }
}
