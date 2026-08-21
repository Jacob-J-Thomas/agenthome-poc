using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

/// <summary>Configures bounded authenticated model-usage ledger persistence.</summary>
public sealed class GovernedModelUsageLedgerStoreOptions
{
    /// <summary>The schema-1 maximum transitions in one authenticated workspace-ledger segment.</summary>
    public const int MaximumEntries = EmbodySense.Core.Common.Inference.Profiles.GovernedModelContractLimits.MaxWorkspaceUsageLedgerEntries;
    /// <summary>The schema-1 maximum transitions for one exact provider attempt.</summary>
    public const int MaximumEntriesPerAttempt = EmbodySense.Core.Common.Inference.Profiles.GovernedModelContractLimits.MaxUsageLedgerEntries;
    /// <summary>The schema-1 maximum authenticated ledger size.</summary>
    public const int MaximumArtifactUtf8Bytes = 16 * 1024 * 1024;

    /// <summary>Gets the configured segment transition ceiling before immutable authenticated rotation.</summary>
    public int MaxEntries { get; init; } = MaximumEntries;
    /// <summary>Gets the configured per-attempt transition ceiling.</summary>
    public int MaxEntriesPerAttempt { get; init; } = MaximumEntriesPerAttempt;
    /// <summary>Gets the configured authenticated-document byte ceiling.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = MaximumArtifactUtf8Bytes;
    /// <summary>Gets an optional observer invoked after durable append boundaries.</summary>
    public Func<GovernedModelUsageLedgerPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }
    /// <summary>Gets an optional retained-handle path observer.</summary>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
