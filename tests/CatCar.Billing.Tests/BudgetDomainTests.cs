using System.Security.Cryptography;
using System.Text;
using CatCar.Contexts.Communication.Domain.ExternalAccessTokens;
using CatCar.Contexts.ServiceOperations.Domain.Budgets;
using Xunit;

namespace CatCar.Billing.Tests;

public sealed class BudgetDomainTests
{
    private static readonly DateTime IssuedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Issue_CreatesIndependentLineSnapshotsAndCalculatesTotals()
    {
        var workOrderId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var lines = new List<(BudgetLineType Type, Guid ReferenceId, string Description, decimal UnitPrice, int Quantity)>
        {
            (BudgetLineType.Service, serviceId, "Labor", 120.50m, 2),
            (BudgetLineType.Part, partId, "Part", 30m, 3)
        };

        var result = Budget.Issue(workOrderId, lines, IssuedAt);

        Assert.False(result.IsFailure);
        var budget = result.Value;
        lines.Clear();
        Assert.NotEqual(Guid.Empty, budget.Id);
        Assert.Equal(workOrderId, budget.WorkOrderId);
        Assert.Equal(BudgetStatus.Active, budget.Status);
        Assert.Equal(IssuedAt, budget.IssuedAt);
        Assert.Null(budget.ApprovedAt);
        Assert.Null(budget.RejectionReason);
        Assert.Equal(2, budget.Lines.Count);
        var service = budget.Lines[0];
        var part = budget.Lines[1];
        Assert.NotEqual(Guid.Empty, service.Id);
        Assert.NotEqual(service.Id, part.Id);
        Assert.Equal(BudgetLineType.Service, service.Type);
        Assert.Equal(serviceId, service.ReferenceId);
        Assert.Equal("Labor", service.Description);
        Assert.Equal(120.50m, service.UnitPrice);
        Assert.Equal(2, service.Quantity);
        Assert.Equal(241m, service.LineTotal);
        Assert.Equal(BudgetLineType.Part, part.Type);
        Assert.Equal(partId, part.ReferenceId);
        Assert.Equal("Part", part.Description);
        Assert.Equal(30m, part.UnitPrice);
        Assert.Equal(3, part.Quantity);
        Assert.Equal(90m, part.LineTotal);
        Assert.Equal(331m, budget.TotalAmount);
    }

    [Fact]
    public void Issue_RejectsMissingWorkOrderOrLines()
    {
        Assert.True(Budget.Issue(Guid.Empty, [(BudgetLineType.Service, Guid.NewGuid(), "Labor", 1m, 1)], IssuedAt).IsFailure);
        Assert.True(Budget.Issue(Guid.NewGuid(), [], IssuedAt).IsFailure);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Issue_RejectsNonUtcTimestamp(DateTimeKind kind)
    {
        var timestamp = DateTime.SpecifyKind(IssuedAt, kind);
        Assert.True(Budget.Issue(Guid.NewGuid(), [(BudgetLineType.Part, Guid.NewGuid(), "Part", 1m, 1)], timestamp).IsFailure);
    }

    [Fact]
    public void Approve_AllowsIssuanceTimestampAndRecordsDecision()
    {
        var budget = NewBudget();
        Assert.False(budget.Approve(IssuedAt).IsFailure);
        Assert.Equal(BudgetStatus.Approved, budget.Status);
        Assert.Equal(IssuedAt, budget.ApprovedAt);
        Assert.Null(budget.RejectionReason);
    }

    [Fact]
    public void Approve_RejectsInvalidTimestampsWithoutChangingBudget()
    {
        var budget = NewBudget();
        Assert.True(budget.Approve(IssuedAt.AddTicks(-1)).IsFailure);
        Assert.True(budget.Approve(DateTime.SpecifyKind(IssuedAt, DateTimeKind.Local)).IsFailure);
        Assert.True(budget.Approve(DateTime.SpecifyKind(IssuedAt, DateTimeKind.Unspecified)).IsFailure);
        Assert.Equal(BudgetStatus.Active, budget.Status);
        Assert.Null(budget.ApprovedAt);
        Assert.False(budget.Approve(IssuedAt.AddMinutes(1)).IsFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Reject_RequiresReasonWithoutChangingBudget(string? reason)
    {
        var budget = NewBudget();
        Assert.True(budget.Reject(reason).IsFailure);
        Assert.Equal(BudgetStatus.Active, budget.Status);
        Assert.Null(budget.RejectionReason);
    }

    [Fact]
    public void Reject_TrimsReasonAndCanBeReplaced()
    {
        var budget = NewBudget();
        Assert.False(budget.Reject("  Too expensive  ").IsFailure);
        Assert.Equal(BudgetStatus.Rejected, budget.Status);
        Assert.Equal("Too expensive", budget.RejectionReason);
        Assert.Null(budget.ApprovedAt);
        Assert.False(budget.Replace().IsFailure);
        Assert.Equal(BudgetStatus.Replaced, budget.Status);
        Assert.Equal("Too expensive", budget.RejectionReason);
    }

    [Fact]
    public void Replace_AllowsActiveBudget()
    {
        var budget = NewBudget();
        Assert.False(budget.Replace().IsFailure);
        Assert.Equal(BudgetStatus.Replaced, budget.Status);
    }

    [Theory]
    [InlineData(BudgetStatus.Approved)]
    [InlineData(BudgetStatus.Rejected)]
    [InlineData(BudgetStatus.Replaced)]
    public void Decisions_RejectNonActiveBudgets(BudgetStatus status)
    {
        var budget = NewBudget();
        switch (status)
        {
            case BudgetStatus.Approved:
                Assert.False(budget.Approve(IssuedAt).IsFailure);
                break;
            case BudgetStatus.Rejected:
                Assert.False(budget.Reject("Reason").IsFailure);
                break;
            case BudgetStatus.Replaced:
                Assert.False(budget.Replace().IsFailure);
                break;
        }

        Assert.True(budget.Approve(IssuedAt.AddHours(1)).IsFailure);
        Assert.True(budget.Reject("Another reason").IsFailure);
        Assert.Equal(status, budget.Status);
        Assert.Equal(status == BudgetStatus.Approved ? IssuedAt : null, budget.ApprovedAt);
        Assert.Equal(status == BudgetStatus.Rejected ? "Reason" : null, budget.RejectionReason);
        if (status != BudgetStatus.Rejected)
            Assert.True(budget.Replace().IsFailure);
    }

    [Fact]
    public void TokenIssue_StoresHashAndValidatesWithoutConsuming()
    {
        var budgetId = Guid.NewGuid();
        var before = DateTime.UtcNow;
        var (token, raw) = ExternalAccessToken.Issue(budgetId, TimeSpan.FromHours(1));
        var after = DateTime.UtcNow;

        Assert.NotEqual(Guid.Empty, token.Id);
        Assert.Equal(budgetId, token.BudgetId);
        Assert.Equal(ExternalAccessTokenStatus.Active, token.Status);
        Assert.InRange(token.IssuedAt, before, after);
        Assert.Equal(token.IssuedAt.AddHours(1), token.ExpiresAt);
        Assert.Null(token.ConsumedAt);
        Assert.Equal(43, raw.Length);
        Assert.All(raw, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), token.TokenHash);
        Assert.Equal(token.TokenHash, ExternalAccessToken.ComputeHash(raw));
        Assert.NotEqual(raw, token.TokenHash);
        Assert.False(token.Validate(raw).IsFailure);
        Assert.False(token.Validate(raw).IsFailure);
        Assert.Equal(ExternalAccessTokenStatus.Active, token.Status);
        Assert.Null(token.ConsumedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("wrong-token")]
    public void TokenValidate_RejectsInvalidValuesWithoutConsuming(string? value)
    {
        var (token, raw) = ExternalAccessToken.Issue(Guid.NewGuid(), TimeSpan.FromHours(1));
        Assert.True(token.Validate(value).IsFailure);
        Assert.Equal(ExternalAccessTokenStatus.Active, token.Status);
        Assert.Null(token.ConsumedAt);
        Assert.False(token.Validate(raw).IsFailure);
    }

    [Fact]
    public void TokenConsume_IsSingleUseAndCannotBeExpired()
    {
        var (token, raw) = ExternalAccessToken.Issue(Guid.NewGuid(), TimeSpan.FromHours(1));
        var before = DateTime.UtcNow;
        Assert.False(token.Consume().IsFailure);
        var after = DateTime.UtcNow;
        Assert.Equal(ExternalAccessTokenStatus.Consumed, token.Status);
        Assert.NotNull(token.ConsumedAt);
        Assert.InRange(token.ConsumedAt.Value, before, after);
        var consumedAt = token.ConsumedAt;
        Assert.True(token.Validate(raw).IsFailure);
        Assert.True(token.Consume().IsFailure);
        Assert.True(token.Expire().IsFailure);
        Assert.Equal(ExternalAccessTokenStatus.Consumed, token.Status);
        Assert.Equal(consumedAt, token.ConsumedAt);
    }

    [Fact]
    public void TokenExpire_RejectsValidationConsumptionAndRepeatedExpiration()
    {
        var (token, raw) = ExternalAccessToken.Issue(Guid.NewGuid(), TimeSpan.FromHours(1));
        Assert.False(token.Expire().IsFailure);
        Assert.True(token.Validate(raw).IsFailure);
        Assert.True(token.Consume().IsFailure);
        Assert.True(token.Expire().IsFailure);
        Assert.Equal(ExternalAccessTokenStatus.Expired, token.Status);
        Assert.Null(token.ConsumedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TokenWithElapsedTtl_ExpiresLazilyOnValidationOrConsumption(bool validate)
    {
        var (token, raw) = ExternalAccessToken.Issue(Guid.NewGuid(), TimeSpan.FromSeconds(-1));
        Assert.Equal(ExternalAccessTokenStatus.Active, token.Status);
        Assert.True(validate ? token.Validate(raw).IsFailure : token.Consume().IsFailure);
        Assert.Equal(ExternalAccessTokenStatus.Expired, token.Status);
        Assert.Null(token.ConsumedAt);
        Assert.True(token.Validate(raw).IsFailure);
        Assert.True(token.Consume().IsFailure);
    }

    private static Budget NewBudget() => Budget.Issue(Guid.NewGuid(),
        [(BudgetLineType.Service, Guid.NewGuid(), "Labor", 10m, 1)], IssuedAt).Value;
}
