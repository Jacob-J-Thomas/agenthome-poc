namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

/// <summary>Configures bounded contextual-role persistence and optional durable-boundary observation.</summary>
public sealed class ContextualRoleRevisionStoreOptions
{
    /// <summary>Maximum immutable revision artifacts accepted by one workspace.</summary>
    public const int MaximumRevisionArtifacts = 4_096;
    /// <summary>Maximum immutable operation intents accepted by one workspace.</summary>
    public const int MaximumOperationArtifacts = 8_192;
    /// <summary>Maximum aggregate bytes accepted beneath the contextual-role store.</summary>
    public const long MaximumTotalArtifactBytes = 64L * 1024 * 1024;

    /// <summary>Gets the configured revision artifact ceiling.</summary>
    public int MaxRevisionArtifacts { get; init; } = MaximumRevisionArtifacts;
    /// <summary>Gets the configured operation artifact ceiling.</summary>
    public int MaxOperationArtifacts { get; init; } = MaximumOperationArtifacts;
    /// <summary>Gets the configured aggregate byte ceiling.</summary>
    public long MaxTotalArtifactBytes { get; init; } = MaximumTotalArtifactBytes;
    /// <summary>Gets an optional observer invoked after each named boundary is durably published.</summary>
    /// <remarks>Observers support deterministic crash-window evaluation. Exceptions are treated exactly like failures after the corresponding durable boundary.</remarks>
    public Func<ContextualRolePersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }
    /// <summary>Gets an optional observer invoked inside guarded physical read, validation-enumeration, and publication windows.</summary>
    /// <remarks>Observers support deterministic path-race, enumeration-race, and pre-directory-barrier crash evaluation. Production callers should normally leave this unset.</remarks>
    public Func<ContextualRolePhysicalPersistenceBoundary, CancellationToken, ValueTask>? PhysicalBoundaryObserver { get; init; }
}
