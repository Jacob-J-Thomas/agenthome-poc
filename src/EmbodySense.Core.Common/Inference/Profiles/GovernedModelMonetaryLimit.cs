namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Represents an explicit unbounded or positive one-currency hard monetary ceiling.</summary>
public sealed record GovernedModelMonetaryLimit
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelMonetaryLimit(bool isBounded, string? currency, long maximumMicros)
    {
        IsBounded = isBounded;
        Currency = currency;
        MaximumMicros = maximumMicros;
    }

    /// <summary>Gets whether a hard monetary ceiling is present.</summary>
    public bool IsBounded { get; }
    /// <summary>Gets the exact currency when bounded.</summary>
    public string? Currency { get; }
    /// <summary>Gets the positive integer-micros ceiling when bounded, or zero when unbounded.</summary>
    public long MaximumMicros { get; }
    /// <summary>Gets an explicit unbounded monetary limit.</summary>
    public static GovernedModelMonetaryLimit Unbounded { get; } = new(false, null, 0);
    /// <summary>Creates a positive one-currency hard monetary ceiling.</summary>
    public static GovernedModelMonetaryLimit Bounded(string currency, long maximumMicros)
        => new(true, GovernedModelContractRules.RequireCurrency(currency, nameof(currency)), GovernedModelContractRules.RequireQuantity(maximumMicros, GovernedModelContractLimits.MaxCurrencyMicros, nameof(maximumMicros), positive: true));
}
