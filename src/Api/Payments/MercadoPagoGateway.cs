using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatCar.Billing.Api.Payments;

public sealed class MercadoPagoOptions
{
    public string AccessToken { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string NotificationUrl { get; set; } = string.Empty;
}

public sealed record MercadoPagoPreference(string Id, string CheckoutUrl);

public sealed record MercadoPagoPayment(string Id, string Status, string? StatusDetail, string? ExternalReference, decimal Amount);


public interface IPaymentGateway
{
    Task<MercadoPagoPreference> CreatePreferenceAsync(string externalReference, decimal amount, string customerEmail, CancellationToken cancellationToken = default);
    Task<MercadoPagoPaymentPage> FindPaymentsAsync(string externalReference, int offset = 0, int limit = 50, CancellationToken cancellationToken = default);
    Task<MercadoPagoPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default);
    Task RefundAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken = default);
}
public sealed record MercadoPagoPaymentPage(IReadOnlyList<MercadoPagoPayment> Payments, int Total, int Offset, int Limit);

public sealed class MercadoPagoGateway(HttpClient httpClient, MercadoPagoOptions options) : IPaymentGateway
{
    private static readonly Uri ApiBase = new("https://api.mercadopago.com/");

    public async Task<MercadoPagoPreference> CreatePreferenceAsync(
        string externalReference, decimal amount, string customerEmail, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerEmail);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        using var request = CreateRequest(HttpMethod.Post, "checkout/preferences");
        request.Content = JsonContent.Create(new
        {
            external_reference = externalReference,
            items = new[] { new { title = "CatCar work order", quantity = 1, currency_id = "BRL", unit_price = amount } },
            payer = new { email = customerEmail },
            notification_url = options.NotificationUrl
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PreferenceResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Mercado Pago returned an empty preference response.");
        if (string.IsNullOrWhiteSpace(result.Id) || string.IsNullOrWhiteSpace(result.InitPoint))
            throw new InvalidDataException("Mercado Pago returned an incomplete preference response.");
        return new MercadoPagoPreference(result.Id, result.InitPoint);
    }

    public async Task<MercadoPagoPaymentPage> FindPaymentsAsync(
        string externalReference, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalReference);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var request = CreateRequest(HttpMethod.Get,
            $"v1/payments/search?external_reference={Uri.EscapeDataString(externalReference)}&offset={offset}&limit={limit}");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Mercado Pago returned an empty search response.");
        if (result.Results is null || result.Paging is null)
            throw new InvalidDataException("Mercado Pago returned an incomplete search response.");
        return new MercadoPagoPaymentPage(result.Results.Select(ToPayment).ToArray(), result.Paging.Total, result.Paging.Offset, result.Paging.Limit);
    }

    // Call after verifying the webhook signature; never trust payment state from the webhook body.
    public async Task<MercadoPagoPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentId);
        using var request = CreateRequest(HttpMethod.Get, $"v1/payments/{Uri.EscapeDataString(paymentId)}");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Mercado Pago returned an empty payment response.");
        return ToPayment(result);
    }

    // Reuse the same idempotency key for every retry of the same refund.
    public async Task RefundAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var request = CreateRequest(HttpMethod.Post, $"v1/payments/{Uri.EscapeDataString(paymentId)}/refunds");
        request.Headers.Add("X-Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessToken);
        var request = new HttpRequestMessage(method, new Uri(httpClient.BaseAddress ?? ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        return request;
    }

    private static MercadoPagoPayment ToPayment(PaymentResponse payment)
    {
        var id = payment.Id.ValueKind switch
        {
            JsonValueKind.Number => payment.Id.GetRawText(),
            JsonValueKind.String => payment.Id.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(payment.Status) || payment.Amount is null)
            throw new InvalidDataException("Mercado Pago returned an incomplete payment response.");
        return new MercadoPagoPayment(id, payment.Status, payment.StatusDetail, payment.ExternalReference, payment.Amount.Value);
    }

    private sealed record PreferenceResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("init_point")] string? InitPoint);

    private sealed record PaymentResponse(
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("status_detail")] string? StatusDetail,
        [property: JsonPropertyName("external_reference")] string? ExternalReference,
        [property: JsonPropertyName("transaction_amount")] decimal? Amount);

    private sealed record SearchResponse(
        [property: JsonPropertyName("results")] PaymentResponse[]? Results,
        [property: JsonPropertyName("paging")] PagingResponse? Paging);

    private sealed record PagingResponse(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("offset")] int Offset,
        [property: JsonPropertyName("limit")] int Limit);
}
