using System.Net;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.IO;
using SmtpServer.Net;

namespace SmtpRelay.Smtp;

/// <summary>
/// Factory che avvolge il listener di default per applicare l'allowlist IP a livello di socket:
/// gli IP non consentiti vengono chiusi immediatamente, prima di ricevere qualsiasi banner SMTP.
/// </summary>
public sealed class AllowListEndpointListenerFactory : IEndpointListenerFactory
{
    private readonly IEndpointListenerFactory _inner;
    private readonly Security.IpAllowList _allowList;
    private readonly ILogger _logger;

    public AllowListEndpointListenerFactory(IEndpointListenerFactory inner, Security.IpAllowList allowList, ILogger logger)
    {
        _inner = inner;
        _allowList = allowList;
        _logger = logger;
    }

    public IEndpointListener CreateListener(IEndpointDefinition endpointDefinition)
        => new AllowListEndpointListener(_inner.CreateListener(endpointDefinition), _allowList, _logger);
}

/// <summary>
/// Listener che accetta le connessioni tramite quello interno e scarta quelle provenienti da IP
/// non presenti nell'allowlist, continuando ad accettare le successive nello stesso ciclo di accept.
/// </summary>
public sealed class AllowListEndpointListener : IEndpointListener
{
    private readonly IEndpointListener _inner;
    private readonly Security.IpAllowList _allowList;
    private readonly ILogger _logger;

    public AllowListEndpointListener(IEndpointListener inner, Security.IpAllowList allowList, ILogger logger)
    {
        _inner = inner;
        _allowList = allowList;
        _logger = logger;
    }

    public async Task<ISecurableDuplexPipe> GetPipeAsync(ISessionContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pipe = await _inner.GetPipeAsync(context, cancellationToken).ConfigureAwait(false);
            var remote = GetRemoteEndPoint(context);

            if (remote is null || _allowList.IsAllowed(remote.Address))
            {
                if (remote is not null)
                    _logger.LogDebug("Connessione accettata da {RemoteIp}:{RemotePort}", remote.Address, remote.Port);
                return pipe;
            }

            _logger.LogWarning("Connessione RIFIUTATA: IP non consentito {RemoteIp}:{RemotePort}", remote.Address, remote.Port);
            try { pipe.Dispose(); } catch { /* ignora */ }

            // Rimuove la chiave per permettere al listener interno di reimpostarla alla prossima accept.
            context.Properties.Remove(EndpointListener.RemoteEndPointKey);
        }
    }

    private static IPEndPoint? GetRemoteEndPoint(ISessionContext context)
        => context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value)
            ? value as IPEndPoint
            : null;

    public void Dispose() => _inner.Dispose();
}
