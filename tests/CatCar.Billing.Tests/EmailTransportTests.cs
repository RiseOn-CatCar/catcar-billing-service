using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Communication.Email;
using CatCar.Contexts.Communication.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using DomainEmailMessage = CatCar.Contexts.Communication.Notifications.EmailMessage;

namespace CatCar.Billing.Tests;

public sealed class EmailTransportTests
{
    private static readonly DomainEmailMessage Message = new(
        "cliente@example.com",
        "Maria Cliente",
        "Orçamento disponível",
        "<p>Seu orçamento secreto-123 está pronto.</p>");

    // ---------- LoggingEmailSender ----------

    [Fact]
    public async Task LoggingSender_LogsSimulatedDeliveryWithoutLeakingMessageData()
    {
        var logger = new CapturingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(logger);

        await sender.SendAsync(Message);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Simulated email delivery.", entry.Text);
        Assert.DoesNotContain(Message.ToAddress, entry.Text);
        Assert.DoesNotContain(Message.ToName, entry.Text);
        Assert.DoesNotContain(Message.Subject, entry.Text);
        Assert.DoesNotContain("secreto-123", entry.Text);
    }

    [Fact]
    public async Task LoggingSender_ThrowsWhenCancelledBeforeSendingAndDoesNotLog()
    {
        var logger = new CapturingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message, cts.Token));

        Assert.Empty(logger.Entries);
    }

    // ---------- MailpitEmailSender ----------

    [Fact]
    public async Task MailpitSender_PostsMessageToSendEndpoint()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var client = MailpitClient(async (request, ct) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sender = new MailpitEmailSender(client, "noreply@catcar.local");

        await sender.SendAsync(Message);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("http://mailpit.test:8025/api/v1/send", captured.RequestUri!.ToString());
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        var from = Property(root, "From");
        Assert.Equal("noreply@catcar.local", Property(from, "Email").GetString());
        Assert.Equal("CatCar", Property(from, "Name").GetString());
        var to = Assert.Single(Property(root, "To").EnumerateArray());
        Assert.Equal(Message.ToAddress, Property(to, "Email").GetString());
        Assert.Equal(Message.ToName, Property(to, "Name").GetString());
        Assert.Equal(Message.Subject, Property(root, "Subject").GetString());
        Assert.Equal(Message.Body, Property(root, "HTML").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task MailpitSender_PropagatesNonSuccessStatus(HttpStatusCode status)
    {
        using var client = MailpitClient((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var sender = new MailpitEmailSender(client, "noreply@catcar.local");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Message));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task MailpitSender_PropagatesTransportFailure()
    {
        using var client = MailpitClient((_, _) => throw new HttpRequestException("connection refused"));
        var sender = new MailpitEmailSender(client, "noreply@catcar.local");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Message));

        Assert.Equal("connection refused", ex.Message);
    }

    [Fact]
    public async Task MailpitSender_ThrowsWhenCancelledBeforeSending()
    {
        using var client = MailpitClient((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var sender = new MailpitEmailSender(client, "noreply@catcar.local");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message, cts.Token));
    }

    [Fact]
    public async Task MailpitSender_ThrowsWhenCancelledWhileRequestIsInFlight()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = MailpitClient(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sender = new MailpitEmailSender(client, "noreply@catcar.local");
        using var cts = new CancellationTokenSource();

        var send = sender.SendAsync(Message, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    // ---------- AzureCommunicationEmailSender ----------

    [Fact]
    public async Task AzureSender_SendsExpectedPayloadAndCompletesWhenOperationSucceeds()
    {
        var requests = new List<(HttpMethod Method, string Uri, string? Body)>();
        var handler = new AcsHandler(async (request, ct) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (requests) requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return request.Method == HttpMethod.Post
                ? Accepted(OperationId)
                : OperationStatus("Succeeded");
        });
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");

        await sender.SendAsync(Message);

        var post = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("/emails:send", post.Uri);
        using var json = JsonDocument.Parse(post.Body!);
        var root = json.RootElement;
        Assert.Equal("DoNotReply@catcar.example", Property(root, "senderAddress").GetString());
        var content = Property(root, "content");
        Assert.Equal(Message.Subject, Property(content, "subject").GetString());
        Assert.Equal(Message.Body, Property(content, "plainText").GetString());
        var recipients = Property(root, "recipients");
        var to = Assert.Single(Property(recipients, "to").EnumerateArray());
        Assert.Equal(Message.ToAddress, Property(to, "address").GetString());

        // WaitUntil.Completed must poll the long-running operation at least once.
        Assert.Contains(requests, r => r.Method == HttpMethod.Get && r.Uri.Contains(OperationId));
    }

    [Fact]
    public async Task AzureSender_ThrowsWhenServiceRejectsRequest()
    {
        var handler = new AcsHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":{"code":"InvalidRequestBody","message":"bad recipient"}}""",
                Encoding.UTF8,
                "application/json")
        }));
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => sender.SendAsync(Message));

        Assert.Equal(400, ex.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    public async Task AzureSender_ThrowsWhenDeliveryOperationEndsUnsuccessfully(string terminalStatus)
    {
        var handler = new AcsHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Accepted(OperationId)
            : OperationStatus(terminalStatus, terminalStatus == "Failed"
                ? """{"code":"SenderDenied","message":"sender not verified"}"""
                : null)));
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");

        await Assert.ThrowsAsync<RequestFailedException>(() => sender.SendAsync(Message));

        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("", "body")]
    [InlineData("subject", "")]
    public async Task AzureSender_RejectsInvalidContentBeforeCallingService(string subject, string body)
    {
        var handler = new AcsHandler((_, _) => Task.FromResult(Accepted(OperationId)));
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            sender.SendAsync(new DomainEmailMessage(Message.ToAddress, Message.ToName, subject, body)));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AzureSender_PropagatesTransportFailure()
    {
        var handler = new AcsHandler((_, _) => throw new HttpRequestException("network down"));
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");

        await Assert.ThrowsAsync<RequestFailedException>(() => sender.SendAsync(Message));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AzureSender_ThrowsWhenCancelledBeforeSending()
    {
        var handler = new AcsHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Accepted(OperationId));
        });
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message, cts.Token));
    }

    [Fact]
    public async Task AzureSender_ThrowsWhenCancelledWhileRequestIsInFlight()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AcsHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Accepted(OperationId);
        });
        var sender = new AzureCommunicationEmailSender(AcsClient(handler), "DoNotReply@catcar.example");
        using var cts = new CancellationTokenSource();

        var send = sender.SendAsync(Message, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    [Fact]
    public void AzureSender_RejectsNullClient()
    {
        Assert.Throws<ArgumentNullException>(() => new AzureCommunicationEmailSender(null!, "DoNotReply@catcar.example"));
    }

    [Fact]
    public void AzureSender_BuildsFromConfiguration()
    {
        var configuration = Configuration(
            ("Communication:AzureCommunicationServicesConnectionString",
                $"endpoint=https://catcar.communication.azure.com/;accesskey={AccessKey}"),
            ("Communication:SenderAddress", "DoNotReply@catcar.example"));

        var sender = new AzureCommunicationEmailSender(configuration);

        Assert.IsAssignableFrom<IEmailSender>(sender);
    }

    [Fact]
    public void AzureSender_RequiresConnectionStringInConfiguration()
    {
        var configuration = Configuration(("Communication:SenderAddress", "DoNotReply@catcar.example"));

        var ex = Assert.Throws<InvalidOperationException>(() => new AzureCommunicationEmailSender(configuration));

        Assert.Contains("AzureCommunicationServicesConnectionString", ex.Message);
    }

    [Fact]
    public void AzureSender_RequiresSenderAddressInConfiguration()
    {
        var configuration = Configuration(
            ("Communication:AzureCommunicationServicesConnectionString",
                $"endpoint=https://catcar.communication.azure.com/;accesskey={AccessKey}"));

        var ex = Assert.Throws<InvalidOperationException>(() => new AzureCommunicationEmailSender(configuration));

        Assert.Contains("SenderAddress", ex.Message);
    }

    // ---------- helpers ----------

    private const string OperationId = "11111111-2222-3333-4444-555555555555";
    private static readonly string AccessKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("catcar-test-access-key"));

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new Xunit.Sdk.XunitException($"JSON property '{name}' not found in {element}.");
    }

    private static HttpClient MailpitClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send)) { BaseAddress = new Uri("http://mailpit.test:8025") };

    private static EmailClient AcsClient(HttpMessageHandler handler)
    {
        var options = new EmailClientOptions
        {
            Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        return new EmailClient(
            new Uri("https://catcar.communication.azure.com"),
            new AzureKeyCredential(AccessKey),
            options);
    }

    private static HttpResponseMessage Accepted(string operationId)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(
                $$"""{"id":"{{operationId}}","status":"Running"}""",
                Encoding.UTF8,
                "application/json")
        };
        response.Headers.Add("operation-id", operationId);
        response.Headers.Add(
            "Operation-Location",
            $"https://catcar.communication.azure.com/emails/operations/{operationId}?api-version=2023-03-31");
        return response;
    }

    private static HttpResponseMessage OperationStatus(string status, string? errorJson = null)
    {
        var error = errorJson is null ? string.Empty : $$""","error":{{errorJson}}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"id":"{{OperationId}}","status":"{{status}}"{{error}}}""",
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class AcsHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return send(request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
