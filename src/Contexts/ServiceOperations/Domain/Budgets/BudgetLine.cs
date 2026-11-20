namespace CatCar.Contexts.ServiceOperations.Domain.Budgets;

using System.Text.Json.Serialization;

/// <summary>
/// A single frozen line item of a <see cref="Budget"/>. Snapshot of description/price/quantity at issuance -
/// immutable after the Budget is issued (AC: orçamento gerado automaticamente com snapshot de preços).
/// </summary>
public sealed class BudgetLine
{
    [JsonInclude]
    public Guid Id { get; private set; }

    [JsonInclude]
    public BudgetLineType Type { get; private set; }

    [JsonInclude]
    public Guid ReferenceId { get; private set; }

    [JsonInclude]
    public string Description { get; private set; }

    [JsonInclude]
    public decimal UnitPrice { get; private set; }

    [JsonInclude]
    public int Quantity { get; private set; }

    public decimal LineTotal => UnitPrice * Quantity;

    internal BudgetLine(BudgetLineType type, Guid referenceId, string description, decimal unitPrice, int quantity)
    {
        Id = Guid.CreateVersion7();
        Type = type;
        ReferenceId = referenceId;
        Description = description;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

#pragma warning disable CS8618
    [JsonConstructor]
    private BudgetLine()
    {
    }
#pragma warning restore CS8618
}
