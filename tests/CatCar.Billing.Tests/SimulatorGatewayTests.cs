using CatCar.Billing.Api.Payments;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CatCar.Billing.Tests;

public sealed class SimulatorGatewayTests
{
    [Fact]
    public void ValidateProvider_ProductionSimulatorWithoutOverride_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => SimulatorEndpoints.ValidateProvider("Simulator", true, false));
        Assert.Throws<InvalidOperationException>(() => SimulatorEndpoints.ValidateProvider("unknown", false, false));
        SimulatorEndpoints.ValidateProvider("Simulator", true, true);
        SimulatorEndpoints.ValidateProvider("MercadoPago", true, false);
    }

    [Fact]
    public async Task CreatePreference_WithReference_ReturnsClearlySimulatedCheckout()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:Simulator:BaseUrl"] = "http://billing.test/" }).Build();
        var gateway = new SimulatorGateway(configuration);
        var id = Guid.NewGuid().ToString();
        var preference = await gateway.CreatePreferenceAsync(id, 100m, "client@example.test");
        Assert.Equal($"SIM-{id}", preference.Id);
        Assert.Equal($"http://billing.test/simulator/payments/{id}", preference.CheckoutUrl);
        Assert.Empty((await gateway.FindPaymentsAsync(id)).Payments);
        await gateway.RefundAsync(preference.Id, id);
    }
}
