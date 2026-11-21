namespace CatCar.Billing.Api.Payments;

using CatCar.Contexts.Communication.Notifications;
using Marten;
using Wolverine.Attributes;
using Wolverine.Marten;

public enum PaymentStatus { Pending, Confirmed, Failed, Refunded }

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid BudgetId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string? ExternalId { get; set; }
    public string Provider { get; set; } = "mercadopago";
    public string? CheckoutUrl { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[WolverineHandler]
public static class PaymentHandlers
{
    public static async Task Handle(RequestPayment message, IDocumentSession session, IMartenOutbox bus, IPaymentGateway gateway, MercadoPagoOptions options, IEmailSender email, CancellationToken ct)
    {
        var payment = await session.LoadAsync<Payment>(message.BudgetId, ct).ConfigureAwait(false);
        if (payment is not null) return;
        payment = new Payment { Id = message.BudgetId, BudgetId = message.BudgetId, WorkOrderId = message.WorkOrderId, Amount = message.Amount, CorrelationId = message.CorrelationId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Provider = gateway is SimulatorGateway ? "simulator" : "mercadopago" };
        if (gateway is not SimulatorGateway && string.IsNullOrWhiteSpace(options.AccessToken))
        {
            payment.Status = PaymentStatus.Failed;
            session.Store(payment);
            await bus.PublishAsync(new PaymentFailed(message.WorkOrderId, message.BudgetId, "payment-provider-not-configured", DateTime.UtcNow, message.CorrelationId)).ConfigureAwait(false);
        }
        else
        {
            var preference = await gateway.CreatePreferenceAsync(message.BudgetId.ToString(), message.Amount, message.CustomerEmail, ct).ConfigureAwait(false);
            if (gateway is SimulatorGateway) payment.ExternalId = preference.Id;
            payment.CheckoutUrl = preference.CheckoutUrl;
            session.Store(payment);
            await email.SendAsync(new EmailMessage(message.CustomerEmail, message.CustomerEmail, gateway is SimulatorGateway ? "CatCar - simulated payment (no money moves)" : "CatCar - pagamento", preference.CheckoutUrl), ct).ConfigureAwait(false);
        }
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static async Task Handle(RefundPayment message, IDocumentSession session, IPaymentGateway gateway, CancellationToken ct)
    {
        var payment = await session.LoadAsync<Payment>(message.BudgetId, ct).ConfigureAwait(false);
        if (payment is null || payment.WorkOrderId != message.WorkOrderId || payment.Status != PaymentStatus.Confirmed || payment.ExternalId is null) return;
        await gateway.RefundAsync(payment.ExternalId, message.BudgetId.ToString(), ct).ConfigureAwait(false);
        payment.Status = PaymentStatus.Refunded;
        payment.UpdatedAt = DateTime.UtcNow;
        session.Store(payment);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static async Task ApplyAsync(MercadoPagoPayment remote, IDocumentSession session, IMartenOutbox bus, CancellationToken ct)
    {
        if (!Guid.TryParse(remote.ExternalReference, out var budgetId)) return;
        var payment = await session.LoadAsync<Payment>(budgetId, ct).ConfigureAwait(false);
        if (payment is null || payment.Status != PaymentStatus.Pending || payment.Amount != remote.Amount) return;
        if (remote.Status == "approved")
        {
            payment.Status = PaymentStatus.Confirmed;
            payment.ExternalId = remote.Id;
            await bus.PublishAsync(new PaymentConfirmed(payment.WorkOrderId, budgetId, remote.Id, payment.Amount, DateTime.UtcNow, payment.CorrelationId)).ConfigureAwait(false);
        }
        else if (remote.Status is "rejected" or "cancelled")
        {
            payment.Status = PaymentStatus.Failed;
            await bus.PublishAsync(new PaymentFailed(payment.WorkOrderId, budgetId, remote.Status, DateTime.UtcNow, payment.CorrelationId)).ConfigureAwait(false);
        }
        else return;
        payment.UpdatedAt = DateTime.UtcNow;
        session.Store(payment);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

public sealed class PaymentReconciliation(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            using var scope = scopeFactory.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<MercadoPagoOptions>();
            if (string.IsNullOrWhiteSpace(options.AccessToken)) continue;
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var session = store.LightweightSession();
            var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();
            var bus = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            bus.Enroll(session);
            var pending = await session.Query<Payment>().Where(x => x.Status == PaymentStatus.Pending).ToListAsync(stoppingToken).ConfigureAwait(false);
            foreach (var payment in pending)
            {
                var offset = 0;
                MercadoPagoPaymentPage page;
                do
                {
                    page = await gateway.FindPaymentsAsync(payment.BudgetId.ToString(), offset, cancellationToken: stoppingToken).ConfigureAwait(false);
                    foreach (var remote in page.Payments) await PaymentHandlers.ApplyAsync(remote, session, bus, stoppingToken).ConfigureAwait(false);
                    offset += page.Payments.Count;
                } while (page.Payments.Count > 0 && offset < page.Total);
            }
        }
    }
}
