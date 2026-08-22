namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies an append-only provider-usage transition outcome.</summary>
public enum GovernedModelUsageTransitionStatus
{
    /// <summary>The exact transition is durable.</summary>
    Applied = 1,
    /// <summary>The exact transition already exists.</summary>
    Replayed = 2,
    /// <summary>The transition shape or immutable binding is invalid.</summary>
    Invalid = 3,
    /// <summary>The requested transition conflicts with retained history.</summary>
    Conflict = 4,
    /// <summary>The retained state or durable outcome is unavailable.</summary>
    Unavailable = 5,
    /// <summary>Conflicting or over-reservation evidence was durably retained for attention.</summary>
    AttentionRequired = 6
}
