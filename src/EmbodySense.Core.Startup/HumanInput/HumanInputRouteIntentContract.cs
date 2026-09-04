namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Defines the versioned server-owned contract for Human Input reroute alternatives.</summary>
public static class HumanInputRouteIntentContract
{
    /// <summary>The stable contract identifier for deterministic exclusion intents.</summary>
    public const string ContractId = "human-input.route-exclusion";

    /// <summary>The only supported route-intent contract version.</summary>
    public const int Version = 1;
}
