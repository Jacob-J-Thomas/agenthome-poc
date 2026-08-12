using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

/// <summary>Configures bounded effect-authority evidence persistence and optional recovery observation.</summary>
public sealed class GovernedLoopEffectAuthorityEvidenceStoreOptions
{
    /// <summary>Maximum immutable decisions retained without eviction.</summary>
    public const int MaximumDecisions = 8_192;

    /// <summary>Maximum immutable target reservations retained without eviction.</summary>
    public const int MaximumTargetReservations = 8_192;

    /// <summary>Maximum immutable completion claims retained without eviction.</summary>
    public const int MaximumCompletionClaims = 8_192;

    /// <summary>Maximum UTF-8 bytes accepted for one authenticated decision ledger.</summary>
    public const int MaximumArtifactUtf8Bytes = 16 * 1024 * 1024;

    /// <summary>Gets the configured immutable decision ceiling.</summary>
    public int MaxDecisions { get; init; } = MaximumDecisions;

    /// <summary>Gets the configured immutable target-reservation ceiling.</summary>
    public int MaxTargetReservations { get; init; } = MaximumTargetReservations;

    /// <summary>Gets the configured immutable completion-claim ceiling.</summary>
    public int MaxCompletionClaims { get; init; } = MaximumCompletionClaims;

    /// <summary>Gets the configured authenticated-document byte ceiling.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = MaximumArtifactUtf8Bytes;

    /// <summary>Gets an optional observer invoked after each named durable boundary.</summary>
    /// <remarks>Observer failures model process loss and are never proof that a decision did not commit.</remarks>
    public Func<GovernedLoopEffectAuthorityPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked inside retained-handle path operations.</summary>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
