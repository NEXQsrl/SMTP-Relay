using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmtpRelay.Security;

namespace SmtpRelay.Configuration;

/// <summary>What a backup file holds, as told to the administrator.</summary>
public sealed record BackupSummary(DateTime CreatedUtc, string ProductVersion, string Computer, int Secrets, int Users, bool UsesCertificates);

/// <summary>
/// Backup of the settings in one file, to move the relay to another server: appsettings.json, the stored
/// secrets and the users of the relay. Secrets on disk are bound to the computer (DPAPI), so they are put in
/// the backup in clear text and the whole content is encrypted with a password (AES-256-GCM, key derived with
/// PBKDF2). Certificates, logs and undelivered messages are not part of it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SettingsBackup
{
    public const string FileExtension = ".srbackup";
    public const int MinPasswordLength = 8;

    private const string FormatName = "NEXQ-SMTP-Relay-Backup";
    private const int Iterations = 600_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string? DescribePasswordProblem(string? password)
        => string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"The password must be at least {MinPasswordLength} characters long."
            : null;

    /// <summary>Writes the backup of the installation whose appsettings.json is in <paramref name="configDirectory"/>.</summary>
    public static BackupSummary Create(string path, string configDirectory, string password)
    {
        if (DescribePasswordProblem(password) is { } problem)
            throw new ArgumentException(problem, nameof(password));

        var options = ConfigLoader.Load(configDirectory);
        var settingsPath = Path.Combine(configDirectory, ConfigLoader.FileName);
        var secrets = SecretStore.Open(options);

        var payload = new Payload
        {
            Settings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null,
            Users = File.Exists(options.UsersFilePath) ? File.ReadAllText(options.UsersFilePath) : null
        };
        foreach (var name in secrets.Names)
        {
            // A secret that cannot be decrypted (file copied from another computer) is left out, not saved empty.
            if (secrets.Get(name) is { } value)
                payload.Secrets[name] = value;
        }

        var summary = new BackupSummary(
            DateTime.UtcNow, AppInfo.Version, Environment.MachineName,
            payload.Secrets.Count, CountUsers(payload.Users), UsesCertificates(options));

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        var key = DeriveKey(password, salt, Iterations);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(FormatName));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }

        var envelope = new Envelope
        {
            Format = FormatName,
            Version = 1,
            CreatedUtc = summary.CreatedUtc,
            ProductVersion = summary.ProductVersion,
            Computer = summary.Computer,
            Secrets = summary.Secrets,
            Users = summary.Users,
            UsesCertificates = summary.UsesCertificates,
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher)
        };

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
        File.Move(tmp, path, overwrite: true);
        return summary;
    }

    /// <summary>Reads what a backup holds without its password (the description is not encrypted).</summary>
    public static BackupSummary Describe(string path)
    {
        var envelope = ReadEnvelope(path);
        return new BackupSummary(envelope.CreatedUtc, envelope.ProductVersion, envelope.Computer, envelope.Secrets, envelope.Users, envelope.UsesCertificates);
    }

    /// <summary>
    /// Replaces the settings, the secrets and the users of this installation with the ones of the backup.
    /// Nothing is written when the password is wrong or the file is damaged.
    /// </summary>
    public static BackupSummary Restore(string path, string configDirectory, string password)
    {
        var envelope = ReadEnvelope(path);

        byte[] plain;
        var key = DeriveKey(password ?? "", Convert.FromBase64String(envelope.Salt), envelope.Iterations);
        try
        {
            var cipher = Convert.FromBase64String(envelope.Data);
            plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), cipher, Convert.FromBase64String(envelope.Tag), plain, Encoding.UTF8.GetBytes(FormatName));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("The password is wrong, or the backup file is damaged.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        Payload payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(plain) ?? throw new InvalidDataException("The backup file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The content of the backup file cannot be read.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // Settings first: they say where the data folder of the restored installation is.
        if (payload.Settings is not null)
        {
            Directory.CreateDirectory(configDirectory);
            var settingsPath = Path.Combine(configDirectory, ConfigLoader.FileName);
            var tmp = settingsPath + ".tmp";
            File.WriteAllText(tmp, payload.Settings);
            File.Move(tmp, settingsPath, overwrite: true);
        }

        var options = ConfigLoader.Load(configDirectory);
        Directory.CreateDirectory(options.DataDirectory);

        var secrets = SecretStore.Open(options);
        foreach (var name in secrets.Names.Where(n => !payload.Secrets.ContainsKey(n)).ToList())
            secrets.Remove(name);
        foreach (var (name, value) in payload.Secrets)
            secrets.Set(name, value);

        if (payload.Users is not null)
        {
            var tmp = options.UsersFilePath + ".tmp";
            File.WriteAllText(tmp, payload.Users);
            File.Move(tmp, options.UsersFilePath, overwrite: true);
        }
        else if (File.Exists(options.UsersFilePath))
        {
            File.Delete(options.UsersFilePath);
        }

        return new BackupSummary(envelope.CreatedUtc, envelope.ProductVersion, envelope.Computer,
            payload.Secrets.Count, CountUsers(payload.Users), UsesCertificates(options));
    }

    /// <summary>True when the settings point to certificates of the computer store, which a backup cannot carry.</summary>
    public static bool UsesCertificates(RelayOptions options)
        => (options.Tls.Mode != InboundTlsMode.None &&
            (!string.IsNullOrWhiteSpace(options.Tls.CertificateThumbprint) || !string.IsNullOrWhiteSpace(options.Tls.CertificatePath))) ||
           options.Upstream.EntraApplication is { Credential: EntraCredentialType.Certificate };

    private static Envelope ReadEnvelope(string path)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This file is not a backup of SMTP Relay.", ex);
        }

        if (envelope is null || envelope.Format != FormatName)
            throw new InvalidDataException("This file is not a backup of SMTP Relay.");
        if (envelope.Version != 1)
            throw new InvalidDataException("This backup was made by a newer version of SMTP Relay: update this installation first.");
        if (envelope.Iterations is < 1 or > 10_000_000)
            throw new InvalidDataException("The backup file is damaged.");
        return envelope;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static int CountUsers(string? usersJson)
    {
        if (string.IsNullOrWhiteSpace(usersJson))
            return 0;
        try
        {
            using var document = JsonDocument.Parse(usersJson);
            return document.RootElement.TryGetProperty("Users", out var users) && users.ValueKind == JsonValueKind.Array ? users.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private sealed class Payload
    {
        public string? Settings { get; set; }
        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Users { get; set; }
    }

    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string ProductVersion { get; set; } = "";
        public string Computer { get; set; } = "";
        public int Secrets { get; set; }
        public int Users { get; set; }
        public bool UsesCertificates { get; set; }
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }
}
