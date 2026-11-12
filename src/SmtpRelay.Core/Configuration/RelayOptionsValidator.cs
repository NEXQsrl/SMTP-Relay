using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using SmtpRelay.Security;

namespace SmtpRelay.Configuration;

/// <summary>Validates the configuration. The service refuses to start when any error is returned.</summary>
public static partial class RelayOptionsValidator
{
    private static readonly string[] LogLevels = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,253}$")]
    private static partial Regex TenantRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-. ]{1,80}$")]
    private static partial Regex NameRegex();

    /// <summary>True when the value can be used as a service name or as the name of a stored secret.</summary>
    public static bool IsValidName(string? value) => NameRegex().IsMatch(value ?? "");

    public static IReadOnlyList<string> Validate(RelayOptions o)
    {
        var errors = new List<string>();

        if (!IsValidName(o.ServiceName))
            errors.Add("ServiceName must be 1-80 characters: letters, digits, space, '.', '-' or '_'.");
        if (string.IsNullOrWhiteSpace(o.DataDirectory) || !Path.IsPathFullyQualified(o.DataDirectory))
            errors.Add("DataDirectory must be an absolute path.");
        else if (Path.GetPathRoot(o.DataDirectory)!.TrimEnd('\\', '/').Equals(o.DataDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            errors.Add("DataDirectory cannot be the root of a drive: its permissions are restricted to administrators.");

        // Listener
        if (o.Listener.Port is < 1 or > 65535)
            errors.Add($"Listener.Port is not valid: {o.Listener.Port}.");
        if (!IPAddress.TryParse(o.Listener.IpAddress, out _))
            errors.Add($"Listener.IpAddress is not a valid IP address: '{o.Listener.IpAddress}'.");
        if (string.IsNullOrWhiteSpace(o.Listener.ServerName) ||
            o.Listener.ServerName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            errors.Add("Listener.ServerName is required and cannot contain spaces.");
        if (o.Listener.MaxMessageSizeBytes is <= 0 or > int.MaxValue)
            errors.Add("Listener.MaxMessageSizeBytes must be between 1 and 2147483647.");
        if (o.Listener.MaxRecipients <= 0)
            errors.Add("Listener.MaxRecipients must be greater than 0.");
        if (o.Listener.CommandTimeoutSeconds is < 5 or > 3600)
            errors.Add("Listener.CommandTimeoutSeconds must be between 5 and 3600.");
        if (o.Listener.MaxConcurrentSessions is < 1 or > 10000)
            errors.Add("Listener.MaxConcurrentSessions must be between 1 and 10000.");

        // Allowlist
        foreach (var entry in o.Security.AllowedIps)
        {
            if (!IpAllowList.TryParse(entry, out _))
                errors.Add($"Security.AllowedIps: '{entry}' is not an IP address or a CIDR network.");
        }

        // Inbound TLS
        if (o.Tls.Mode != InboundTlsMode.None)
        {
            var hasFile = !string.IsNullOrWhiteSpace(o.Tls.CertificatePath);
            var hasThumb = !string.IsNullOrWhiteSpace(o.Tls.CertificateThumbprint);
            if (!hasFile && !hasThumb)
                errors.Add("Tls.Mode needs a certificate: set Tls.CertificateThumbprint or Tls.CertificatePath.");
        }
        if (!string.Equals(o.Tls.MinimumProtocol, "Tls12", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(o.Tls.MinimumProtocol, "Tls13", StringComparison.OrdinalIgnoreCase))
            errors.Add("Tls.MinimumProtocol must be 'Tls12' or 'Tls13'.");

        // Upstream
        var up = o.Upstream;
        if (string.IsNullOrWhiteSpace(up.Host) || Uri.CheckHostName(up.Host.Trim()) == UriHostNameType.Unknown)
            errors.Add("Upstream.Host is required and must be a host name or an IP address.");
        if (up.Port is < 1 or > 65535)
            errors.Add($"Upstream.Port is not valid: {up.Port}.");
        if (up.MaxAttempts is < 1 or > 10)
            errors.Add("Upstream.MaxAttempts must be between 1 and 10.");
        if (up.TimeoutSeconds is < 5 or > 600)
            errors.Add("Upstream.TimeoutSeconds must be between 5 and 600.");
        if (up.RetryBaseDelaySeconds is < 0 or > 300)
            errors.Add("Upstream.RetryBaseDelaySeconds must be between 0 and 300.");
        if (!string.IsNullOrWhiteSpace(up.OverrideFromAddress) && !IsMailbox(up.OverrideFromAddress))
            errors.Add("Upstream.OverrideFromAddress is not a valid e-mail address.");

        bool mayBeClearText = up.Security is UpstreamSecureMode.None or UpstreamSecureMode.StartTlsWhenAvailable;
        if (up.Mode != UpstreamAuthMode.Anonymous && mayBeClearText && !up.AllowInsecureAuthentication)
        {
            errors.Add($"Upstream.Security '{up.Security}' could send the credentials in clear text. " +
                       "Use StartTls, SslOnConnect or Auto, or set Upstream.AllowInsecureAuthentication to true.");
        }

        switch (up.Mode)
        {
            case UpstreamAuthMode.Basic:
                if (string.IsNullOrWhiteSpace(up.Basic.Username))
                    errors.Add("Upstream.Basic.Username is required in Basic mode.");
                if (!IsValidName(up.Basic.PasswordSecret))
                    errors.Add("Upstream.Basic.PasswordSecret is required in Basic mode (name of the stored secret).");
                break;

            case UpstreamAuthMode.Microsoft365:
                ValidateEntraApplication(up.Microsoft365, "Upstream.Microsoft365", errors);
                if (!IsMailbox(up.Microsoft365.SenderUsername))
                    errors.Add("Upstream.Microsoft365.SenderUsername is required (UPN of the sending mailbox).");
                break;

            case UpstreamAuthMode.AzureCommunicationServices:
                ValidateEntraApplication(up.AzureCommunication, "Upstream.AzureCommunication", errors);
                var smtpUser = up.AzureCommunication.SmtpUsername;
                if (string.IsNullOrWhiteSpace(smtpUser) || smtpUser.Any(char.IsControl))
                    errors.Add("Upstream.AzureCommunication.SmtpUsername is required (the SMTP Username created on the Communication Services resource).");
                break;
        }

        // Logging and spool
        if (!LogLevels.Contains(o.Logging.MinimumLevel, StringComparer.OrdinalIgnoreCase))
            errors.Add("Logging.MinimumLevel must be one of: " + string.Join(", ", LogLevels) + ".");
        if (o.Logging.RetainedFileCountLimit < 1)
            errors.Add("Logging.RetainedFileCountLimit must be at least 1.");
        if (o.Logging.RetainedFileTimeLimitDays is < 1)
            errors.Add("Logging.RetainedFileTimeLimitDays must be at least 1 (or null for no limit).");
        if (o.Logging.FileSizeLimitBytes < 1_048_576)
            errors.Add("Logging.FileSizeLimitBytes must be at least 1048576 (1 MB).");
        if (o.Delivery.FailedSpoolRetentionDays < 0)
            errors.Add("Delivery.FailedSpoolRetentionDays cannot be negative.");

        // SNMP agent
        if (o.Snmp.Enabled)
        {
            if (o.Snmp.Port is < 1 or > 65535)
                errors.Add($"Snmp.Port is not valid: {o.Snmp.Port}.");
            if (!IPAddress.TryParse(o.Snmp.IpAddress, out _))
                errors.Add($"Snmp.IpAddress is not a valid IP address: '{o.Snmp.IpAddress}'.");
            if (!IsValidName(o.Snmp.CommunitySecret))
                errors.Add("Snmp.CommunitySecret is required (name of the stored secret that holds the community).");
        }
        foreach (var entry in o.Snmp.AllowedManagers)
        {
            if (!IpAllowList.TryParse(entry, out _))
                errors.Add($"Snmp.AllowedManagers: '{entry}' is not an IP address or a CIDR network.");
        }

        return errors;
    }

    /// <summary>Settings that are accepted but deserve the administrator's attention.</summary>
    public static IReadOnlyList<string> Warnings(RelayOptions o)
    {
        var warnings = new List<string>();
        if (o.Security.AllowedIps.Count == 0)
        {
            warnings.Add(o.Security.RejectWhenAllowListEmpty
                ? "The IP allowlist is empty: every connection is refused."
                : "The IP allowlist is empty and RejectWhenAllowListEmpty is off: the relay accepts mail from ANY address (open relay).");
        }
        if (o.Security.AllowedIps.Any(e => e.Trim().EndsWith("/0", StringComparison.Ordinal)))
            warnings.Add("The IP allowlist contains a /0 network: the relay accepts mail from any address (open relay).");

        bool mayBeClearText = o.Upstream.Security is UpstreamSecureMode.None or UpstreamSecureMode.StartTlsWhenAvailable;
        if (o.Upstream.Mode != UpstreamAuthMode.Anonymous && mayBeClearText && o.Upstream.AllowInsecureAuthentication)
            warnings.Add("Upstream credentials may travel in clear text (AllowInsecureAuthentication is on).");
        if (o.Tls.Mode == InboundTlsMode.None)
            warnings.Add("Inbound TLS is off: clients talk to the relay in clear text.");
        if (o.Snmp.Enabled && o.Snmp.AllowedManagers.Count == 0)
            warnings.Add("The SNMP agent is on but no monitoring system is allowed to query it (Snmp.AllowedManagers is empty).");
        return warnings;
    }

    private static void ValidateEntraApplication(EntraApplicationOptions app, string section, List<string> errors)
    {
        if (!TenantRegex().IsMatch(app.TenantId ?? ""))
            errors.Add($"{section}.TenantId is required (tenant GUID or domain, for example contoso.onmicrosoft.com).");
        if (!Guid.TryParse(app.ClientId, out _))
            errors.Add($"{section}.ClientId is required (application ID, a GUID).");
        if (app.Credential == EntraCredentialType.Certificate)
        {
            if (!CertificateFinder.IsThumbprint(app.CertificateThumbprint))
                errors.Add($"{section}.CertificateThumbprint is required when Credential is Certificate (40 hexadecimal digits, certificate in the LocalMachine\\My store).");
        }
        else if (!IsValidName(app.ClientSecretName))
        {
            errors.Add($"{section}.ClientSecretName is required (name of the stored secret).");
        }
        if (!Uri.TryCreate(app.Scope, UriKind.Absolute, out var scope) || scope.Scheme != Uri.UriSchemeHttps)
            errors.Add($"{section}.Scope must be an https URL.");
    }

    private static bool IsMailbox(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains('@') && MailboxAddress.TryParse(value, out _);
}
