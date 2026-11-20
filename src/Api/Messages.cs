namespace CatCar.Billing.Api;

using Wolverine.Attributes;

public sealed record BudgetLineRequest(string Type, Guid ReferenceId, string Description, decimal UnitPrice, int Quantity);
[MessageIdentity("catcar.os.request-budget.v1")]
public sealed record RequestBudget(Guid WorkOrderId, Guid CustomerId, string CustomerName, string CustomerEmail, IReadOnlyList<BudgetLineRequest> Lines, DateTime RequestedAt, Guid CorrelationId);
[MessageIdentity("catcar.os.cancel-budget.v1")]
public sealed record CancelBudget(Guid WorkOrderId, Guid BudgetId, string Reason, Guid CorrelationId);
[MessageIdentity("catcar.os.request-payment.v1")]
public sealed record RequestPayment(Guid WorkOrderId, Guid BudgetId, decimal Amount, string CustomerEmail, Guid CorrelationId);
[MessageIdentity("catcar.os.refund-payment.v1")]
public sealed record RefundPayment(Guid WorkOrderId, Guid BudgetId, string Reason, Guid CorrelationId);
[MessageIdentity("catcar.os.work-order-status-changed.v1")]
public sealed record WorkOrderStatusChanged(Guid WorkOrderId, Guid CustomerId, string CustomerName, string? CustomerEmail, string PreviousStatus, string NewStatus, DateTime ChangedAt, Guid CorrelationId, Guid EventId);
[MessageIdentity("catcar.billing.budget-issued.v1")]
public sealed record BudgetIssued(Guid WorkOrderId, Guid BudgetId, decimal TotalAmount, DateTime IssuedAt, Guid CorrelationId);
[MessageIdentity("catcar.billing.budget-approved.v1")]
public sealed record BudgetApproved(Guid WorkOrderId, Guid BudgetId, decimal TotalAmount, DateTime ApprovedAt, Guid CorrelationId);
[MessageIdentity("catcar.billing.budget-rejected.v1")]
public sealed record BudgetRejected(Guid WorkOrderId, Guid BudgetId, string Reason, DateTime RejectedAt, Guid CorrelationId);
[MessageIdentity("catcar.billing.payment-confirmed.v1")]
public sealed record PaymentConfirmed(Guid WorkOrderId, Guid BudgetId, string PaymentId, decimal Amount, DateTime ConfirmedAt, Guid CorrelationId);
[MessageIdentity("catcar.billing.payment-failed.v1")]
public sealed record PaymentFailed(Guid WorkOrderId, Guid BudgetId, string Reason, DateTime FailedAt, Guid CorrelationId);
