namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Identifies one closed, value-free durable lifecycle operation failure.</summary>
public enum GovernedLoopRevisionOperationFailureCode
{
    /// <summary>No supported failure code was supplied.</summary>
    Unknown = 0,
    /// <summary>The committed operation has no failure.</summary>
    None,
    /// <summary>The expected lifecycle version or exact head was stale.</summary>
    OptimisticStateConflict,
    /// <summary>The operation identifier was already bound to a different canonical request.</summary>
    OperationIntentConflict,
    /// <summary>The requested graph lifecycle does not exist.</summary>
    LifecycleNotFound,
    /// <summary>The requested immutable revision does not exist.</summary>
    RevisionNotFound,
    /// <summary>The requested exact publication does not exist.</summary>
    PublicationNotFound,
    /// <summary>The graph lifecycle is terminally archived.</summary>
    LifecycleArchived,
    /// <summary>The immutable revision artifact bound is exhausted.</summary>
    ArtifactLimitExceeded,
    /// <summary>The append-only operation-evidence bound is exhausted.</summary>
    EvidenceLimitExceeded,
    /// <summary>The finite optimistic lifecycle-version bound is exhausted.</summary>
    LifecycleVersionLimitExceeded,
    /// <summary>The durable outcome requires exact retry or reconciliation before it is known.</summary>
    OutcomeUnresolved
}
