using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using SmtpRelay.Configuration;

namespace SmtpRelay.Security;

/// <summary>
/// Store of named secrets (smart host password, Microsoft 365 client secret, password of the .pfx).
/// Each value is encrypted on its own with <see cref="SecretProtector"/> and saved in a JSON file whose ACL
/// is restricted to SYSTEM and Administrators. The file is read again when it changes on disk, so a secret
/// set from the CLI or from the management console reaches the running service without a restart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SecretProtector _protector;
    private readonly object _sync = new();
    private Dictionary<string, string> _protectedValues = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedStamp = DateTime.MinValue;

    public SecretStore(string path, SecretProtector protector)
    {
        _path = path;
        _protector = protector;
        lock (_sync) { Reload(); }
    }

    /// <summary>Opens the store of an installation, creating the per-installation entropy when missing.</summary>
    public static SecretStore Open(RelayOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var entropy = MachineKey.LoadOrCreate(options.EntropyFilePath);
        return new SecretStore(options.SecretsFilePath, new SecretProtector(entropy));
    }

    /// <summary>Returns the secret in clear text, or null when it is missing or cannot be decrypted.</summary>
    public string? Get(string name)
    {
        lock (_sync)
        {
            ReloadIfChanged();
            if (!_protectedValues.TryGetValue(name, out var blob))
                return null;
            try { return _protector.Unprotect(blob); }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
        }
    }

    public bool Contains(string name)
    {
        lock (_sync)
        {
            ReloadIfChanged();
            return _protectedValues.ContainsKey(name);
        }
    }

    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_sync)
            {
                ReloadIfChanged();
                return _protectedValues.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>Encrypts and saves a secret (replacing an existing one with the same name).</summary>
    public void Set(string name, string plaintext)
    {
        if (!RelayOptionsValidator.IsValidName(name))
            throw new ArgumentException("A secret name can contain letters, digits, space, '.', '-' and '_' (max 80).", nameof(name));

        lock (_sync)
        {
            ReloadIfChanged();
            _protectedValues[name] = _protector.Protect(plaintext);
            Save();
        }
    }

    public bool Remove(string name)
    {
        lock (_sync)
        {
            ReloadIfChanged();
            if (!_protectedValues.Remove(name))
                return false;
            Save();
            return true;
        }
    }

    private void ReloadIfChanged()
    {
        DateTime stamp;
        try { stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue; }
        catch (IOException) { return; }

        if (stamp != _loadedStamp)
        {
            try { Reload(); }
            catch (IOException) { /* being written right now: keep the values in memory, retry on the next call */ }
        }
    }

    private void Reload()
    {
        if (!File.Exists(_path))
        {
            _protectedValues = new(StringComparer.OrdinalIgnoreCase);
            _loadedStamp = DateTime.MinValue;
            return;
        }

        var stamp = File.GetLastWriteTimeUtc(_path);
        var json = File.ReadAllText(_path);
        Dictionary<string, string>? data;
        try
        {
            data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException ex)
        {
            // Never treat a damaged file as empty: the next Set would overwrite every other secret.
            throw new InvalidDataException(
                $"The secret store '{_path}' is damaged and cannot be read. Restore it from a backup or delete it and enter the secrets again.", ex);
        }

        _protectedValues = data is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(data, StringComparer.OrdinalIgnoreCase);
        _loadedStamp = stamp;
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_protectedValues, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        try { WindowsAcl.RestrictToSystemAndAdmins(tmp); } catch (Exception) { /* best effort: the folder ACL still applies */ }
        File.Move(tmp, _path, overwrite: true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }
}
