namespace CatCar.Billing.Tests;

using System.Text.Json;
using CatCar.Billing.Api;
using Wolverine.Attributes;
using Xunit;

public sealed class MessageContractTests
{
    [Theory]
    [InlineData(typeof(RequestBudget), "catcar.os.request-budget.v1", "WorkOrderId,CustomerId,CustomerName,CustomerEmail,Lines,RequestedAt,CorrelationId")]
    [InlineData(typeof(CancelBudget), "catcar.os.cancel-budget.v1", "WorkOrderId,BudgetId,Reason,CorrelationId")]
    [InlineData(typeof(RequestPayment), "catcar.os.request-payment.v1", "WorkOrderId,BudgetId,Amount,CustomerEmail,CorrelationId")]
    [InlineData(typeof(RefundPayment), "catcar.os.refund-payment.v1", "WorkOrderId,BudgetId,Reason,CorrelationId")]
    [InlineData(typeof(BudgetIssued), "catcar.billing.budget-issued.v1", "WorkOrderId,BudgetId,TotalAmount,IssuedAt,CorrelationId")]
    [InlineData(typeof(BudgetApproved), "catcar.billing.budget-approved.v1", "WorkOrderId,BudgetId,TotalAmount,ApprovedAt,CorrelationId")]
    [InlineData(typeof(BudgetRejected), "catcar.billing.budget-rejected.v1", "WorkOrderId,BudgetId,Reason,RejectedAt,CorrelationId")]
    [InlineData(typeof(PaymentConfirmed), "catcar.billing.payment-confirmed.v1", "WorkOrderId,BudgetId,PaymentId,Amount,ConfirmedAt,CorrelationId")]
    [InlineData(typeof(PaymentFailed), "catcar.billing.payment-failed.v1", "WorkOrderId,BudgetId,Reason,FailedAt,CorrelationId")]
    [InlineData(typeof(WorkOrderStatusChanged), "catcar.os.work-order-status-changed.v1", "WorkOrderId,CustomerId,CustomerName,CustomerEmail,PreviousStatus,NewStatus,ChangedAt,CorrelationId,EventId")]
    public void Contracts_DeclaredMessages_HaveExpectedIdentityAndFields(Type type, string identity, string fields)
    {
        Assert.NotNull(Attribute.GetCustomAttribute(type, typeof(MessageIdentityAttribute)));
        var attribute = type.CustomAttributes.Single(x => x.AttributeType == typeof(MessageIdentityAttribute));
        Assert.Equal(identity, attribute.ConstructorArguments[0].Value);
        var values = type.GetConstructors()[0].GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var instance = Activator.CreateInstance(type, values);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(instance, type));
        Assert.Equal(fields.Split(','), json.RootElement.EnumerateObject().Select(x => x.Name));
    }
}
