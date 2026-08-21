namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Represents explicit authoritative or unavailable token usage for one dimension.</summary>
public sealed record GovernedModelUsageMeasurement
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageMeasurement(GovernedModelUsageEvidenceStatus status, long value)
    {
        Status = status;
        Value = value;
    }

    /// <summary>Gets whether the value is authoritative or unavailable.</summary>
    public GovernedModelUsageEvidenceStatus Status { get; }
    /// <summary>Gets the authoritative value, or zero only when status is unavailable.</summary>
    public long Value { get; }
    /// <summary>Gets a canonical unavailable measurement.</summary>
    public static GovernedModelUsageMeasurement Unavailable { get; } = new(GovernedModelUsageEvidenceStatus.Unavailable, 0);

    /// <summary>Creates an authoritative nonnegative token measurement.</summary>
    public static GovernedModelUsageMeasurement Authoritative(long value) => new(GovernedModelUsageEvidenceStatus.Authoritative, GovernedModelContractRules.RequireQuantity(value, GovernedModelContractLimits.MaxTokens, nameof(value)));
}
