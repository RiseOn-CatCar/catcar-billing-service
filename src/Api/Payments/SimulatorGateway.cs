using System.Net.Http.Json;
using Marten;
using Wolverine.Marten;

namespace CatCar.Billing.Api.Payments;

public sealed class SimulatorGateway(IConfiguration configuration) : IPaymentGateway
{
    public Task<MercadoPagoPreference> CreatePreferenceAsync(string externalReference, decimal amount, string customerEmail, CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["Payments:Simulator:BaseUrl"] ?? "http://localhost:5000";
        return Task.FromResult(new MercadoPagoPreference($"SIM-{externalReference}", $"{baseUrl.TrimEnd('/')}/simulator/payments/{externalReference}"));
    }

    public Task<MercadoPagoPaymentPage> FindPaymentsAsync(string externalReference, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MercadoPagoPaymentPage([], 0, offset, limit));

    public Task<MercadoPagoPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Simulator decisions are submitted through the simulator endpoint.");

    public Task RefundAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static partial class SimulatorEndpoints
{
    public static void ValidateProvider(string provider, bool isProduction, bool allowInProduction)
    {
        if (provider is not ("MercadoPago" or "Simulator"))
            throw new InvalidOperationException("Payments:Provider must be MercadoPago or Simulator.");
        if (provider == "Simulator" && isProduction && !allowInProduction)
            throw new InvalidOperationException("Simulated payments are disabled in Production unless Payments:Simulator:AllowInProduction is true.");
    }

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "SIMULATED PAYMENTS ARE ACTIVE: no money moves; this is not Mercado Pago.")]
    public static partial void LogSimulatorActive(ILogger logger);

    public static void MapSimulatorEndpoints(this WebApplication app)
    {
        app.MapGet("/simulator/payments/{paymentId:guid}", async (Guid paymentId, IDocumentSession session, CancellationToken ct) =>
        {
            var payment = await session.LoadAsync<Payment>(paymentId, ct).ConfigureAwait(false);
            if (payment is null || payment.Provider != "simulator") return Results.NotFound();
            return Results.Content($"""
                <!doctype html><html><head><title>Payment simulation</title></head><body>
                <h1>SIMULATION - this is NOT Mercado Pago and no money moves</h1>
                <p>Payment {payment.Id} — status: {payment.Status} — provider: simulator</p>
                <form method="post" action="/simulator/payments/{payment.Id}/decision">
                <button name="decision" value="approve">Approve</button>
                <button name="decision" value="reject">Reject</button>
                <button name="decision" value="pending">Leave pending</button>
                </form></body></html>
                """, "text/html");
        });
        app.MapPost("/simulator/payments/{paymentId:guid}/decision", async (Guid paymentId, HttpRequest request, IDocumentSession session, IMartenOutbox bus, CancellationToken ct) =>
        {
            string? decision;
            if (request.HasFormContentType)
                decision = (await request.ReadFormAsync(ct).ConfigureAwait(false))["decision"].ToString();
            else if (request.HasJsonContentType())
                decision = (await request.ReadFromJsonAsync<SimulatorDecision>(cancellationToken: ct).ConfigureAwait(false))?.Decision;
            else return Results.BadRequest();
            if (decision is not ("approve" or "reject" or "pending")) return Results.BadRequest();
            var payment = await session.LoadAsync<Payment>(paymentId, ct).ConfigureAwait(false);
            bus.Enroll(session);
            if (payment is null || payment.Provider != "simulator") return Results.NotFound();
            if (decision != "pending")
                await PaymentHandlers.ApplyAsync(new MercadoPagoPayment($"SIM-{payment.Id}", decision == "approve" ? "approved" : "rejected", null, payment.Id.ToString(), payment.Amount), session, bus, ct).ConfigureAwait(false);
            var decided = await session.LoadAsync<Payment>(paymentId, ct).ConfigureAwait(false) ?? payment;
            return Results.Ok(new { decided.Id, decided.Status, decided.Provider, decided.ExternalId });
        });
    }

    private sealed record SimulatorDecision(string? Decision);
}
