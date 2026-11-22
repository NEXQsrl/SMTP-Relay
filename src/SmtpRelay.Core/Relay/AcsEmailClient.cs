using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Relay;

/// <summary>
/// Sends a message through the Email API of an Azure Communication Services resource (HTTPS), signing each
/// request with the access key of the connection string (HMAC-SHA256). The connection string does not work
/// over SMTP: smtp.azurecomm.net only accepts a Microsoft Entra application.
/// <para>
/// The API takes the parts of a message, not the message itself, so the MIME message is taken apart: sender,
/// recipients of the SMTP envelope, subject, text and HTML body, attachments, Reply-To and the X- headers.
/// Azure builds a new message from them.
/// </para>
/// </summary>
internal sealed class AcsEmailClient
{
    internal const string ApiVersion = "2025-09-01";

    // One client for the process: the connections to the resource are reused. Redirects are never followed,
    // because the signature covers the host the request was meant for.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly UpstreamOptions _options;
    private readonly SecretStore _secrets;
    private readonly ILogger _logger;

    public AcsEmailClient(UpstreamOptions options, SecretStore secrets, ILogger logger)
    {
        _options = options;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Hands the message to Azure. Returns when Azure has accepted it (HTTP 202).</summary>
    public async Task SendAsync(MimeMessage message, MailboxAddress sender, IReadOnlyList<MailboxAddress> recipients, CancellationToken cancellationToken)
    {
        var connection = GetConnection();
        var body = BuildRequest(message, sender, recipients);

        using var response = await SendRequestAsync(connection, HttpMethod.Post, "/emails:send", body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

        // Azure sends the message in the background; what happens to it later (bounces, suppressed recipients)
        // is in the logs of the resource, under this operation.
        var operation = await ReadOperationIdAsync(response, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Azure Communication Services accepted the message (operation {OperationId}).",
            LogSanitizer.Clean(operation ?? "unknown"));
    }

    /// <summary>Checks that the resource answers and accepts the access key, without sending anything.</summary>
    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var connection = GetConnection();

        // The state of an operation that does not exist: a request that changes nothing and still has to be signed.
        using var response = await SendRequestAsync(
            connection, HttpMethod.Get, $"/emails/operations/{Guid.NewGuid():D}", Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);

        // "Not found" is the expected answer and proves the signature was accepted. Only a refused key, throttling
        // and server errors mean the resource cannot be used right now.
        int status = (int)response.StatusCode;
        if (status is 401 or 403 or 408 or 429 or >= 500)
            throw await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private AcsConnectionString GetConnection()
    {
        var name = _options.AzureCommunication.ConnectionStringSecret;
        var value = _secrets.Get(name)
            ?? throw new UpstreamDeliveryException(
                $"The connection string of Azure Communication Services '{name}' is not in the secret store. " +
                "Set it in the management console, or with: SmtpRelay set-credential --name " + name,
                isPermanent: true);

        if (!AcsConnectionString.TryParse(value, out var connection, out var error))
            throw new UpstreamDeliveryException($"The stored secret '{name}' cannot be used. {error}", isPermanent: true);
        return connection;
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        AcsConnectionString connection, HttpMethod method, string path, byte[] body, CancellationToken cancellationToken)
    {
        var uri = new Uri(connection.Endpoint.GetLeftPart(UriPartial.Authority) + path + "?api-version=" + ApiVersion);
        using var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        Sign(request, connection.AccessKey, body, DateTimeOffset.UtcNow);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            return await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Azure Communication Services ({uri.Host}) did not answer within {_options.TimeoutSeconds} seconds.");
        }
    }

    /// <summary>
    /// Adds the headers Azure checks: the date, the hash of the body and the signature of
    /// "VERB, path and query, date;host;hash" made with the access key.
    /// </summary>
    internal static void Sign(HttpRequestMessage request, byte[] accessKey, byte[] body, DateTimeOffset now)
    {
        var uri = request.RequestUri!;
        var date = now.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
        var contentHash = Convert.ToBase64String(SHA256.HashData(body));
        var stringToSign = $"{request.Method.Method}\n{uri.PathAndQuery}\n{date};{uri.Authority};{contentHash}";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(accessKey, Encoding.UTF8.GetBytes(stringToSign)));

        request.Headers.TryAddWithoutValidation("x-ms-date", date);
        request.Headers.TryAddWithoutValidation("x-ms-content-sha256", contentHash);
        request.Headers.TryAddWithoutValidation("Authorization",
            "HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=" + signature);
    }

    /// <summary>Turns the MIME message and its SMTP envelope into the JSON request of the Email API.</summary>
    internal static byte[] BuildRequest(MimeMessage message, MailboxAddress sender, IReadOnlyList<MailboxAddress> recipients)
    {
        // The first text and the first HTML part that are not attachments are the body; every other part,
        // inline pictures included, travels as an attachment.
        TextPart? text = null, html = null;
        var others = new List<MimeEntity>();
        foreach (var part in message.BodyParts)
        {
            if (part is TextPart { IsAttachment: false } candidate)
            {
                if (candidate.IsPlain && text is null) { text = candidate; continue; }
                if (candidate.IsHtml && html is null) { html = candidate; continue; }
            }
            others.Add(part);
        }
        var htmlBody = html?.Text;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("senderAddress", sender.Address);

            json.WriteStartObject("content");
            json.WriteString("subject", message.Subject ?? "");
            if (text is not null || htmlBody is null)
                json.WriteString("plainText", text?.Text ?? "");
            if (htmlBody is not null)
                json.WriteString("html", htmlBody);
            json.WriteEndObject();

            WriteRecipients(json, message, recipients);

            var replyTo = message.ReplyTo.Mailboxes.ToList();
            if (replyTo.Count > 0)
            {
                json.WriteStartArray("replyTo");
                foreach (var mailbox in replyTo)
                    WriteAddress(json, mailbox.Address, mailbox.Name);
                json.WriteEndArray();
            }

            if (others.Count > 0)
            {
                json.WriteStartArray("attachments");
                for (int i = 0; i < others.Count; i++)
                    WriteAttachment(json, others[i], i + 1, htmlBody);
                json.WriteEndArray();
            }

            WriteHeaders(json, message);
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // The SMTP envelope decides who receives the message, as it does over SMTP. The headers only say where each
    // recipient is shown: To, Cc, or nowhere (Bcc). An address in the headers that is not in the envelope is
    // somebody else's delivery and is left out.
    private static void WriteRecipients(Utf8JsonWriter json, MimeMessage message, IReadOnlyList<MailboxAddress> recipients)
    {
        var shownInTo = DisplayNames(message.To);
        var shownInCc = DisplayNames(message.Cc);
        var to = new List<(string Address, string? Name)>();
        var cc = new List<(string Address, string? Name)>();
        var bcc = new List<(string Address, string? Name)>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            if (!seen.Add(recipient.Address))
                continue;
            if (shownInTo.TryGetValue(recipient.Address, out var name))
                to.Add((recipient.Address, name));
            else if (shownInCc.TryGetValue(recipient.Address, out name))
                cc.Add((recipient.Address, name));
            else
                bcc.Add((recipient.Address, null));
        }

        json.WriteStartObject("recipients");
        WriteList("to", to, always: true);
        WriteList("cc", cc, always: false);
        WriteList("bcc", bcc, always: false);
        json.WriteEndObject();

        void WriteList(string name, List<(string Address, string? Name)> list, bool always)
        {
            if (list.Count == 0 && !always)
                return;
            json.WriteStartArray(name);
            foreach (var (address, displayName) in list)
                WriteAddress(json, address, displayName);
            json.WriteEndArray();
        }
    }

    private static Dictionary<string, string?> DisplayNames(InternetAddressList list)
    {
        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var mailbox in list.Mailboxes)
            names.TryAdd(mailbox.Address, mailbox.Name);
        return names;
    }

    private static void WriteAddress(Utf8JsonWriter json, string address, string? displayName)
    {
        json.WriteStartObject();
        json.WriteString("address", address);
        if (!string.IsNullOrWhiteSpace(displayName))
            json.WriteString("displayName", displayName);
        json.WriteEndObject();
    }

    private static void WriteAttachment(Utf8JsonWriter json, MimeEntity entity, int number, string? htmlBody)
    {
        string name, contentType;
        string? contentId = null;
        using var content = new MemoryStream();

        switch (entity)
        {
            case MimePart part:
                contentType = part.ContentType.MimeType;
                name = part.FileName ?? "";
                part.Content?.DecodeTo(content);
                // A picture shown inside the HTML body: it keeps its Content-ID so that "cid:" links still work.
                if (!string.IsNullOrEmpty(part.ContentId) &&
                    (!part.IsAttachment || htmlBody?.Contains("cid:" + part.ContentId, StringComparison.OrdinalIgnoreCase) == true))
                    contentId = part.ContentId;
                break;

            case MessagePart forwarded:
                contentType = "message/rfc822";
                name = forwarded.ContentDisposition?.FileName ?? "";
                forwarded.Message?.WriteTo(content);
                break;

            default:
                contentType = entity.ContentType.MimeType;
                name = entity.ContentDisposition?.FileName ?? "";
                entity.WriteTo(content, contentOnly: true);
                break;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var extension = contentType == "message/rfc822" ? ".eml"
                : MimeTypes.TryGetExtension(contentType, out var known) ? known : ".bin";
            name = $"attachment-{number}{extension}";
        }

        json.WriteStartObject();
        json.WriteString("name", name);
        json.WriteString("contentType", contentType);
        json.WriteBase64String("contentInBase64", content.GetBuffer().AsSpan(0, (int)content.Length));
        if (contentId is not null)
            json.WriteString("contentId", contentId);
        json.WriteEndObject();
    }

    // Azure writes the standard headers itself. The application's own X- headers (priority, tags of a ticketing
    // system, ...) are the ones worth carrying over.
    private static void WriteHeaders(Utf8JsonWriter json, MimeMessage message)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool open = false;
        foreach (var header in message.Headers)
        {
            if (!header.Field.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || !seen.Add(header.Field))
                continue;
            if (!open)
            {
                json.WriteStartObject("headers");
                open = true;
            }
            json.WriteString(header.Field, header.Value ?? "");
        }
        if (open)
            json.WriteEndObject();
    }

    private static async Task<string?> ReadOperationIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            return null; // the message was accepted: an answer we cannot read does not change that
        }
    }

    private static async Task<AcsRequestException> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string? code = response.Headers.TryGetValues("x-ms-error-code", out var codes) ? codes.FirstOrDefault() : null;
        string? detail = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code ??= c.GetString();
                if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    detail = m.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // an error page that is not JSON: the status code is all there is
        }

        var message = new StringBuilder("Azure Communication Services answered ").Append(status);
        if (!string.IsNullOrWhiteSpace(code))
            message.Append(' ').Append(code);
        if (!string.IsNullOrWhiteSpace(detail))
            message.Append(": ").Append(detail);
        else
            message.Append('.');

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            message.Append(" The access key was refused: store the connection string again (the key may have been regenerated) and check the clock of this computer.");

        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null
        };
        return new AcsRequestException(message.ToString(), status, retryAfter);
    }
}

/// <summary>An HTTP error answered by Azure Communication Services.</summary>
internal sealed class AcsRequestException : Exception
{
    public AcsRequestException(string message, int statusCode, TimeSpan? retryAfter) : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }

    /// <summary>How long Azure asks to wait before the next request, when it says so (throttling).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Throttling, time-outs and server errors pass; everything else is an answer about this message or this key.</summary>
    public bool IsTransient => StatusCode is 408 or 429 or >= 500;
}
