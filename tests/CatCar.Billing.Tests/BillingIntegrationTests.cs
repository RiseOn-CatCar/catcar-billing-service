using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using CatCar.Billing.Api;
using CatCar.Billing.Api.Payments;
using CatCar.Contexts.Communication.Domain.ExternalAccessTokens;
using CatCar.Contexts.Communication.Notifications;
using CatCar.Contexts.ServiceOperations.Domain.Budgets;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace CatCar.Billing.Tests;

public sealed class BillingIntegrationTests
{
    [Fact]
    public async Task RequestBudget_PersistsBudgetAndToken_AndPublishesIssued()
    {
        await using var fixture = await BillingFixture.StartAsync();
        var request = NewBudgetRequest();

        var tracked = await fixture.Services.TrackActivity().InvokeMessageAndWaitAsync(request);
        var issued = tracked.FindSingleTrackedMessageOfType<BudgetIssued>();
        Assert.Contains(fixture.Services.GetRequiredService<ObservedEvents>().Messages, x => x is BudgetIssued e && e.BudgetId == issued.BudgetId);

        await using var session = fixture.Store.QuerySession();
        var budget = await session.LoadAsync<Budget>(issued.BudgetId);
        Assert.NotNull(budget);
        Assert.Equal(request.WorkOrderId, budget.WorkOrderId);
        Assert.Equal(BudgetStatus.Active, budget.Status);
        Assert.Equal(150m, budget.TotalAmount);
        Assert.Single(budget.Lines);
        var token = Assert.Single(await session.Query<ExternalAccessToken>().Where(x => x.BudgetId == issued.BudgetId).ToListAsync());
        var customer = await session.LoadAsync<BudgetCustomer>(issued.BudgetId);
        Assert.NotNull(customer);
        Assert.Equal(request.CustomerId, customer.CustomerId);
        Assert.Equal(request.CorrelationId, customer.CorrelationId);
        Assert.Equal(request.WorkOrderId, issued.WorkOrderId);
        Assert.Equal(budget.TotalAmount, issued.TotalAmount);
        Assert.Equal(request.CorrelationId, issued.CorrelationId);
        var rawToken = fixture.RawToken();
        Assert.Equal(ExternalAccessToken.ComputeHash(rawToken), token.TokenHash);
        Assert.NotEqual(rawToken, token.TokenHash);
    }

    [Fact]
    public async Task Approval_ConsumesTokenAndPersistsApprovedBudget_AndPublishesApproved()
    {
        await using var fixture = await BillingFixture.StartAsync();
        var request = NewBudgetRequest();
        var issuedActivity = await fixture.Services.TrackActivity().InvokeMessageAndWaitAsync(request);
        var issued = issuedActivity.FindSingleTrackedMessageOfType<BudgetIssued>();
        var rawToken = fixture.RawToken();
        HttpResponseMessage? response = null;

        var approvalActivity = await fixture.Services.ExecuteAndWaitAsync(async () =>
            response = await fixture.Client.PostAsJsonAsync($"/api/v1/billing/approval-links/{rawToken}", new { Approved = true, Reason = (string?)null }));

        using (response)
            Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        var approved = approvalActivity.FindSingleTrackedMessageOfType<BudgetApproved>();
        Assert.Equal(request.WorkOrderId, approved.WorkOrderId);
        Assert.Contains(fixture.Services.GetRequiredService<ObservedEvents>().Messages, x => x is BudgetApproved e && e.BudgetId == approved.BudgetId);
        Assert.Equal(issued.BudgetId, approved.BudgetId);
        Assert.Equal(issued.TotalAmount, approved.TotalAmount);
        Assert.Equal(request.CorrelationId, approved.CorrelationId);
        await using var session = fixture.Store.QuerySession();
        var budget = await session.LoadAsync<Budget>(issued.BudgetId);
        Assert.NotNull(budget);
        Assert.Equal(BudgetStatus.Approved, budget.Status);
        Assert.NotNull(budget.ApprovedAt);
        var token = Assert.Single(await session.Query<ExternalAccessToken>().Where(x => x.BudgetId == issued.BudgetId).ToListAsync());
        Assert.NotNull(token.ConsumedAt);
        using var repeated = await fixture.Client.PostAsJsonAsync($"/api/v1/billing/approval-links/{rawToken}", new { Approved = true, Reason = (string?)null });
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
    }

    [Fact]
    public async Task RequestPayment_WithoutProviderToken_PersistsFailureAndPublishesPaymentFailed()
    {
        await using var fixture = await BillingFixture.StartAsync();
        var request = new RequestPayment(Guid.NewGuid(), Guid.NewGuid(), 150m, "client@example.test", Guid.NewGuid());

        var tracked = await fixture.Services.TrackActivity().InvokeMessageAndWaitAsync(request);
        var failed = tracked.FindSingleTrackedMessageOfType<PaymentFailed>();
        Assert.Contains(fixture.Services.GetRequiredService<ObservedEvents>().Messages, x => x is PaymentFailed e && e.BudgetId == failed.BudgetId);

        await using var session = fixture.Store.QuerySession();
        var payment = await session.LoadAsync<Payment>(request.BudgetId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(request.WorkOrderId, payment.WorkOrderId);
        Assert.Equal(request.Amount, payment.Amount);
        Assert.Equal(request.CorrelationId, payment.CorrelationId);
        Assert.Equal(request.WorkOrderId, failed.WorkOrderId);
        Assert.Equal(request.BudgetId, failed.BudgetId);
        Assert.Equal("payment-provider-not-configured", failed.Reason);
        Assert.Equal(request.CorrelationId, failed.CorrelationId);
    }

    [Fact]
    public async Task Simulator_DecisionsAndRefund_UsePaymentWorkflow()
    {
        await using var fixture = await BillingFixture.StartAsync("Simulator");
        var approved = new RequestPayment(Guid.NewGuid(), Guid.NewGuid(), 150m, "client@example.test", Guid.NewGuid());
        await fixture.Services.TrackActivity().InvokeMessageAndWaitAsync(approved);
        using var page = await fixture.Client.GetAsync($"/simulator/payments/{approved.BudgetId}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("SIMULATION - this is NOT Mercado Pago and no money moves", await page.Content.ReadAsStringAsync());
        using var pending = await fixture.Client.PostAsJsonAsync($"/simulator/payments/{approved.BudgetId}/decision", new { decision = "pending" });
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        using var invalid = await fixture.Client.PostAsJsonAsync($"/simulator/payments/{approved.BudgetId}/decision", new { decision = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await fixture.Client.PostAsJsonAsync($"/simulator/payments/{Guid.NewGuid()}/decision", new { decision = "approve" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        HttpResponseMessage? response = null;
        var confirmed = await fixture.Services.ExecuteAndWaitAsync(async () =>
            response = await fixture.Client.PostAsJsonAsync($"/simulator/payments/{approved.BudgetId}/decision", new { decision = "approve" }));
        using (response) Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.StartsWith("SIM-", confirmed.FindSingleTrackedMessageOfType<PaymentConfirmed>().PaymentId);
        await fixture.Services.TrackActivity().InvokeMessageAndWaitAsync(new RefundPayment(approved.WorkOrderId, approved.BudgetId, "cancelled", Guid.NewGuid()));
        var rejected = approved with { WorkOrderId = Guid.NewGuid(), BudgetId = Guid.NewGuid() };
        await fixture.Services.TrackActivity().InvokeMessageAndWaitAsync(rejected);
        var failed = await fixture.Services.ExecuteAndWaitAsync(async () =>
            response = await fixture.Client.PostAsync($"/simulator/payments/{rejected.BudgetId}/decision", new FormUrlEncodedContent(new Dictionary<string, string> { ["decision"] = "reject" })));
        using (response) Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.Equal(rejected.BudgetId, failed.FindSingleTrackedMessageOfType<PaymentFailed>().BudgetId);
        await using var session = fixture.Store.QuerySession();
        Assert.Equal(PaymentStatus.Refunded, (await session.LoadAsync<Payment>(approved.BudgetId))!.Status);
        Assert.Equal(PaymentStatus.Failed, (await session.LoadAsync<Payment>(rejected.BudgetId))!.Status);
    }

    [Fact]
    public async Task Simulator_WhenMercadoPagoSelected_EndpointsAreAbsent()
    {
        await using var fixture = await BillingFixture.StartAsync();
        using var response = await fixture.Client.GetAsync($"/simulator/payments/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static RequestBudget NewBudgetRequest() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Client", "client@example.test",
        [new BudgetLineRequest("Service", Guid.NewGuid(), "Inspection", 75m, 2)], DateTime.UtcNow, Guid.NewGuid());

    private sealed class BillingFixture : IAsyncDisposable
    {
        private static readonly SemaphoreSlim StartupLock = new(1, 1);
        private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        private readonly CapturingEmailSender _email = new();
        private WebApplicationFactory<Program> _factory = null!;
        public HttpClient Client { get; private set; } = null!;
        public IServiceProvider Services => _factory.Services;
        public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();

        public static async Task<BillingFixture> StartAsync(string provider = "MercadoPago")
        {
            var fixture = new BillingFixture();
            try
            {
                await fixture._database.StartAsync();
                fixture._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:billing"] = fixture._database.GetConnectionString(),
                        ["ConnectionStrings:messaging"] = "amqp://guest:guest@localhost:5672",
                        ["Jwt:Secret"] = "integration-test-secret-at-least-32-characters",
                        ["Communication:ExternalBaseUrl"] = "http://localhost",
                        ["MercadoPago:AccessToken"] = ""
                    }));
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IEmailSender>();
                        services.AddSingleton<IEmailSender>(fixture._email);
                        services.ConfigureWolverine(options =>
                        {
                            options.StubAllExternalTransports();
                            options.Discovery.IncludeAssembly(typeof(BillingIntegrationTests).Assembly);
                            options.PublishMessage<BudgetIssued>().ToLocalQueue("test-budget-issued");
                            options.PublishMessage<BudgetApproved>().ToLocalQueue("test-budget-approved");
                            options.PublishMessage<PaymentFailed>().ToLocalQueue("test-payment-failed");
                            options.PublishMessage<PaymentConfirmed>().ToLocalQueue("test-payment-confirmed");
                        });
                        services.AddSingleton<ObservedEvents>();
                    });
                });
                // The minimal host reads configuration before test host overrides run.
                await StartupLock.WaitAsync();
                var oldDatabase = Environment.GetEnvironmentVariable("ConnectionStrings__billing");
                var oldMessaging = Environment.GetEnvironmentVariable("ConnectionStrings__messaging");
                var oldSecret = Environment.GetEnvironmentVariable("Jwt__Secret");
                var oldProvider = Environment.GetEnvironmentVariable("Payments__Provider");
                try
                {
                    Environment.SetEnvironmentVariable("ConnectionStrings__billing", fixture._database.GetConnectionString());
                    Environment.SetEnvironmentVariable("ConnectionStrings__messaging", "amqp://guest:guest@localhost:5672");
                    Environment.SetEnvironmentVariable("Payments__Provider", provider);
                    Environment.SetEnvironmentVariable("Jwt__Secret", "integration-test-secret-at-least-32-characters");
                    fixture.Client = fixture._factory.CreateClient();
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ConnectionStrings__billing", oldDatabase);
                    Environment.SetEnvironmentVariable("ConnectionStrings__messaging", oldMessaging);
                    Environment.SetEnvironmentVariable("Payments__Provider", oldProvider);
                    Environment.SetEnvironmentVariable("Jwt__Secret", oldSecret);
                    StartupLock.Release();
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public string RawToken()
        {
            var email = Assert.Single(_email.Messages);
            return email.Body[(email.Body.LastIndexOf('/') + 1)..];
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (_factory is not null) await _factory.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Messages { get; } = new();
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}

public sealed class ObservedEvents
{
    public ConcurrentQueue<object> Messages { get; } = new();
}

[WolverineHandler]
public static class BillingTestEventHandlers
{
    public static void Handle(BudgetIssued message, ObservedEvents observed) => observed.Messages.Enqueue(message);
    public static void Handle(BudgetApproved message, ObservedEvents observed) => observed.Messages.Enqueue(message);
    public static void Handle(PaymentFailed message, ObservedEvents observed) => observed.Messages.Enqueue(message);
}
