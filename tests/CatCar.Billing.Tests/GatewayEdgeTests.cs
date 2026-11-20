using System.Net;
using System.Security.Cryptography;
using System.Text;
using CatCar.Billing.Api.Payments;
using Xunit;

namespace CatCar.Billing.Tests;

public sealed class GatewayEdgeTests
{
    [Fact]
    public async Task Gateway_UsesConfiguredBaseAddressAndMapsStringPaymentId()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://localhost:1234/v1/payments/pay%2F1", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("token", request.Headers.Authorization.Parameter);
            return Response(HttpStatusCode.OK, "{\"id\":\"pay/1\",\"status\":\"pending\",\"transaction_amount\":0}");
        }))
        { BaseAddress = new Uri("http://localhost:1234/") };
        var gateway = new MercadoPagoGateway(client, new MercadoPagoOptions { AccessToken = "token" });

        var payment = await gateway.GetPaymentAsync("pay/1");

        Assert.Equal("pay/1", payment.Id);
        Assert.Equal("pending", payment.Status);
        Assert.Equal(0m, payment.Amount);
        Assert.Null(payment.StatusDetail);
        Assert.Null(payment.ExternalReference);
    }

    [Fact]
    public async Task CreatePreference_IncludesNotificationUrl()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal("https://example.test/webhook", json.RootElement.GetProperty("notification_url").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("items")[0].GetProperty("quantity").GetInt32());
            return Response(HttpStatusCode.Created, "{\"id\":\"pref\",\"init_point\":\"https://example.test/pay\"}");
        }));
        var gateway = new MercadoPagoGateway(client, new MercadoPagoOptions
        {
            AccessToken = "token",
            NotificationUrl = "https://example.test/webhook"
        });

        var result = await gateway.CreatePreferenceAsync("order", 1m, "buyer@example.test");

        Assert.Equal("pref", result.Id);
        Assert.Equal("https://example.test/pay", result.CheckoutUrl);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"pref\",\"init_point\":\" \"}")]
    public async Task CreatePreference_RejectsEmptyOrIncompleteResponse(string body)
    {
        using var client = ClientReturning(HttpStatusCode.OK, body);
        var gateway = Gateway(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => gateway.CreatePreferenceAsync("order", 1m, "a@b.test"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"results\":[],\"paging\":null}")]
    [InlineData("{\"results\":[{\"id\":null,\"status\":\"approved\",\"transaction_amount\":1}],\"paging\":{}}")]
    public async Task FindPayments_RejectsEmptyOrIncompleteResponse(string body)
    {
        using var client = ClientReturning(HttpStatusCode.OK, body);
        var gateway = Gateway(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => gateway.FindPaymentsAsync("order"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":true,\"status\":\"approved\",\"transaction_amount\":1}")]
    [InlineData("{\"id\":42,\"status\":\" \",\"transaction_amount\":1}")]
    [InlineData("{\"id\":42,\"status\":\"approved\"}")]
    public async Task GetPayment_RejectsEmptyOrIncompleteResponse(string body)
    {
        using var client = ClientReturning(HttpStatusCode.OK, body);
        var gateway = Gateway(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => gateway.GetPaymentAsync("42"));
    }

    [Fact]
    public async Task FindPayments_MapsEmptyPageAndPaging()
    {
        using var client = ClientReturning(HttpStatusCode.OK, "{\"results\":[],\"paging\":{\"total\":0,\"offset\":0,\"limit\":50}}");
        var page = await Gateway(client).FindPaymentsAsync("order");
        Assert.Empty(page.Payments);
        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.Offset);
        Assert.Equal(50, page.Limit);
    }

    [Theory]
    [InlineData("preference")]
    [InlineData("search")]
    [InlineData("payment")]
    [InlineData("refund")]
    public async Task Gateway_PropagatesProviderErrorStatus(string operation)
    {
        using var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}");
        var gateway = Gateway(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            switch (operation)
            {
                case "preference": await gateway.CreatePreferenceAsync("order", 1m, "a@b.test"); break;
                case "search": await gateway.FindPaymentsAsync("order"); break;
                case "payment": await gateway.GetPaymentAsync("42"); break;
                case "refund": await gateway.RefundAsync("42", "key"); break;
            }
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task Refund_EscapesIdAndSendsIdempotencyKeyOnNoContentSuccess()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/payments/a%2Fb/refunds", request.RequestUri!.AbsolutePath);
            Assert.Equal("stable-key", Assert.Single(request.Headers.GetValues("X-Idempotency-Key")));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        await Gateway(client).RefundAsync("a/b", "stable-key");
    }

    [Fact]
    public async Task Gateway_RejectsInvalidArgumentsBeforeSending()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected HTTP request")));
        var gateway = Gateway(client);
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreatePreferenceAsync(" ", 1m, "a@b.test"));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreatePreferenceAsync("order", 1m, " "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.CreatePreferenceAsync("order", 0m, "a@b.test"));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.FindPaymentsAsync(" "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.FindPaymentsAsync("order", -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.FindPaymentsAsync("order", 0, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetPaymentAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RefundAsync(" ", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.RefundAsync("42", " "));
        var noToken = new MercadoPagoGateway(client, new MercadoPagoOptions());
        await Assert.ThrowsAsync<ArgumentException>(() => noToken.GetPaymentAsync("42"));
    }

    [Fact]
    public void Signature_AcceptsWhitespaceUnknownFieldsAndLowercaseDigest()
    {
        var digest = Digest("id:abc;request-id:req;ts:001;");
        Assert.True(MercadoPagoWebhookSignature.IsValid($"ignored, other=x, v1 = {digest.ToLowerInvariant()} , ts = 001", "req", "ABC", "secret"));
    }

    [Fact]
    public void Signature_HandlesMissingAndEmptyRequestIdEquivalently()
    {
        var signature = $"ts=1,v1={Digest("id:123;ts:1;")}";
        Assert.True(MercadoPagoWebhookSignature.IsValid(signature, null, "123", "secret"));
        Assert.True(MercadoPagoWebhookSignature.IsValid(signature, "", "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, " ", "123", "secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("v1=abcd")]
    [InlineData("ts=,v1=abcd")]
    [InlineData("ts=-1,v1=abcd")]
    [InlineData("ts=1.0,v1=abcd")]
    [InlineData("ts=１２,v1=abcd")]
    [InlineData("ts=1,v1=abcd")]
    public void Signature_RejectsMissingOrMalformedFields(string? signature)
    {
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, null, "123", "secret"));
    }

    [Fact]
    public void Signature_RejectsDuplicatesInvalidHexAndModifiedDigest()
    {
        var digest = Digest("id:123;ts:1;");
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts=1,v1={digest},v1={digest}", null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts=1,ts=1,v1={digest}", null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts=1,v1={new string('g', 64)}", null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts=1,v1={(digest[0] == '0' ? '1' : '0')}{digest[1..]}", null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts=1,v1={digest}", null, " ", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts=1,v1={digest}", null, "123", " "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("１２")]
    public void Signature_RejectsNonAsciiNumericTimestampEvenWithValidLengthDigest(string timestamp)
    {
        Assert.False(MercadoPagoWebhookSignature.IsValid($"ts={timestamp},v1={new string('0', 64)}", null, "123", "secret"));
    }

    private static string Digest(string manifest) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(manifest)));

    private static MercadoPagoGateway Gateway(HttpClient client) =>
        new MercadoPagoGateway(client, new MercadoPagoOptions { AccessToken = "token" });

    private static HttpClient ClientReturning(HttpStatusCode status, string body) =>
        new(new Handler(_ => Response(status, body)));

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
