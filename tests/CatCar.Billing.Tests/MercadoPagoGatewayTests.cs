using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CatCar.Billing.Api.Payments;
using Xunit;

namespace CatCar.Billing.Tests;

public sealed class MercadoPagoGatewayTests
{
    [Fact]
    public async Task CreatePreference_SendsPaymentDetailsAndReturnsCheckoutUrl()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/checkout/preferences", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("budget-1", body.RootElement.GetProperty("external_reference").GetString());
            var item = body.RootElement.GetProperty("items")[0];
            Assert.Equal(125.50m, item.GetProperty("unit_price").GetDecimal());
            Assert.Equal("BRL", item.GetProperty("currency_id").GetString());
            Assert.Equal("customer@example.com", body.RootElement.GetProperty("payer").GetProperty("email").GetString());
            return Json("{\"id\":\"pref-1\",\"init_point\":\"https://checkout.example/pay\"}");
        });
        var gateway = CreateGateway(handler);

        var preference = await gateway.CreatePreferenceAsync("budget-1", 125.50m, "customer@example.com");

        Assert.Equal("pref-1", preference.Id);
        Assert.Equal("https://checkout.example/pay", preference.CheckoutUrl);
    }

    [Fact]
    public async Task FindPayments_EncodesReferenceAndReturnsPagination()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Contains("external_reference=budget%2F1%20%26%20other", request.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("offset=2&limit=1", request.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(Json("{\"paging\":{\"total\":3,\"offset\":2,\"limit\":1},\"results\":[{\"id\":123,\"status\":\"approved\",\"external_reference\":\"budget/1 & other\",\"transaction_amount\":12.5}]}"));
        });

        var page = await CreateGateway(handler).FindPaymentsAsync("budget/1 & other", 2, 1);

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Offset);
        Assert.Equal("123", Assert.Single(page.Payments).Id);
        Assert.Equal(12.5m, page.Payments[0].Amount);
    }

    [Fact]
    public async Task GetPayment_RefetchesProviderState()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/v1/payments/123", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"id\":123,\"status\":\"approved\",\"status_detail\":\"accredited\",\"external_reference\":\"budget-1\",\"transaction_amount\":10}"));
        });

        var payment = await CreateGateway(handler).GetPaymentAsync("123");

        Assert.Equal("approved", payment.Status);
        Assert.Equal("budget-1", payment.ExternalReference);
        Assert.Equal("accredited", payment.StatusDetail);
    }

    [Fact]
    public async Task Refund_UsesStableIdempotencyKey()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/payments/123/refunds", request.RequestUri!.AbsolutePath);
            Assert.Equal("refund-1", Assert.Single(request.Headers.GetValues("X-Idempotency-Key")));
            return Task.FromResult(Json("{}"));
        });
        await CreateGateway(handler).RefundAsync("123", "refund-1");
    }

    [Fact]
    public async Task GetPayment_PropagatesProviderFailure()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateGateway(handler).GetPaymentAsync("123"));
    }

    [Fact]
    public void WebhookSignature_ValidatesSignedQueryIdAndRequestId()
    {
        var signature = Sign("id:abc123;request-id:req-1;ts:1700000000000;", "secret");
        Assert.True(MercadoPagoWebhookSignature.IsValid(signature, "req-1", "ABC123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, "req-1", "different", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, "other", "ABC123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, "req-1", "ABC123", "wrong"));
    }

    [Fact]
    public void WebhookSignature_RejectsMalformedAndDuplicateValues()
    {
        var signature = Sign("id:123;ts:1700000000000;", "secret");
        Assert.True(MercadoPagoWebhookSignature.IsValid(signature, null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature + ",ts=1", null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid("ts=1,v1=" + new string('z', 64), null, "123", "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, null, null, "secret"));
        Assert.False(MercadoPagoWebhookSignature.IsValid(signature, null, "123", ""));
    }

    private static string Sign(string manifest, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        return $"ts=1700000000000,v1={Convert.ToHexString(hash)}";
    }

    private static MercadoPagoGateway CreateGateway(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new MercadoPagoOptions { AccessToken = "access-token", WebhookSecret = "secret" });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
