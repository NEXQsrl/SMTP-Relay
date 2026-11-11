using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SmtpRelay.Security;

namespace SmtpRelay.Manager.Services;

/// <summary>A release published on GitHub.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string? InstallerUrl,
    string? InstallerName,
    long InstallerSize,
    string? InstallerSha256);

/// <summary>
/// Looks for a newer release on GitHub, downloads its setup, verifies it and starts it.
/// Nothing is sent to GitHub except the request itself: no identifier, no configuration, no telemetry.
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// Thumbprint of the NEXQ S.r.l. code signing certificate. A downloaded setup is run only when it is signed
    /// with this certificate, or with the certificate that signed the running console.
    /// </summary>
    public const string PublisherThumbprint = "989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8";

    private const long MaxInstallerSize = 400L * 1024 * 1024;
    private static readonly string DownloadPrefix =
        $"https://github.com/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepository}/releases/download/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NEXQ-SMTP-Relay", AppInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version CurrentVersion { get; } = ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0, 0);

    /// <summary>Returns the latest published release, or null when the repository has none.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepository}/releases/latest";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await Http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return ParseRelease(json);
    }

    internal static ReleaseInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var version = ParseVersion(tag);
        if (tag is null || version is null)
            return null;

        string? installerUrl = null, installerName = null, sha256 = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var download = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (name is null || download is null)
                    continue;
                if (!name.StartsWith("SmtpRelay-Setup", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Only a file hosted in the releases of this repository is ever downloaded.
                if (!download.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                installerUrl = download;
                installerName = Path.GetFileName(name);
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                var digest = asset.TryGetProperty("digest", out var g) ? g.GetString() : null;
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest["sha256:".Length..].ToUpperInvariant();
                break;
            }
        }

        return new ReleaseInfo(
            version,
            tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? AppInfo.ReleasesUrl : AppInfo.ReleasesUrl,
            installerUrl, installerName, size, sha256);
    }

    public static bool IsNewer(ReleaseInfo release) => release.Version > CurrentVersion;

    /// <summary>Parses "v2.1.0", "2.1" or "2.1.0-rc1" into a four-part version. Null when it is not a version.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().TrimStart('v', 'V');
        var cut = value.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0)
            value = value[..cut];

        if (!Version.TryParse(value.Contains('.') ? value : value + ".0", out var parsed))
            return null;

        // "2.0.0" and "2.0.0.0" must compare as equal.
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
    }

    /// <summary>
    /// Downloads the setup of a release into a folder only administrators can write to, then checks its size,
    /// its SHA-256 (when GitHub publishes it) and its signature. Throws when any check fails; the file is deleted.
    /// </summary>
    public async Task<string> DownloadAndVerifyAsync(ReleaseInfo release, string updatesDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (release.InstallerUrl is null || release.InstallerName is null)
            throw new InvalidOperationException("This release has no setup file to download.");
        if (release.InstallerSize > MaxInstallerSize)
            throw new InvalidOperationException("The setup file is larger than expected.");

        // A user without administrator rights must not be able to swap the file between the check and the launch.
        var folder = new DirectoryInfo(updatesDirectory);
        if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"'{updatesDirectory}' is a link to another folder: the update was not downloaded.");
        WindowsAcl.HardenDataDirectory(updatesDirectory);
        foreach (var old in Directory.EnumerateFiles(updatesDirectory, "SmtpRelay-Setup*.exe"))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var path = Path.Combine(updatesDirectory, release.InstallerName);
        try
        {
            using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > MaxInstallerSize)
                        throw new InvalidOperationException("The setup file is larger than expected.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total > 0)
                        progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            if (release.InstallerSha256 is not null)
            {
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(release.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The SHA-256 of the downloaded setup does not match the one published with the release.");
            }

            var signature = Authenticode.Verify(path);
            if (!signature.IsIntact || signature.SignerThumbprint is null)
                throw new InvalidOperationException("The downloaded setup is not signed, or its signature is not valid.");
            if (!TrustedPublishers().Contains(signature.SignerThumbprint, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The downloaded setup is signed by '{signature.SignerSubject}', which is not the publisher of this program.");

            return path;
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Thumbprints accepted for an update: the NEXQ certificate and the signer of the running console.</summary>
    internal static IReadOnlyCollection<string> TrustedPublishers()
    {
        var list = new List<string> { PublisherThumbprint };
        var self = Environment.ProcessPath;
        if (self is not null)
        {
            var own = Authenticode.Verify(self);
            if (own.IsIntact && own.SignerThumbprint is not null)
                list.Add(own.SignerThumbprint);
        }
        return list;
    }

    /// <summary>Starts the verified setup. It stops the service, replaces the files, starts the service and reopens the console.</summary>
    public static void LaunchInstaller(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = false };
        psi.ArgumentList.Add("/SILENT");
        psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
        psi.ArgumentList.Add("/NORESTART");
        psi.ArgumentList.Add("/RELAUNCH=1");
        Process.Start(psi)?.Dispose();
    }
}
