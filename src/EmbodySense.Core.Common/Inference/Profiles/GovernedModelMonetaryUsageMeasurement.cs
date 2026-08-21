namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Represents explicit authoritative or unavailable monetary usage without conversion.</summary>
public sealed record GovernedModelMonetaryUsageMeasurement
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelMonetaryUsageMeasurement(GovernedModelUsageEvidenceStatus status, string? currency, long micros)
    {
        Status = status;
        Currency = currency;
        Micros = micros;
    }

    /// <summary>Gets whether the value is authoritative or unavailable.</summary>
    public GovernedModelUsageEvidenceStatus Status { get; }
    /// <summary>Gets the exact currency when authoritative.</summary>
    public string? Currency { get; }
    /// <summary>Gets integer micros when authoritative, or zero only when unavailable.</summary>
    public long Micros { get; }
    /// <summary>Gets a canonical unavailable monetary measurement.</summary>
    public static GovernedModelMonetaryUsageMeasurement Unavailable { get; } = new(GovernedModelUsageEvidenceStatus.Unavailable, null, 0);

    /// <summary>Creates an authoritative one-currency integer-micros measurement.</summary>
    public static GovernedModelMonetaryUsageMeasurement Authoritative(string currency, long micros)
        => new(GovernedModelUsageEvidenceStatus.Authoritative, GovernedModelContractRules.RequireCurrency(currency, nameof(currency)), GovernedModelContractRules.RequireQuantity(micros, GovernedModelContractLimits.MaxCurrencyMicros, nameof(micros)));
}
