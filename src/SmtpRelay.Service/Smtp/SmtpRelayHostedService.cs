using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpServer;
using SmtpServer.Net;

namespace SmtpRelay.Smtp;

/// <summary>
/// Servizio ospitato che avvia il server SMTP in ascolto, applica il gate IP a livello di connessione,
/// il TLS opzionale e inoltra i messaggi all'upstream.
/// </summary>
public sealed class SmtpRelayHostedService : BackgroundService
{
    private readonly RelayOptions _options;
    private readonly IUpstreamSender _sender;
    private readonly SecretStore _secrets;
    private readonly IpAllowList _allowList;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SmtpRelayHostedService> _logger;

    private X509Certificate2? _certificate;

    public SmtpRelayHostedService(
        RelayOptions options,
        IUpstreamSender sender,
        SecretStore secrets,
        IpAllowList allowList,
        ILoggerFactory loggerFactory,
        ILogger<SmtpRelayHostedService> logger)
    {
        _options = options;
        _sender = sender;
        _secrets = secrets;
        _allowList = allowList;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _certificate = CertificateLoader.Load(_options.Tls, _secrets, _logger);

        var serverOptions = BuildServerOptions();
        var serviceProvider = BuildComponents();

        var server = new SmtpServer.SmtpServer(serverOptions, serviceProvider);
        server.SessionCreated += (_, e) => _logger.LogDebug("Sessione avviata {SessionId}.", e.Context.SessionId);
        server.SessionCompleted += (_, e) => _logger.LogDebug("Sessione completata {SessionId}.", e.Context.SessionId);
        server.SessionFaulted += (_, e) => _logger.LogDebug(e.Exception, "Sessione in errore.");

        _logger.LogInformation(
            "SMTP Relay in ascolto su {Ip}:{Port} | Upstream={Mode} {Host}:{UpstreamPort} | TLS ingresso={Tls} | IP consentiti={AllowCount}",
            _options.Listener.IpAddress, _options.Listener.Port,
            _options.Upstream.Mode, _options.Upstream.Host, _options.Upstream.Port,
            _options.Tls.Mode, _allowList.Count);

        try
        {
            await server.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arresto normale.
        }
        finally
        {
            _certificate?.Dispose();
            _logger.LogInformation("SMTP Relay arrestato.");
        }
    }

    private ISmtpServerOptions BuildServerOptions()
    {
        var builder = new SmtpServerOptionsBuilder()
            .ServerName(_options.Listener.ServerName)
            .CommandWaitTimeout(TimeSpan.FromSeconds(_options.Listener.CommandTimeoutSeconds))
            .MaxMessageSize((int)Math.Min(_options.Listener.MaxMessageSizeBytes, int.MaxValue))
            .Endpoint(endpoint =>
            {
                endpoint
                    .Endpoint(new IPEndPoint(IPAddress.Parse(_options.Listener.IpAddress), _options.Listener.Port))
                    .IsSecure(false)
                    .AllowUnsecureAuthentication(true)
                    .SupportedSslProtocols(ResolveSslProtocols());

                if (_certificate is not null)
                    endpoint.Certificate(_certificate);
            });

        return builder.Build();
    }

    private IServiceProvider BuildComponents()
    {
        var componentLogger = _loggerFactory.CreateLogger("SmtpRelay.Smtp");
        var auditLogger = _loggerFactory.CreateLogger(LoggingSetup.AuditSourceContext);

        var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
        serviceProvider.Add(new RelayMessageStore(
            _sender,
            _options.Delivery,
            _options.FailedSpoolDirectory,
            componentLogger,
            auditLogger));
        serviceProvider.Add(new RelayMailboxFilter(_options.Listener, _options.Tls.Mode, componentLogger));
        serviceProvider.Add(new AllowListEndpointListenerFactory(new EndpointListenerFactory(), _allowList, componentLogger));

        return serviceProvider;
    }

    private SslProtocols ResolveSslProtocols()
        => string.Equals(_options.Tls.MinimumProtocol, "Tls13", StringComparison.OrdinalIgnoreCase)
            ? SslProtocols.Tls13
            : SslProtocols.Tls12 | SslProtocols.Tls13;
}
