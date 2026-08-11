namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

/// <summary>Identifies durable graph-authoring boundaries exposed for crash/restart verification.</summary>
public enum GovernedLoopGraphRevisionPersistenceBoundary
{
    /// <summary>An immutable graph payload was durably published.</summary>
    ArtifactPublished = 1,
    /// <summary>The full authoring intent was durably published.</summary>
    IntentPublished = 2,
    /// <summary>The server-owned graph-authoring trust anchor advanced to the intent.</summary>
    TrustAdvanced = 3,
    /// <summary>The graph payload and intent were proved immediately before lifecycle commit.</summary>
    LifecycleCommitStarting = 4,
}
