namespace CatCar.Contexts.ServiceOperations.Domain.Budgets;

/// <summary>
/// Lifecycle status of a Budget. Active -> Approved | Rejected -> Replaced. Cancelled is set by the saga compensation (CancelBudget).
/// </summary>
public enum BudgetStatus
{
    Active,
    Approved,
    Rejected,
    Replaced,
    Cancelled
}
