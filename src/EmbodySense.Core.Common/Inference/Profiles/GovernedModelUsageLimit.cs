namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Represents an explicit unbounded or positive hard token ceiling.</summary>
public sealed record GovernedModelUsageLimit
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageLimit(bool isBounded, long maximum)
    {
        IsBounded = isBounded;
        Maximum = maximum;
    }

    /// <summary>Gets whether a hard ceiling is present.</summary>
    public bool IsBounded { get; }
    /// <summary>Gets the positive maximum when bounded, or zero when unbounded.</summary>
    public long Maximum { get; }
    /// <summary>Gets an explicit unbounded limit.</summary>
    public static GovernedModelUsageLimit Unbounded { get; } = new(false, 0);
    /// <summary>Creates a positive hard ceiling.</summary>
    public static GovernedModelUsageLimit Bounded(long maximum) => new(true, GovernedModelContractRules.RequireQuantity(maximum, GovernedModelContractLimits.MaxTokens, nameof(maximum), positive: true));
}
