using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

/// <summary>
/// Azure Communication Services with the connection string of the resource: the real sender against a fake
/// Email API on the loopback interface, which checks the signature of every request the way Azure documents it.
/// </summary>
public sealed class AzureCommunicationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smtprelay-acs-").FullName;
    private readonly FakeAcsResource _acs = new();

    public void Dispose()
    {
        _acs.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private RelayOptions Options(string? connectionString = null)
    {
        var o = new RelayOptions
        {
            DataDirectory = _dir,
            Security = { AllowedIps = { "10.0.0.0/8" } },
            Upstream =
            {
                Mode = UpstreamAuthMode.AzureCommunicationServices,
                MaxAttempts = 1,
                RetryBaseDelaySeconds = 0,
                TimeoutSeconds = 10
            }
        };
        Assert.Empty(RelayOptionsValidator.Validate(o));
        if (connectionString is not null)
            SecretStore.Open(o).Set(o.Upstream.AzureCommunication.ConnectionStringSecret, connectionString);
        return o;
    }

    private static UpstreamSender Sender(RelayOptions o)
    {
        var secrets = SecretStore.Open(o);
        return new UpstreamSender(
            o.Upstream, secrets,
            new EntraTokenProvider(o.Upstream, secrets, NullLogger<EntraTokenProvider>.Instance),
            NullLogger<UpstreamSender>.Instance);
    }

    private static MimeMessage Scan()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Scanner", "scanner@contoso.com"));
        message.To.Add(new MailboxAddress("Anna Rossi", "anna@example.com"));
        message.Cc.Add(MailboxAddress.Parse("bob@example.com"));
        message.ReplyTo.Add(MailboxAddress.Parse("helpdesk@contoso.com"));
        message.Subject = "Scan 42 – città";
        message.Headers.Add("X-Priority", "1");

        var body = new BodyBuilder
        {
            TextBody = "The scan is attached.",
            HtmlBody = "<p>The scan is attached.</p><img src=\"cid:logo1\">"
        };
        var logo = body.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3, 4 }, new ContentType("image", "png"));
        logo.ContentId = "logo1";
        body.Attachments.Add("report.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 test"), new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();
        return message;
    }

    // ----- connection string and settings ----------------------------------------------------------------

    [Fact]
    public void TheConnectionStringOfThePortalIsUnderstood()
    {
        // Pasted with a line break, and with the names in the capitals some tools use.
        Assert.True(AcsConnectionString.TryParse(
            "Endpoint=https://contoso-relay.europe.communication.azure.com/;AccessKey=QUJDREVGR0g=\r\n", out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("contoso-relay.europe.communication.azure.com", parsed!.Endpoint.Host);
        Assert.Equal("ABCDEFGH", Encoding.ASCII.GetString(parsed.AccessKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("QUJDREVGR0g=")] // the key alone
    [InlineData("endpoint=https://contoso.communication.azure.com/")]
    [InlineData("endpoint=contoso.communication.azure.com;accesskey=QUJDREVGR0g=")]
    [InlineData("endpoint=http://contoso.communication.azure.com/;accesskey=QUJDREVGR0g=")] // clear text
    [InlineData("endpoint=https://contoso.communication.azure.com/;accesskey=not-base64-at-all!")]
    public void AnythingElseIsRefusedWithoutRepeatingIt(string? value)
    {
        Assert.False(AcsConnectionString.TryParse(value, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("QUJDREVGR0g", error);
        Assert.DoesNotContain("not-base64", error);
    }

    [Fact]
    public void NoSmtpServerIsAskedFor()
    {
        var o = Options();
        // Whatever the SMTP settings hold, they are not this mode's business.
        o.Upstream.Host = "";
        o.Upstream.Port = 0;
        o.Upstream.Security = UpstreamSecureMode.None;
        Assert.Empty(RelayOptionsValidator.Validate(o));
        Assert.DoesNotContain(RelayOptionsValidator.Warnings(o), w => w.Contains("clear text") && w.Contains("Upstream"));
        Assert.False(o.Upstream.UsesSmtp);
        Assert.Null(o.Upstream.EntraApplication);
        Assert.Equal("AcsConnectionString", o.Upstream.AzureCommunication.ConnectionStringSecret);

        o.Upstream.AzureCommunication.ConnectionStringSecret = "";
        Assert.Contains(RelayOptionsValidator.Validate(o), e => e.Contains("AzureCommunication.ConnectionStringSecret"));
    }

    [Fact]
    public void ASettingsFileOfTheSmtpSignInStillLoads()
    {
        // Up to version 2.4 this mode signed in over SMTP with a Microsoft Entra application.
        File.WriteAllText(Path.Combine(_dir, ConfigLoader.FileName), """
            { "SmtpRelay": { "Upstream": { "Mode": "AzureCommunicationServices", "Host": "smtp.azurecomm.net", "Port": 587,
                "AzureCommunication": { "TenantId": "contoso.onmicrosoft.com", "ClientId": "11111111-2222-3333-4444-555555555555",
                "Credential": "ClientSecret", "ClientSecretName": "AcsClientSecret", "CertificateThumbprint": "",
                "SmtpUsername": "relay-smtp-user", "Scope": "https://communication.azure.com/.default" } } } }
            """);
        var old = ConfigLoader.Load(_dir);
        Assert.Empty(RelayOptionsValidator.Validate(old));
        Assert.Equal(UpstreamAuthMode.AzureCommunicationServices, old.Upstream.Mode);
        Assert.Equal("AcsConnectionString", old.Upstream.AzureCommunication.ConnectionStringSecret);

        // Saved again, the file no longer carries the settings nobody reads.
        ConfigLoader.Save(_dir, old);
        var text = File.ReadAllText(Path.Combine(_dir, ConfigLoader.FileName));
        Assert.Contains("ConnectionStringSecret", text);
        Assert.DoesNotContain("SmtpUsername", text);
        Assert.DoesNotContain("UsesSmtp", text);
    }

    // ----- sending ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMessageIsHandedToTheEmailApiSignedWithTheKey()
    {
        var o = Options(_acs.ConnectionString);
        using var message = Scan();

        // The envelope decides who gets the message: one recipient is in no header (Bcc).
        await Sender(o).SendAsync(message, "bounces@contoso.com",
            new[] { "anna@example.com", "bob@example.com", "hidden@example.com" }, CancellationToken.None);

        var request = Assert.Single(_acs.Requests);
        Assert.True(request.SignatureValid);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/emails:send?api-version=2025-09-01", request.PathAndQuery);
        Assert.StartsWith("application/json", request.ContentType);

        var json = request.Json.RootElement;
        // The author of the message, not the return-path: it is the From the recipient reads.
        Assert.Equal("scanner@contoso.com", json.GetProperty("senderAddress").GetString());
        var content = json.GetProperty("content");
        Assert.Equal("Scan 42 – città", content.GetProperty("subject").GetString());
        Assert.Equal("The scan is attached.", content.GetProperty("plainText").GetString()!.Trim());
        Assert.Contains("cid:logo1", content.GetProperty("html").GetString());

        var recipients = json.GetProperty("recipients");
        var to = Assert.Single(recipients.GetProperty("to").EnumerateArray());
        Assert.Equal("anna@example.com", to.GetProperty("address").GetString());
        Assert.Equal("Anna Rossi", to.GetProperty("displayName").GetString());
        Assert.Equal("bob@example.com", Assert.Single(recipients.GetProperty("cc").EnumerateArray()).GetProperty("address").GetString());
        Assert.Equal("hidden@example.com", Assert.Single(recipients.GetProperty("bcc").EnumerateArray()).GetProperty("address").GetString());

        Assert.Equal("helpdesk@contoso.com", Assert.Single(json.GetProperty("replyTo").EnumerateArray()).GetProperty("address").GetString());
        Assert.Equal("1", json.GetProperty("headers").GetProperty("X-Priority").GetString());

        var attachments = json.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(2, attachments.Count);
        var logo = Assert.Single(attachments, a => a.GetProperty("name").GetString() == "logo.png");
        Assert.Equal("image/png", logo.GetProperty("contentType").GetString());
        Assert.Equal("logo1", logo.GetProperty("contentId").GetString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, logo.GetProperty("contentInBase64").GetBytesFromBase64());
        var report = Assert.Single(attachments, a => a.GetProperty("name").GetString() == "report.pdf");
        Assert.Equal("application/pdf", report.GetProperty("contentType").GetString());
        Assert.False(report.TryGetProperty("contentId", out _));
        Assert.Equal("%PDF-1.7 test", Encoding.ASCII.GetString(report.GetProperty("contentInBase64").GetBytesFromBase64()));
    }

    [Fact]
    public async Task APlainMessageWithRecipientsOnlyInTheEnvelopeIsSent()
    {
        var o = Options(_acs.ConnectionString);
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("nas@contoso.com"));
        message.Subject = "Backup report";
        message.Body = new TextPart("plain") { Text = "All good." };

        // Same address twice, in two spellings: Azure gets it once.
        await Sender(o).SendAsync(message, "nas@contoso.com", new[] { "it@example.com", "IT@example.com" }, CancellationToken.None);

        var json = Assert.Single(_acs.Requests).Json.RootElement;
        var recipients = json.GetProperty("recipients");
        Assert.Empty(recipients.GetProperty("to").EnumerateArray());
        Assert.Equal("it@example.com", Assert.Single(recipients.GetProperty("bcc").EnumerateArray()).GetProperty("address").GetString());
        Assert.False(json.GetProperty("content").TryGetProperty("html", out _));
        Assert.False(json.TryGetProperty("attachments", out _));
        Assert.False(json.TryGetProperty("headers", out _));
    }

    [Fact]
    public async Task AlwaysSendAsReplacesTheSender()
    {
        var o = Options(_acs.ConnectionString);
        o.Upstream.OverrideFromAddress = "DoNotReply@contoso.com";
        using var message = Scan();

        await Sender(o).SendAsync(message, "scanner@contoso.com", new[] { "anna@example.com" }, CancellationToken.None);

        Assert.Equal("DoNotReply@contoso.com", Assert.Single(_acs.Requests).Json.RootElement.GetProperty("senderAddress").GetString());
    }

    [Fact]
    public async Task ARefusedKeyIsAPermanentErrorAndIsNotRetried()
    {
        // A key that was regenerated in the portal: the stored one no longer signs.
        var o = Options($"endpoint=http://127.0.0.1:{_acs.Port}/;accesskey={Convert.ToBase64String(new byte[32])}");
        o.Upstream.MaxAttempts = 3;
        using var message = Scan();

        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() =>
            Sender(o).SendAsync(message, "scanner@contoso.com", new[] { "anna@example.com" }, CancellationToken.None));

        Assert.True(ex.IsPermanent);
        Assert.Contains("401", ex.Message);
        Assert.Contains("connection string", ex.Message);
        Assert.False(Assert.Single(_acs.Requests).SignatureValid);
    }

    [Fact]
    public async Task ARefusedMessageIsAPermanentErrorWithTheReasonAzureGave()
    {
        var o = Options(_acs.ConnectionString);
        o.Upstream.MaxAttempts = 3;
        _acs.Answer(400, """{"error":{"code":"InvalidSenderUserName","message":"Invalid email sender username: 'scanner'."}}""");
        using var message = Scan();

        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() =>
            Sender(o).SendAsync(message, "scanner@contoso.com", new[] { "anna@example.com" }, CancellationToken.None));

        Assert.True(ex.IsPermanent);
        Assert.Contains("InvalidSenderUserName", ex.Message);
        Assert.Contains("Invalid email sender username", ex.Message);
        Assert.Single(_acs.Requests);
    }

    [Fact]
    public async Task ThrottlingIsRetriedAfterTheTimeAzureAsksFor()
    {
        var o = Options(_acs.ConnectionString);
        o.Upstream.MaxAttempts = 2;
        _acs.Answer(429, """{"error":{"code":"TooManyRequests","message":"Slow down."}}""", retryAfterSeconds: 1);
        using var message = Scan();

        var started = DateTime.UtcNow;
        await Sender(o).SendAsync(message, "scanner@contoso.com", new[] { "anna@example.com" }, CancellationToken.None);

        Assert.Equal(2, _acs.Requests.Count);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(900), "The second attempt did not wait.");
    }

    [Fact]
    public async Task AServerErrorIsTransientSoThatTheClientRetries()
    {
        var o = Options(_acs.ConnectionString);
        _acs.Answer(503, "Service Unavailable"); // not JSON: the status code is all there is
        using var message = Scan();

        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() =>
            Sender(o).SendAsync(message, "scanner@contoso.com", new[] { "anna@example.com" }, CancellationToken.None));

        Assert.False(ex.IsPermanent);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task WithoutAConnectionStringNothingIsSentAndTheErrorNamesTheSecret()
    {
        var o = Options();
        using var message = Scan();

        var ex = await Assert.ThrowsAsync<UpstreamDeliveryException>(() =>
            Sender(o).SendAsync(message, "scanner@contoso.com", new[] { "anna@example.com" }, CancellationToken.None));

        Assert.True(ex.IsPermanent);
        Assert.Contains("AcsConnectionString", ex.Message);
        Assert.Empty(_acs.Requests);
    }

    [Fact]
    public async Task TheConnectionTestProvesTheKeyWithoutSendingAnything()
    {
        var good = Options(_acs.ConnectionString);
        await Sender(good).VerifyConnectionAsync(CancellationToken.None);

        // First "is the resource there?", with no signature; then a signed send request with nothing to send.
        // Never the state of an operation: the real service keeps that request waiting (the fake does too).
        var requests = _acs.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("GET", requests[0].Method);
        Assert.StartsWith("/?", requests[0].PathAndQuery);
        Assert.False(requests[0].SignatureValid);
        Assert.Equal("POST", requests[1].Method);
        Assert.Equal("/emails:send?api-version=2025-09-01", requests[1].PathAndQuery);
        Assert.True(requests[1].SignatureValid);
        Assert.Equal("{}", Encoding.UTF8.GetString(requests[1].Body));
        // Logs and messages name the resource, which is not a secret.
        Assert.Equal("127.0.0.1", UpstreamSender.DescribeTarget(good.Upstream, SecretStore.Open(good)));

        SecretStore.Open(good).Set("AcsConnectionString", $"endpoint=http://127.0.0.1:{_acs.Port}/;accesskey={Convert.ToBase64String(new byte[32])}");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Sender(good).VerifyConnectionAsync(CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.Contains("connection string", ex.Message);
    }

    [Fact]
    public async Task AResourceThatTakesTheKeyButKeepsTheTestWaitingStillPasses()
    {
        // What 2.5.0 got wrong: Azure delivered the messages, and the test said it did not answer.
        var o = Options(_acs.ConnectionString);
        _acs.KeepSignedRequestsWaiting = true;
        var secrets = SecretStore.Open(o);

        var client = new AcsEmailClient(o.Upstream, secrets, NullLogger.Instance) { KeyCheckWait = TimeSpan.FromSeconds(1) };
        await client.VerifyAsync(CancellationToken.None);

        // A key that does not sign is refused at once, waiting or not.
        secrets.Set("AcsConnectionString", $"endpoint=http://127.0.0.1:{_acs.Port}/;accesskey={Convert.ToBase64String(new byte[32])}");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.VerifyAsync(CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task AResourceThatCannotBeReachedFailsTheTestAndSaysWhere()
    {
        // A port nobody listens on: what a firewall that closes HTTPS looks like.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var o = Options($"endpoint=http://127.0.0.1:{port}/;accesskey={Convert.ToBase64String(new byte[32])}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Sender(o).VerifyConnectionAsync(CancellationToken.None));

        Assert.Contains($"Cannot reach 127.0.0.1 on port {port}", ex.Message);
    }
}

/// <summary>
/// Just enough of the Email API of a Communication Services resource: it recomputes the HMAC signature of
/// every request from the documented recipe and answers 401 when it does not match.
/// <para>
/// It also behaves as a real resource was seen to (November 2026): a request that is not signed, or signed with
/// another key, gets 401 at once whatever it asks; the root answers 404; the state of an operation that does not
/// exist, asked with the right key, is kept waiting. That an empty send request with the right key gets 400 is
/// what the API documents for a missing required field; it could not be tried here without a real key.
/// </para>
/// </summary>
internal sealed class FakeAcsResource : IDisposable
{
    private readonly HttpListener _listener;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentQueue<(int Status, string Body, int? RetryAfter)> _answers = new();
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly CancellationTokenSource _stopping = new();

    public FakeAcsResource()
    {
        // The port is chosen by trying: HttpListener cannot ask the system for a free one.
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try
            {
                listener.Start();
                _listener = listener;
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }
    public string ConnectionString => $"endpoint=http://127.0.0.1:{Port}/;accesskey={Convert.ToBase64String(_key)}";
    public IReadOnlyCollection<Request> Requests => _requests;

    /// <summary>The next correctly signed request gets this answer instead of the usual one.</summary>
    public void Answer(int status, string body, int? retryAfterSeconds = null) => _answers.Enqueue((status, body, retryAfterSeconds));

    /// <summary>Correctly signed requests get no answer at all (they are neither refused nor served).</summary>
    public bool KeepSignedRequestsWaiting { get; set; }

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }

            // Each request on its own: one that is kept waiting must not hold the next one.
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            var body = buffer.ToArray();
            var http = context.Request;
            var path = (http.RawUrl ?? "").Split('?')[0];

            // Azure's recipe: VERB \n path and query \n date;host;hash of the body, signed with the key.
            var date = http.Headers["x-ms-date"] ?? "";
            var hash = http.Headers["x-ms-content-sha256"] ?? "";
            var toSign = $"{http.HttpMethod}\n{http.RawUrl}\n{date};{http.Headers["Host"]};{hash}";
            var expected = "HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=" +
                           Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(toSign)));
            bool valid = http.Headers["Authorization"] == expected &&
                         hash == Convert.ToBase64String(SHA256.HashData(body)) &&
                         DateTimeOffset.TryParse(date, out var sent) && (DateTimeOffset.UtcNow - sent).Duration() < TimeSpan.FromMinutes(5);

            _requests.Enqueue(new Request(http.HttpMethod, http.RawUrl ?? "", http.ContentType ?? "", body, valid));

            (int Status, string Body, int? RetryAfter) answer;
            if (path == "/")
            {
                answer = (404, "", null);
            }
            else if (!valid)
            {
                answer = (401, """{"error":{"code":"Denied","message":"Denied by the resource provider."}}""", null);
            }
            else if (KeepSignedRequestsWaiting || http.HttpMethod == "GET")
            {
                // No answer until the test is over.
                try { await Task.Delay(Timeout.Infinite, _stopping.Token); }
                catch (OperationCanceledException) { }
                context.Response.Abort();
                return;
            }
            else if (!_answers.TryDequeue(out answer))
            {
                answer = IsMessage(body)
                    ? (202, $$"""{"id":"{{Guid.NewGuid():D}}","status":"Running"}""", null)
                    : (400, """{"error":{"code":"InvalidRequest","message":"The senderAddress field is required."}}""", null);
            }

            context.Response.StatusCode = answer.Status;
            if (answer.RetryAfter is { } seconds)
                context.Response.Headers["Retry-After"] = seconds.ToString();
            var bytes = Encoding.UTF8.GetBytes(answer.Body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the client went away, or the test is over
        }
    }

    private static bool IsMessage(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("senderAddress", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
    }

    public sealed record Request(string Method, string PathAndQuery, string ContentType, byte[] Body, bool SignatureValid)
    {
        public JsonDocument Json => JsonDocument.Parse(Body);
    }
}
