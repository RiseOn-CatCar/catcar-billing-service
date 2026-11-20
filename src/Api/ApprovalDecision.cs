namespace CatCar.Billing.Api;

/// <summary>Customer decision posted through the single-use approval link.</summary>
public sealed record ApprovalDecision(bool Approved, string? Reason);
