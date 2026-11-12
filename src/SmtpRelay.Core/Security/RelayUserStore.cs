using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmtpRelay.Configuration;

namespace SmtpRelay.Security;

/// <summary>A user that may sign in to the relay. The password is never kept, only its salted hash.</summary>
public sealed record RelayUser(string Name, bool Enabled, DateTime CreatedUtc, DateTime ChangedUtc);

/// <summary>
/// The users of the relay itself, for SMTP AUTH: names and salted PBKDF2 hashes in a JSON file of the data
/// folder. They are not Windows accounts. The file is read again when it changes on disk, so a user added
/// from the console or from the CLI can sign in to the running service without a restart.
/// </summary>
public sealed partial class RelayUserStore
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 256;

    private const int Iterations = 200_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Verified against when the user does not exist, so that the answer takes the same time either way.
    private static readonly byte[] DecoySalt = RandomNumberGenerator.GetBytes(SaltBytes);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@\-]{0,63}$")]
    private static partial Regex UserNameRegex();

    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedStamp = DateTime.MinValue;

    public RelayUserStore(string path)
    {
        _path = path;
        lock (_sync) { Reload(); }
    }

    public static RelayUserStore Open(RelayOptions options) => new(options.UsersFilePath);

    /// <summary>1-64 characters: letters, digits, '.', '_', '-' and '@'. Names are not case-sensitive.</summary>
    public static bool IsValidUserName(string? name) => UserNameRegex().IsMatch(name ?? "");

    /// <summary>What is wrong with a new password, or null when it can be used.</summary>
    public static string? DescribePasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"The password must be at least {MinPasswordLength} characters long.";
        if (password.Length > MaxPasswordLength)
            return $"The password cannot be longer than {MaxPasswordLength} characters.";
        if (password.Any(char.IsControl))
            return "The password cannot contain control characters.";
        return null;
    }

    public IReadOnlyList<RelayUser> Users
    {
        get
        {
            lock (_sync)
            {
                ReloadIfChanged();
                return _entries.Values
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new RelayUser(e.Name, e.Enabled, e.CreatedUtc, e.ChangedUtc))
                    .ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                ReloadIfChanged();
                return _entries.Count;
            }
        }
    }

    public bool Contains(string name)
    {
        lock (_sync)
        {
            ReloadIfChanged();
            return _entries.ContainsKey(name);
        }
    }

    /// <summary>Adds a user, or changes the password of an existing one (which keeps its enabled state).</summary>
    public void Set(string name, string password)
    {
        if (!IsValidUserName(name))
            throw new ArgumentException("A user name is 1-64 characters: letters, digits, '.', '_', '-' and '@', starting with a letter or a digit.", nameof(name));
        if (DescribePasswordProblem(password) is { } problem)
            throw new ArgumentException(problem, nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            ReloadIfChanged();
            _entries.TryGetValue(name, out var existing);
            _entries[name] = new Entry
            {
                Name = existing?.Name ?? name,
                Enabled = existing?.Enabled ?? true,
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(hash),
                Iterations = Iterations,
                CreatedUtc = existing?.CreatedUtc ?? now,
                ChangedUtc = now
            };
            Save();
        }
    }

    /// <summary>Lets a user sign in, or stops it from signing in without deleting it. Returns false when it does not exist.</summary>
    public bool SetEnabled(string name, bool enabled)
    {
        lock (_sync)
        {
            ReloadIfChanged();
            if (!_entries.TryGetValue(name, out var entry))
                return false;
            entry.Enabled = enabled;
            entry.ChangedUtc = DateTime.UtcNow;
            Save();
            return true;
        }
    }

    public bool Remove(string name)
    {
        lock (_sync)
        {
            ReloadIfChanged();
            if (!_entries.Remove(name))
                return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Checks a user name and a password. Returns the name as it was registered, or null when the user does
    /// not exist, is disabled or the password is wrong.
    /// </summary>
    public string? Verify(string? name, string? password)
    {
        Entry? entry = null;
        lock (_sync)
        {
            ReloadIfChanged();
            if (name is not null)
                _entries.TryGetValue(name, out entry);
        }

        // The hash is computed outside the lock: sessions that sign in at the same time do not wait for each other.
        password ??= "";
        if (password.Length > MaxPasswordLength)
            password = password[..MaxPasswordLength];

        if (entry is null || !entry.Enabled)
        {
            Derive(password, DecoySalt, Iterations);
            return null;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(entry.Salt);
            expected = Convert.FromBase64String(entry.Hash);
        }
        catch (FormatException)
        {
            return null;
        }

        var actual = Derive(password, salt, entry.Iterations > 0 ? entry.Iterations : Iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) ? entry.Name : null;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length <= 0 ? HashBytes : length);

    private void ReloadIfChanged()
    {
        DateTime stamp;
        try { stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue; }
        catch (IOException) { return; }

        if (stamp != _loadedStamp)
        {
            try { Reload(); }
            catch (IOException) { /* being written right now: keep the users in memory, retry on the next call */ }
        }
    }

    private void Reload()
    {
        if (!File.Exists(_path))
        {
            _entries = new(StringComparer.OrdinalIgnoreCase);
            _loadedStamp = DateTime.MinValue;
            return;
        }

        var stamp = File.GetLastWriteTimeUtc(_path);
        var json = File.ReadAllText(_path);
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(json);
        }
        catch (JsonException ex)
        {
            // Never treat a damaged file as empty: the next change would delete every other user.
            throw new InvalidDataException(
                $"The user file '{_path}' is damaged and cannot be read. Restore it from a backup, or delete it and create the users again.", ex);
        }

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in model?.Users ?? new List<Entry>())
        {
            if (IsValidUserName(entry.Name))
                entries[entry.Name] = entry;
        }
        _entries = entries;
        _loadedStamp = stamp;
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var model = new FileModel { Users = _entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList() };

        // The file takes the permissions of the data folder (SYSTEM, Administrators and the service account),
        // which the service account needs in order to read it.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<Entry> Users { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ChangedUtc { get; set; }
    }
}
