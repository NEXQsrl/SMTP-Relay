using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpServer;
using SmtpServer.Net;

namespace SmtpRelay.Smtp;

/// <summary>
/// Hosted service that runs the listening SMTP server: IP gate at connection level, optional TLS,
/// and forwarding of the messages to the smart host.
/// </summary>
public sealed class SmtpRelayHostedService : BackgroundService
{
    private readonly RelayOptions _options;
    private readonly IUpstreamSender _sender;
    private readonly SecretStore _secrets;
    private readonly IpAllowList _allowList;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SmtpRelayHostedService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SessionLimiter _limiter;
    private readonly DeliveryCounters _counters;

    private X509Certificate2? _certificate;

    public SmtpRelayHostedService(
        RelayOptions options,
        IUpstreamSender sender,
        SecretStore secrets,
        IpAllowList allowList,
        ILoggerFactory loggerFactory,
        ILogger<SmtpRelayHostedService> logger,
        IHostApplicationLifetime lifetime,
        DeliveryCounters? counters = null)
    {
        _counters = counters ?? new DeliveryCounters();
        _options = options;
        _sender = sender;
        _secrets = secrets;
        _allowList = allowList;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _lifetime = lifetime;
        _limiter = new SessionLimiter(options.Listener.MaxConcurrentSessions);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _certificate = CertificateLoader.Load(_options.Tls, _secrets, _logger);

            var server = new SmtpServer.SmtpServer(BuildServerOptions(), BuildComponents());
            server.SessionCreated += (_, e) => _logger.LogDebug("Session started {SessionId}.", e.Context.SessionId);
            server.SessionCompleted += (_, e) =>
            {
                _limiter.Exit(e.Context);
                _logger.LogDebug("Session completed {SessionId}.", e.Context.SessionId);
            };
            server.SessionCancelled += (_, e) => _limiter.Exit(e.Context);
            server.SessionFaulted += (_, e) =>
            {
                _limiter.Exit(e.Context);
                _logger.LogDebug(e.Exception, "Session faulted.");
            };

            _logger.LogInformation(
                "Listening on {Ip}:{Port} | upstream {Mode} {Host}:{UpstreamPort} ({Security}) | inbound TLS {Tls} | {AllowCount} allowlist entries",
                _options.Listener.IpAddress, _options.Listener.Port,
                _options.Upstream.Mode, _options.Upstream.Host, _options.Upstream.Port, _options.Upstream.Security,
                _options.Tls.Mode, _allowList.Count);

            await server.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied })
            {
                _logger.LogCritical(
                    "Cannot listen on {Ip}:{Port}: the port is in use by another program (for example the IIS SMTP service) or reserved. The relay is stopping.",
                    _options.Listener.IpAddress, _options.Listener.Port);
            }
            else
            {
                _logger.LogCritical(ex, "The SMTP listener failed. The relay is stopping.");
            }

            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
        finally
        {
            _certificate?.Dispose();
            _logger.LogInformation("SMTP Relay stopped.");
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
                    .AllowUnsecureAuthentication(false)
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
            auditLogger,
            _counters));
        serviceProvider.Add(new RelayMailboxFilter(_options.Listener, _options.Tls.Mode, componentLogger));
        serviceProvider.Add(new AllowListEndpointListenerFactory(new EndpointListenerFactory(), _allowList, _limiter, componentLogger));

        return serviceProvider;
    }

    private SslProtocols ResolveSslProtocols()
        => string.Equals(_options.Tls.MinimumProtocol, "Tls13", StringComparison.OrdinalIgnoreCase)
            ? SslProtocols.Tls13
            : SslProtocols.Tls12 | SslProtocols.Tls13;
}
