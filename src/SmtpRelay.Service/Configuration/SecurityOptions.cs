namespace SmtpRelay.Configuration;

/// <summary>Controllo di accesso in ingresso: allowlist di IP/CIDR.</summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Elenco di IP o reti CIDR autorizzati a contattare il relay.
    /// Esempi: "127.0.0.1", "::1", "192.168.1.0/24", "10.0.0.0/8", "2001:db8::/32".
    /// </summary>
    public List<string> AllowedIps { get; set; } = new();

    /// <summary>
    /// Se true e l'allowlist e vuota, tutte le connessioni vengono rifiutate (fail-closed).
    /// Se false e l'allowlist e vuota, tutte le connessioni sono accettate (sconsigliato).
    /// </summary>
    public bool RejectWhenAllowListEmpty { get; set; } = true;
}
