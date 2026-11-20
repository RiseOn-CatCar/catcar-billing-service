namespace CatCar.Billing.Api;

using CatCar.Contexts.Communication.Domain.ExternalAccessTokens;
using CatCar.Contexts.Communication.Notifications;
using CatCar.Contexts.ServiceOperations.Domain.Budgets;
using Marten;
using Wolverine.Attributes;
using Wolverine.Marten;

public sealed class BudgetCustomer
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public Guid CorrelationId { get; set; }
}

[WolverineHandler]
public static class BudgetHandlers
{
    public static async Task Handle(RequestBudget message, IDocumentSession session, IMartenOutbox bus, IEmailSender email, IConfiguration config, CancellationToken cancellationToken)
    {
        var existing = await session.Query<Budget>().FirstOrDefaultAsync(x => x.WorkOrderId == message.WorkOrderId && x.Status == BudgetStatus.Active, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return;
        var lines = message.Lines.Select(x => (Enum.Parse<BudgetLineType>(x.Type), x.ReferenceId, x.Description, x.UnitPrice, x.Quantity)).ToArray();
        var issued = Budget.Issue(message.WorkOrderId, lines, DateTime.UtcNow);
        if (issued.IsFailure) throw new InvalidOperationException("Invalid budget request.");
        var budget = issued.Value;
        var (token, raw) = ExternalAccessToken.Issue(budget.Id, TimeSpan.FromDays(7));
        session.Store(budget);
        session.Store(token);
        session.Store(new BudgetCustomer { Id = budget.Id, CustomerId = message.CustomerId, Name = message.CustomerName, Email = message.CustomerEmail, CorrelationId = message.CorrelationId });
        var url = $"{config["Communication:ExternalBaseUrl"]?.TrimEnd('/')}/api/v1/billing/approval-links/{raw}";
        await email.SendAsync(new EmailMessage(message.CustomerEmail, message.CustomerName, "CatCar - orçamento", $"Acesse {url}"), cancellationToken).ConfigureAwait(false);
        await bus.PublishAsync(new BudgetIssued(message.WorkOrderId, budget.Id, budget.TotalAmount, budget.IssuedAt, message.CorrelationId)).ConfigureAwait(false);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task Handle(CancelBudget message, IDocumentSession session, CancellationToken cancellationToken)
    {
        var budget = await session.LoadAsync<Budget>(message.BudgetId, cancellationToken).ConfigureAwait(false);
        if (budget is null || budget.WorkOrderId != message.WorkOrderId) return;
        _ = budget.Replace();
        var tokens = await session.Query<ExternalAccessToken>().Where(x => x.BudgetId == message.BudgetId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var token in tokens) { _ = token.Expire(); session.Store(token); }
        session.Store(budget);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task Handle(WorkOrderStatusChanged message, IEmailSender email, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(message.CustomerEmail) ? Task.CompletedTask : email.SendAsync(new EmailMessage(message.CustomerEmail, message.CustomerName, "CatCar - situação da ordem", $"Situação: {message.NewStatus}"), cancellationToken);
}
