using System.Reflection;
using CatCar.Billing.Api;
using CatCar.Billing.Api.Payments;
using CatCar.Contexts.Communication.Notifications;
using Marten;
using Testcontainers.PostgreSql;
using Wolverine.Marten;
using Xunit;

namespace CatCar.Billing.Tests;

/// <summary>
/// Exercises the payment lifecycle (<c>RequestPayment</c> → webhook/reconciliation <c>ApplyAsync</c> → <c>RefundPayment</c>)
/// against a real Marten store on PostgreSQL (same document configuration as <c>Program</c>), with a fake payment gateway,
/// a fake e-mail sender and a recording outbox.
/// </summary>
public sealed class PaymentWorkflowTests(PaymentWorkflowStoreFixture fixture) : IClassFixture<PaymentWorkflowStoreFixture>
{
    private const string ProviderToken = "provider-token";

    private IDocumentStore Store => fixture.Store;

    // ---------------------------------------------------------------- full lifecycle

    [Fact]
    public async Task Lifecycle_RequestPayment_ThenApproval_ThenRefund()
    {
        var gateway = new FakeGateway();
        var email = new FakeEmailSender();
        var request = NewRequest(150m);

        await RequestAsync(request, gateway, email, ProviderToken);

        var pending = await LoadAsync(request.BudgetId);
        Assert.NotNull(pending);
        Assert.Equal(PaymentStatus.Pending, pending.Status);
        Assert.Null(pending.ExternalId);

        var applyOutbox = new OutboxSpy();
        await ApplyAsync(Remote(request.BudgetId, "approved", 150m, "mp-lifecycle"), applyOutbox);

        var confirmed = await LoadAsync(request.BudgetId);
        Assert.NotNull(confirmed);
        Assert.Equal(PaymentStatus.Confirmed, confirmed.Status);
        Assert.Equal("mp-lifecycle", confirmed.ExternalId);
        var published = Assert.IsType<PaymentConfirmed>(Assert.Single(applyOutbox.Messages));
        Assert.Equal(request.WorkOrderId, published.WorkOrderId);
        Assert.Equal(request.BudgetId, published.BudgetId);
        Assert.Equal("mp-lifecycle", published.PaymentId);
        Assert.Equal(150m, published.Amount);
        Assert.Equal(request.CorrelationId, published.CorrelationId);

        await RefundAsync(new RefundPayment(request.WorkOrderId, request.BudgetId, "cancelled", Guid.NewGuid()), gateway);

        var refunded = await LoadAsync(request.BudgetId);
        Assert.NotNull(refunded);
        Assert.Equal(PaymentStatus.Refunded, refunded.Status);
        Assert.Equal("mp-lifecycle", refunded.ExternalId);
        Assert.Equal(("mp-lifecycle", request.BudgetId.ToString()), Assert.Single(gateway.Refunds));
    }

    // ---------------------------------------------------------------- RequestPayment

    [Fact]
    public async Task RequestPayment_WithProviderToken_PersistsPendingPaymentAndEmailsCheckoutUrl()
    {
        var gateway = new FakeGateway();
        var email = new FakeEmailSender();
        var outbox = new OutboxSpy();
        var request = NewRequest(320.75m);

        await RequestAsync(request, gateway, email, ProviderToken, outbox);

        var payment = await LoadAsync(request.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(request.BudgetId, payment.Id);
        Assert.Equal(request.WorkOrderId, payment.WorkOrderId);
        Assert.Equal(320.75m, payment.Amount);
        Assert.Equal(request.CorrelationId, payment.CorrelationId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal($"https://checkout.test/{request.BudgetId}", payment.CheckoutUrl);
        Assert.Equal((request.BudgetId.ToString(), 320.75m, request.CustomerEmail), Assert.Single(gateway.Preferences));
        var sent = Assert.Single(email.Messages);
        Assert.Equal(request.CustomerEmail, sent.ToAddress);
        Assert.Equal(payment.CheckoutUrl, sent.Body);
        Assert.Empty(outbox.Messages);
    }

    [Fact]
    public async Task RequestPayment_ForExistingPayment_IsIdempotent()
    {
        var gateway = new FakeGateway();
        var email = new FakeEmailSender();
        var request = NewRequest(90m);
        await RequestAsync(request, gateway, email, ProviderToken);
        var first = await LoadAsync(request.BudgetId);

        await RequestAsync(request with { Amount = 999m }, gateway, email, ProviderToken);

        var second = await LoadAsync(request.BudgetId);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(90m, second.Amount);
        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        Assert.Single(gateway.Preferences);
        Assert.Single(email.Messages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestPayment_WithoutProviderToken_PersistsFailedPaymentAndPublishesPaymentFailed(string token)
    {
        var gateway = new FakeGateway();
        var email = new FakeEmailSender();
        var outbox = new OutboxSpy();
        var request = NewRequest(200m);
        var before = DateTime.UtcNow;

        await RequestAsync(request, gateway, email, token, outbox);

        var payment = await LoadAsync(request.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.CheckoutUrl);
        var failed = Assert.IsType<PaymentFailed>(Assert.Single(outbox.Messages));
        Assert.Equal(request.WorkOrderId, failed.WorkOrderId);
        Assert.Equal(request.BudgetId, failed.BudgetId);
        Assert.Equal("payment-provider-not-configured", failed.Reason);
        Assert.Equal(request.CorrelationId, failed.CorrelationId);
        Assert.InRange(failed.FailedAt, before, DateTime.UtcNow);
        Assert.Empty(gateway.Preferences);
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task RequestPayment_WhenGatewayFails_PersistsNothingAndCanBeRetried()
    {
        var gateway = new FakeGateway { PreferenceFailure = new HttpRequestException("gateway down") };
        var email = new FakeEmailSender();
        var request = NewRequest(75m);

        await Assert.ThrowsAsync<HttpRequestException>(() => RequestAsync(request, gateway, email, ProviderToken));

        Assert.Null(await LoadAsync(request.BudgetId));
        Assert.Empty(email.Messages);

        gateway.PreferenceFailure = null;
        await RequestAsync(request, gateway, email, ProviderToken);

        var payment = await LoadAsync(request.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Single(email.Messages);
    }

    // ---------------------------------------------------------------- ApplyAsync

    [Fact]
    public async Task ApplyAsync_Approved_ConfirmsPaymentAndPublishesPaymentConfirmed()
    {
        var seeded = await SeedAsync(PaymentStatus.Pending, amount: 480m);
        var outbox = new OutboxSpy();
        var before = DateTime.UtcNow;

        await ApplyAsync(Remote(seeded.BudgetId, "approved", 480m, "mp-approved"), outbox);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Confirmed, payment.Status);
        Assert.Equal("mp-approved", payment.ExternalId);
        Assert.True(payment.UpdatedAt > seeded.UpdatedAt);
        var confirmed = Assert.IsType<PaymentConfirmed>(Assert.Single(outbox.Messages));
        Assert.Equal(seeded.WorkOrderId, confirmed.WorkOrderId);
        Assert.Equal(seeded.BudgetId, confirmed.BudgetId);
        Assert.Equal("mp-approved", confirmed.PaymentId);
        Assert.Equal(480m, confirmed.Amount);
        Assert.Equal(seeded.CorrelationId, confirmed.CorrelationId);
        Assert.InRange(confirmed.ConfirmedAt, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task ApplyAsync_RejectedOrCancelled_FailsPaymentAndPublishesPaymentFailed(string remoteStatus)
    {
        var seeded = await SeedAsync(PaymentStatus.Pending);
        var outbox = new OutboxSpy();

        await ApplyAsync(Remote(seeded.BudgetId, remoteStatus, seeded.Amount, "mp-failed"), outbox);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.ExternalId);
        Assert.True(payment.UpdatedAt > seeded.UpdatedAt);
        var failed = Assert.IsType<PaymentFailed>(Assert.Single(outbox.Messages));
        Assert.Equal(seeded.WorkOrderId, failed.WorkOrderId);
        Assert.Equal(seeded.BudgetId, failed.BudgetId);
        Assert.Equal(remoteStatus, failed.Reason);
        Assert.Equal(seeded.CorrelationId, failed.CorrelationId);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("in_process")]
    [InlineData("authorized")]
    [InlineData("in_mediation")]
    [InlineData("refunded")]
    public async Task ApplyAsync_NonFinalOrUnknownRemoteStatus_LeavesPaymentPendingWithoutPublishing(string remoteStatus)
    {
        var seeded = await SeedAsync(PaymentStatus.Pending);
        var outbox = new OutboxSpy();

        await ApplyAsync(Remote(seeded.BudgetId, remoteStatus, seeded.Amount, "mp-ignored"), outbox);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.ExternalId);
        Assert.Equal(seeded.UpdatedAt, payment.UpdatedAt);
        Assert.Empty(outbox.Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public async Task ApplyAsync_InvalidExternalReference_IsIgnored(string? externalReference)
    {
        var seeded = await SeedAsync(PaymentStatus.Pending);
        var outbox = new OutboxSpy();

        await ApplyAsync(new MercadoPagoPayment("mp-bad-ref", "approved", null, externalReference, seeded.Amount), outbox);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Empty(outbox.Messages);
    }

    [Fact]
    public async Task ApplyAsync_UnknownPayment_DoesNotCreateOrPublishAnything()
    {
        var unknown = Guid.NewGuid();
        var outbox = new OutboxSpy();

        await ApplyAsync(Remote(unknown, "approved", 10m, "mp-unknown"), outbox);

        Assert.Null(await LoadAsync(unknown));
        Assert.Empty(outbox.Messages);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task ApplyAsync_AmountMismatch_IsIgnored(string remoteStatus)
    {
        var seeded = await SeedAsync(PaymentStatus.Pending, amount: 100m);
        var outbox = new OutboxSpy();

        await ApplyAsync(Remote(seeded.BudgetId, remoteStatus, 99.99m, "mp-mismatch"), outbox);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.ExternalId);
        Assert.Empty(outbox.Messages);
    }

    [Theory]
    [InlineData(PaymentStatus.Confirmed, "mp-original")]
    [InlineData(PaymentStatus.Failed, null)]
    [InlineData(PaymentStatus.Refunded, "mp-original")]
    public async Task ApplyAsync_PaymentAlreadyFinal_IsNotChangedByLaterRemoteStatus(PaymentStatus status, string? externalId)
    {
        var seeded = await SeedAsync(status, externalId);
        var outbox = new OutboxSpy();

        await ApplyAsync(Remote(seeded.BudgetId, "approved", seeded.Amount, "mp-late"), outbox);
        await ApplyAsync(Remote(seeded.BudgetId, "rejected", seeded.Amount, "mp-late"), outbox);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(status, payment.Status);
        Assert.Equal(externalId, payment.ExternalId);
        Assert.Equal(seeded.UpdatedAt, payment.UpdatedAt);
        Assert.Empty(outbox.Messages);
    }

    [Fact]
    public async Task ApplyAsync_DuplicateWebhookDelivery_PublishesOnlyOnce()
    {
        var seeded = await SeedAsync(PaymentStatus.Pending);
        var outbox = new OutboxSpy();
        var remote = Remote(seeded.BudgetId, "approved", seeded.Amount, "mp-dup");

        await ApplyAsync(remote, outbox);
        await ApplyAsync(remote, outbox);

        Assert.IsType<PaymentConfirmed>(Assert.Single(outbox.Messages));
        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Confirmed, payment.Status);
    }

    [Fact]
    public async Task ApplyAsync_ReconciliationPage_ProcessesPendingThenIgnoresStaleEntries()
    {
        var seeded = await SeedAsync(PaymentStatus.Pending, amount: 60m);
        var outbox = new OutboxSpy();
        var page = new[]
        {
            Remote(seeded.BudgetId, "in_process", 60m, "mp-1"),
            Remote(seeded.BudgetId, "approved", 60m, "mp-2"),
            Remote(seeded.BudgetId, "rejected", 60m, "mp-3")
        };

        await using (var session = Store.LightweightSession())
        {
            foreach (var remote in page) await PaymentHandlers.ApplyAsync(remote, session, outbox.Outbox, CancellationToken.None);
        }

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Confirmed, payment.Status);
        Assert.Equal("mp-2", payment.ExternalId);
        Assert.Equal("mp-2", Assert.IsType<PaymentConfirmed>(Assert.Single(outbox.Messages)).PaymentId);
    }

    [Fact]
    public async Task ApplyAsync_ConfirmedPayment_CanBeRefundedWithConfirmationId()
    {
        var seeded = await SeedAsync(PaymentStatus.Pending, amount: 42m);
        var gateway = new FakeGateway();
        await ApplyAsync(Remote(seeded.BudgetId, "approved", 42m, "mp-then-refund"), new OutboxSpy());

        await RefundAsync(new RefundPayment(seeded.WorkOrderId, seeded.BudgetId, "customer-request", Guid.NewGuid()), gateway);

        Assert.Equal(("mp-then-refund", seeded.BudgetId.ToString()), Assert.Single(gateway.Refunds));
        Assert.Equal(PaymentStatus.Refunded, (await LoadAsync(seeded.BudgetId))!.Status);
    }

    // ---------------------------------------------------------------- RefundPayment

    [Fact]
    public async Task Refund_ConfirmedPayment_RefundsOnceUsingBudgetIdAsIdempotencyKey()
    {
        var seeded = await SeedAsync(PaymentStatus.Confirmed, "mp-refund");
        var gateway = new FakeGateway();
        var message = new RefundPayment(seeded.WorkOrderId, seeded.BudgetId, "customer-request", Guid.NewGuid());

        await RefundAsync(message, gateway);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal("mp-refund", payment.ExternalId);
        Assert.True(payment.UpdatedAt > seeded.UpdatedAt);
        Assert.Equal(("mp-refund", seeded.BudgetId.ToString()), Assert.Single(gateway.Refunds));
    }

    [Fact]
    public async Task Refund_Redelivered_DoesNotRefundTwice()
    {
        var seeded = await SeedAsync(PaymentStatus.Confirmed, "mp-twice");
        var gateway = new FakeGateway();
        var message = new RefundPayment(seeded.WorkOrderId, seeded.BudgetId, "customer-request", Guid.NewGuid());

        await RefundAsync(message, gateway);
        var afterFirst = await LoadAsync(seeded.BudgetId);
        await RefundAsync(message, gateway);

        var afterSecond = await LoadAsync(seeded.BudgetId);
        Assert.Single(gateway.Refunds);
        Assert.NotNull(afterFirst);
        Assert.NotNull(afterSecond);
        Assert.Equal(PaymentStatus.Refunded, afterSecond.Status);
        Assert.Equal(afterFirst.UpdatedAt, afterSecond.UpdatedAt);
    }

    [Fact]
    public async Task Refund_WhenGatewayFails_KeepsPaymentConfirmedAndRetryReusesIdempotencyKey()
    {
        var seeded = await SeedAsync(PaymentStatus.Confirmed, "mp-retry");
        var gateway = new FakeGateway { RefundFailure = new HttpRequestException("gateway down") };
        var message = new RefundPayment(seeded.WorkOrderId, seeded.BudgetId, "customer-request", Guid.NewGuid());

        await Assert.ThrowsAsync<HttpRequestException>(() => RefundAsync(message, gateway));

        var afterFailure = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(afterFailure);
        Assert.Equal(PaymentStatus.Confirmed, afterFailure.Status);
        Assert.Equal(seeded.UpdatedAt, afterFailure.UpdatedAt);

        gateway.RefundFailure = null;
        await RefundAsync(message, gateway);

        Assert.Equal(PaymentStatus.Refunded, (await LoadAsync(seeded.BudgetId))!.Status);
        Assert.Equal(2, gateway.Refunds.Count);
        Assert.All(gateway.Refunds, refund => Assert.Equal(("mp-retry", seeded.BudgetId.ToString()), refund));
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Refunded)]
    public async Task Refund_PaymentNotConfirmed_IsIgnored(PaymentStatus status)
    {
        var seeded = await SeedAsync(status, "mp-not-confirmed");
        var gateway = new FakeGateway();

        await RefundAsync(new RefundPayment(seeded.WorkOrderId, seeded.BudgetId, "customer-request", Guid.NewGuid()), gateway);

        var payment = await LoadAsync(seeded.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(status, payment.Status);
        Assert.Equal(seeded.UpdatedAt, payment.UpdatedAt);
        Assert.Empty(gateway.Refunds);
    }

    [Fact]
    public async Task Refund_ForDifferentWorkOrder_IsIgnored()
    {
        var seeded = await SeedAsync(PaymentStatus.Confirmed, "mp-other-order");
        var gateway = new FakeGateway();

        await RefundAsync(new RefundPayment(Guid.NewGuid(), seeded.BudgetId, "customer-request", Guid.NewGuid()), gateway);

        Assert.Equal(PaymentStatus.Confirmed, (await LoadAsync(seeded.BudgetId))!.Status);
        Assert.Empty(gateway.Refunds);
    }

    [Fact]
    public async Task Refund_ConfirmedPaymentWithoutExternalId_IsIgnored()
    {
        var seeded = await SeedAsync(PaymentStatus.Confirmed);
        var gateway = new FakeGateway();

        await RefundAsync(new RefundPayment(seeded.WorkOrderId, seeded.BudgetId, "customer-request", Guid.NewGuid()), gateway);

        Assert.Equal(PaymentStatus.Confirmed, (await LoadAsync(seeded.BudgetId))!.Status);
        Assert.Empty(gateway.Refunds);
    }

    [Fact]
    public async Task Refund_UnknownPayment_IsIgnored()
    {
        var gateway = new FakeGateway();
        var budgetId = Guid.NewGuid();

        await RefundAsync(new RefundPayment(Guid.NewGuid(), budgetId, "customer-request", Guid.NewGuid()), gateway);

        Assert.Null(await LoadAsync(budgetId));
        Assert.Empty(gateway.Refunds);
    }

    // ---------------------------------------------------------------- helpers

    private static RequestPayment NewRequest(decimal amount) =>
        new(Guid.NewGuid(), Guid.NewGuid(), amount, $"client-{Guid.NewGuid():N}@example.test", Guid.NewGuid());

    private static MercadoPagoPayment Remote(Guid budgetId, string status, decimal amount, string id) =>
        new(id, status, null, budgetId.ToString(), amount);

    private async Task RequestAsync(RequestPayment request, FakeGateway gateway, FakeEmailSender email, string accessToken, OutboxSpy? outbox = null)
    {
        await using var session = Store.LightweightSession();
        await PaymentHandlers.Handle(request, session, (outbox ?? new OutboxSpy()).Outbox, gateway,
            new MercadoPagoOptions { AccessToken = accessToken }, email, CancellationToken.None);
    }

    private async Task ApplyAsync(MercadoPagoPayment remote, OutboxSpy outbox)
    {
        await using var session = Store.LightweightSession();
        await PaymentHandlers.ApplyAsync(remote, session, outbox.Outbox, CancellationToken.None);
    }

    private async Task RefundAsync(RefundPayment message, FakeGateway gateway)
    {
        await using var session = Store.LightweightSession();
        await PaymentHandlers.Handle(message, session, gateway, CancellationToken.None);
    }

    private async Task<Payment?> LoadAsync(Guid budgetId)
    {
        await using var session = Store.QuerySession();
        return await session.LoadAsync<Payment>(budgetId);
    }

    private async Task<Payment> SeedAsync(PaymentStatus status, string? externalId = null, decimal amount = 150m)
    {
        var created = DateTime.UtcNow.AddHours(-1);
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            WorkOrderId = Guid.NewGuid(),
            Amount = amount,
            Status = status,
            ExternalId = externalId,
            CheckoutUrl = "https://checkout.test/seeded",
            CorrelationId = Guid.NewGuid(),
            CreatedAt = created,
            UpdatedAt = created
        };
        payment.BudgetId = payment.Id;
        await using var session = Store.LightweightSession();
        session.Store(payment);
        await session.SaveChangesAsync();
        return payment;
    }

    private sealed class FakeGateway : IPaymentGateway
    {
        public List<(string ExternalReference, decimal Amount, string CustomerEmail)> Preferences { get; } = [];
        public List<(string PaymentId, string IdempotencyKey)> Refunds { get; } = [];
        public Exception? PreferenceFailure { get; set; }
        public Exception? RefundFailure { get; set; }

        public Task<MercadoPagoPreference> CreatePreferenceAsync(string externalReference, decimal amount, string customerEmail, CancellationToken cancellationToken = default)
        {
            if (PreferenceFailure is not null) throw PreferenceFailure;
            Preferences.Add((externalReference, amount, customerEmail));
            return Task.FromResult(new MercadoPagoPreference($"pref-{externalReference}", $"https://checkout.test/{externalReference}"));
        }

        public Task RefundAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            Refunds.Add((paymentId, idempotencyKey));
            return RefundFailure is null ? Task.CompletedTask : Task.FromException(RefundFailure);
        }

        public Task<MercadoPagoPaymentPage> FindPaymentsAsync(string externalReference, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MercadoPagoPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake <see cref="IMartenOutbox"/> that records every published/sent message.</summary>
    private sealed class OutboxSpy
    {
        public OutboxSpy()
        {
            Outbox = DispatchProxy.Create<IMartenOutbox, RecordingOutboxProxy>();
            Messages = ((RecordingOutboxProxy)(object)Outbox).Messages;
        }

        public IMartenOutbox Outbox { get; }
        public List<object> Messages { get; }
    }
}

/// <summary>Proxy backing the fake outbox; must be public for <see cref="DispatchProxy"/>.</summary>
public class RecordingOutboxProxy : DispatchProxy
{
    public List<object> Messages { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name is "PublishAsync" or "SendAsync" && args is [{ } message, ..]) Messages.Add(message);
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(ValueTask)) return default(ValueTask);
        if (returnType == typeof(Task)) return Task.CompletedTask;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>One PostgreSQL container and Marten store (configured like <c>Program</c>) shared by the payment workflow tests.</summary>
public sealed class PaymentWorkflowStoreFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public IDocumentStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        Store = DocumentStore.For(options =>
        {
            options.Connection(_database.GetConnectionString());
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
            options.UseSystemTextJsonForSerialization();
            options.Schema.For<Payment>().Identity(x => x.Id);
            options.Schema.For<Payment>().UseOptimisticConcurrency(true);
        });
    }

    public async Task DisposeAsync()
    {
        Store?.Dispose();
        await _database.DisposeAsync();
    }
}
