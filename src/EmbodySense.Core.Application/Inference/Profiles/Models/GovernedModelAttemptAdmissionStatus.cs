namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies current attempt eligibility and durable budget-reservation outcomes.</summary>
public enum GovernedModelAttemptAdmissionStatus
{
    /// <summary>The exact admitted primary is current and its maximum reservation is durable.</summary>
    Reserved = 1,
    /// <summary>An identical operation replayed the exact durable reservation.</summary>
    Replayed = 2,
    /// <summary>The operation already crossed dispatch or usage reconciliation and must not dispatch again.</summary>
    AlreadyAdvanced = 7,
    /// <summary>The request is malformed or attempts to repin.</summary>
    Invalid = 3,
    /// <summary>Current profile, authority, privacy, or budget evidence narrowed eligibility.</summary>
    Ineligible = 4,
    /// <summary>Operation reuse or optimistic state conflicts.</summary>
    Conflict = 5,
    /// <summary>Complete trusted current evidence is unavailable.</summary>
    Unavailable = 6,
    /// <summary>The atomic node-series or run-wide provider-usage ceiling is exhausted.</summary>
    BudgetExhausted = 8
}
