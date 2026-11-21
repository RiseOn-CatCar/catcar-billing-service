using System.Text;
using CatCar.Billing.Api;
using CatCar.Billing.Api.Payments;
using CatCar.Contexts.Communication.Domain.ExternalAccessTokens;
using CatCar.Contexts.Communication.Notifications;
using CatCar.Contexts.ServiceOperations.Domain.Budgets;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var provider = builder.Configuration["Payments:Provider"] ?? "MercadoPago";
SimulatorEndpoints.ValidateProvider(provider, builder.Environment.IsProduction(), builder.Configuration.GetValue<bool>("Payments:Simulator:AllowInProduction"));
var database = builder.Configuration.GetConnectionString("billing") ?? throw new InvalidOperationException("ConnectionStrings:billing is required.");
var messaging = builder.Configuration.GetConnectionString("messaging") ?? throw new InvalidOperationException("ConnectionStrings:messaging is required.");
builder.Services.AddMarten(options =>
{
    options.Connection(database);
    options.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
    options.UseSystemTextJsonForSerialization();
    options.Schema.For<Budget>().Identity(x => x.Id);
    options.Schema.For<Budget>().UseOptimisticConcurrency(true);
    options.Schema.For<Payment>().Identity(x => x.Id);
    options.Schema.For<Payment>().UseOptimisticConcurrency(true);
    options.Schema.For<ExternalAccessToken>().Identity(x => x.Id);
    options.Schema.For<ExternalAccessToken>().UseOptimisticConcurrency(true);
    options.Schema.For<BudgetCustomer>().Identity(x => x.Id);
}).IntegrateWithWolverine();
builder.Host.UseWolverine(options =>
{
    options.CodeGeneration.AlwaysUseServiceLocationFor<IPaymentGateway>();
    options.UseRabbitMq(new Uri(messaging)).AutoProvision().UseConventionalRouting();
    options.Policies.UseDurableInboxOnAllListeners();
    options.Policies.UseDurableOutboxOnAllSendingEndpoints();
});
var paymentOptions = builder.Configuration.GetSection("MercadoPago").Get<MercadoPagoOptions>() ?? new MercadoPagoOptions();
builder.Services.AddSingleton(paymentOptions);
builder.Services.AddHttpClient();
if (provider == "Simulator")
    builder.Services.AddTransient<IPaymentGateway, SimulatorGateway>();
else
{
    builder.Services.AddTransient<IPaymentGateway, MercadoPagoGateway>();
    builder.Services.AddHostedService<PaymentReconciliation>();
}
builder.Services.AddHttpClient(MailpitEmailSender.HttpClientName, client =>
    client.BaseAddress = new Uri(builder.Configuration["Communication:MailpitBaseUrl"] ?? throw new InvalidOperationException("Mailpit URL is required.")));
builder.Services.AddSingleton<IEmailSender>(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    return configuration["Communication:EmailTransport"] switch
    {
        "Mailpit" => new MailpitEmailSender(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(MailpitEmailSender.HttpClientName),
            configuration["Communication:SenderAddress"] ?? throw new InvalidOperationException("Sender address is required.")),
        "AzureCommunicationServices" => new AzureCommunicationEmailSender(configuration),
        "Logging" when builder.Environment.IsDevelopment() => ActivatorUtilities.CreateInstance<LoggingEmailSender>(services),
        _ => throw new InvalidOperationException("A real email transport must be configured.")
    };
});
builder.Services.AddAuthorization();
var secret = builder.Configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "CatCar",
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "CatCar.AdminApi",
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
    };
});
var app = builder.Build();
if (provider == "Simulator")
{
    SimulatorEndpoints.LogSimulatorActive(app.Logger);
    app.MapSimulatorEndpoints();
}
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await app.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync().ConfigureAwait(false);
    return;
}
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/internal/v1/payment-provider", () => Results.Ok(new { provider = provider == "Simulator" ? "simulator" : "mercadopago" }));
var billing = app.MapGroup("/api/v1/billing");
billing.MapGet("/budgets/{id:guid}", async (Guid id, IDocumentSession session, CancellationToken ct) =>
    await session.LoadAsync<Budget>(id, ct).ConfigureAwait(false) is { } budget ? Results.Ok(budget) : Results.NotFound()).RequireAuthorization();
billing.MapGet("/work-orders/{id:guid}/budgets", async (Guid id, IDocumentSession session, CancellationToken ct) =>
    Results.Ok(await session.Query<Budget>().Where(x => x.WorkOrderId == id).ToListAsync(ct).ConfigureAwait(false))).RequireAuthorization();
billing.MapGet("/work-orders/{id:guid}/payments", async (Guid id, IDocumentSession session, CancellationToken ct) =>
    Results.Ok(await session.Query<Payment>().Where(x => x.WorkOrderId == id).ToListAsync(ct).ConfigureAwait(false))).RequireAuthorization();
billing.MapPost("/webhooks/mercadopago", async (HttpRequest request, MercadoPagoOptions options, IPaymentGateway gateway, IDocumentSession session, IMartenOutbox bus, CancellationToken ct) =>
{
    var paymentId = request.Query["data.id"].ToString();
    if (!MercadoPagoWebhookSignature.IsValid(request.Headers["x-signature"].ToString(), request.Headers["x-request-id"].ToString(), paymentId, options.WebhookSecret))
        return Results.Unauthorized();
    var payment = await gateway.GetPaymentAsync(paymentId, ct).ConfigureAwait(false);
    await PaymentHandlers.ApplyAsync(payment, session, bus, ct).ConfigureAwait(false);
    return Results.Ok();
});
billing.MapGet("/approval-links/{raw}", async (string raw, IDocumentSession session, CancellationToken ct) =>
{
    var hash = ExternalAccessToken.ComputeHash(raw);
    var token = await session.Query<ExternalAccessToken>().FirstOrDefaultAsync(x => x.TokenHash == hash, ct).ConfigureAwait(false);
    if (token is null || token.Validate(raw).IsFailure) return Results.NotFound();
    var budget = await session.LoadAsync<Budget>(token.BudgetId, ct).ConfigureAwait(false);
    return budget is null ? Results.NotFound() : Results.Ok(budget);
});
billing.MapPost("/approval-links/{raw}", async (string raw, ApprovalDecision decision, IDocumentSession session, IMartenOutbox bus, CancellationToken ct) =>
{
    var hash = ExternalAccessToken.ComputeHash(raw);
    var token = await session.Query<ExternalAccessToken>().FirstOrDefaultAsync(x => x.TokenHash == hash, ct).ConfigureAwait(false);
    if (token is null || token.Validate(raw).IsFailure) return Results.NotFound();
    var budget = await session.LoadAsync<Budget>(token.BudgetId, ct).ConfigureAwait(false);
    var customer = await session.LoadAsync<BudgetCustomer>(token.BudgetId, ct).ConfigureAwait(false);
    if (budget is null || customer is null) return Results.NotFound();
    var now = DateTime.UtcNow;
    var result = decision.Approved ? budget.Approve(now) : budget.Reject(decision.Reason);
    if (result.IsFailure || token.Consume().IsFailure) return Results.BadRequest();
    session.Store(budget); session.Store(token);
    if (decision.Approved) await bus.PublishAsync(new BudgetApproved(budget.WorkOrderId, budget.Id, budget.TotalAmount, now, customer.CorrelationId)).ConfigureAwait(false);
    else await bus.PublishAsync(new BudgetRejected(budget.WorkOrderId, budget.Id, decision.Reason!, now, customer.CorrelationId)).ConfigureAwait(false);
    await session.SaveChangesAsync(ct).ConfigureAwait(false);
    return Results.Ok();
});
await app.RunAsync().ConfigureAwait(false);

public partial class Program;
