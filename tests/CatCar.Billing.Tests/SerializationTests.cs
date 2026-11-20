using System.Text.Json;
using CatCar.Contexts.Communication.Domain.ExternalAccessTokens;
using CatCar.Contexts.ServiceOperations.Domain.Budgets;
using Xunit;

namespace CatCar.Billing.Tests;

public sealed class SerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Budget_RoundTripsIdentityLinesAndApproval(bool webDefaults)
    {
        var issuedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var workOrderId = Guid.NewGuid();
        var firstReference = Guid.NewGuid();
        var secondReference = Guid.NewGuid();
        var original = Budget.Issue(workOrderId,
        [
            (BudgetLineType.Service, firstReference, "Labor", 120.50m, 2),
            (BudgetLineType.Part, secondReference, "Part", 30m, 3)
        ], issuedAt).Value;
        Assert.False(original.Approve(issuedAt.AddHours(1)).IsFailure);

        var copy = RoundTrip(original, webDefaults);

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(workOrderId, copy.WorkOrderId);
        Assert.Equal(original.Status, copy.Status);
        Assert.Equal(issuedAt, copy.IssuedAt);
        Assert.Equal(original.ApprovedAt, copy.ApprovedAt);
        Assert.Equal(original.RejectionReason, copy.RejectionReason);
        Assert.Equal(331m, copy.TotalAmount);
        Assert.Equal(2, copy.Lines.Count);
        for (var i = 0; i < original.Lines.Count; i++)
        {
            var expected = original.Lines[i];
            var actual = copy.Lines[i];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.ReferenceId, actual.ReferenceId);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(expected.UnitPrice, actual.UnitPrice);
            Assert.Equal(expected.Quantity, actual.Quantity);
            Assert.Equal(expected.LineTotal, actual.LineTotal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Budget_RoundTripsRejection(bool webDefaults)
    {
        var original = Budget.Issue(Guid.NewGuid(),
            [(BudgetLineType.Part, Guid.NewGuid(), "Part", 10m, 1)], DateTime.UtcNow).Value;
        Assert.False(original.Reject("Too expensive").IsFailure);

        var copy = RoundTrip(original, webDefaults);

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Status, copy.Status);
        Assert.Equal("Too expensive", copy.RejectionReason);
        Assert.Single(copy.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Token_RoundTripsIdentityHashAndConsumption(bool webDefaults)
    {
        var (original, rawToken) = ExternalAccessToken.Issue(Guid.NewGuid(), TimeSpan.FromDays(1));
        var active = RoundTrip(original, webDefaults);
        Assert.Equal(original.Id, active.Id);
        Assert.Equal(original.BudgetId, active.BudgetId);
        Assert.Equal(original.TokenHash, active.TokenHash);
        Assert.Equal(original.IssuedAt, active.IssuedAt);
        Assert.Equal(original.ExpiresAt, active.ExpiresAt);
        Assert.False(active.Validate(rawToken).IsFailure);
        Assert.False(original.Consume().IsFailure);

        var consumed = RoundTrip(original, webDefaults);
        Assert.Equal(original.Id, consumed.Id);
        Assert.Equal(original.Status, consumed.Status);
        Assert.Equal(original.ConsumedAt, consumed.ConsumedAt);
        Assert.True(consumed.Validate(rawToken).IsFailure);
    }

    private static T RoundTrip<T>(T value, bool webDefaults)
    {
        var options = webDefaults ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions();
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, options), options)!;
    }
}
