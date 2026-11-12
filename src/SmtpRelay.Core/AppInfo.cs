using System.Reflection;

namespace SmtpRelay;

/// <summary>Product information shared by the service and the management console.</summary>
public static class AppInfo
{
    public const string ProductName = "NEXQ SMTP Relay";
    public const string Publisher = "NEXQ S.r.l.";
    public const string GitHubOwner = "NEXQsrl";
    public const string GitHubRepository = "SMTP-Relay";
    public const string RepositoryUrl = "https://github.com/" + GitHubOwner + "/" + GitHubRepository;
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string WebsiteUrl = "https://www.nexq.it";

    /// <summary>
    /// Private enterprise number under 1.3.6.1.4.1 that the SNMP objects are registered below.
    /// PLACEHOLDER: NEXQ S.r.l. has no number assigned by IANA yet. When it is assigned, change it here and
    /// in the MIB module (folder "mib"); a test checks that the two agree.
    /// </summary>
    public const uint SnmpEnterpriseNumber = 99999;

    /// <summary>Version of the product, for example "2.0.0".</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
