using System.Net;
using Microsoft.Extensions.Options;
using SmtpRelay.Security;

namespace SmtpRelay.Configuration;

/// <summary>Validazione della configurazione all'avvio (fail-fast).</summary>
public sealed class RelayOptionsValidator : IValidateOptions<RelayOptions>
{
    public ValidateOptionsResult Validate(string? name, RelayOptions o)
    {
        var errors = new List<string>();

        // Listener
        if (o.Listener.Port is < 1 or > 65535)
            errors.Add($"Listener.Port non valido: {o.Listener.Port}");
        if (!IPAddress.TryParse(o.Listener.IpAddress, out _))
            errors.Add($"Listener.IpAddress non valido: {o.Listener.IpAddress}");
        if (o.Listener.MaxMessageSizeBytes <= 0)
            errors.Add("Listener.MaxMessageSizeBytes deve essere > 0");
        if (o.Listener.MaxRecipients <= 0)
            errors.Add("Listener.MaxRecipients deve essere > 0");

        // Allowlist
        foreach (var entry in o.Security.AllowedIps)
        {
            if (!IpAllowList.TryParse(entry, out _))
                errors.Add($"Security.AllowedIps: voce non valida '{entry}'");
        }

        // TLS
        if (o.Tls.Mode != InboundTlsMode.None)
        {
            var hasFile = !string.IsNullOrWhiteSpace(o.Tls.CertificatePath);
            var hasThumb = !string.IsNullOrWhiteSpace(o.Tls.CertificateThumbprint);
            if (!hasFile && !hasThumb)
                errors.Add("Tls.Mode richiede un certificato: imposta CertificatePath oppure CertificateThumbprint");
        }
        if (!string.Equals(o.Tls.MinimumProtocol, "Tls12", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(o.Tls.MinimumProtocol, "Tls13", StringComparison.OrdinalIgnoreCase))
            errors.Add("Tls.MinimumProtocol deve essere 'Tls12' o 'Tls13'");

        // Upstream
        if (string.IsNullOrWhiteSpace(o.Upstream.Host))
            errors.Add("Upstream.Host obbligatorio");
        if (o.Upstream.Port is < 1 or > 65535)
            errors.Add($"Upstream.Port non valido: {o.Upstream.Port}");
        if (o.Upstream.MaxAttempts < 1)
            errors.Add("Upstream.MaxAttempts deve essere >= 1");

        if (o.Upstream.Mode == UpstreamAuthMode.Basic)
        {
            if (string.IsNullOrWhiteSpace(o.Upstream.Basic.Username))
                errors.Add("Upstream.Basic.Username obbligatorio in modalita Basic");
            if (string.IsNullOrWhiteSpace(o.Upstream.Basic.PasswordSecret))
                errors.Add("Upstream.Basic.PasswordSecret obbligatorio in modalita Basic");
        }
        else // Microsoft365
        {
            if (string.IsNullOrWhiteSpace(o.Upstream.Microsoft365.TenantId))
                errors.Add("Upstream.Microsoft365.TenantId obbligatorio");
            if (string.IsNullOrWhiteSpace(o.Upstream.Microsoft365.ClientId))
                errors.Add("Upstream.Microsoft365.ClientId obbligatorio");
            if (string.IsNullOrWhiteSpace(o.Upstream.Microsoft365.SenderUsername))
                errors.Add("Upstream.Microsoft365.SenderUsername obbligatorio (UPN della casella mittente)");
            if (string.IsNullOrWhiteSpace(o.Upstream.Microsoft365.ClientSecretName))
                errors.Add("Upstream.Microsoft365.ClientSecretName obbligatorio");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
