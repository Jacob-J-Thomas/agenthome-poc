namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

/// <summary>Identifies durable contextual-role publication boundaries exposed for recovery evaluation.</summary>
public enum ContextualRolePersistenceBoundary
{
    /// <summary>The trusted physical-workspace anchor became durable.</summary>
    AnchorPublished = 1,
    /// <summary>The immutable operation intent became durable.</summary>
    IntentPublished = 2,
    /// <summary>A new immutable role revision became durable.</summary>
    RevisionPublished = 3,
    /// <summary>The current lifecycle primary projection became durable.</summary>
    PrimaryPublished = 4,
    /// <summary>The immutable bounded lifecycle proof became durable.</summary>
    ProofPublished = 5,
    /// <summary>The immutable replay result became durable.</summary>
    ResultPublished = 6
}
