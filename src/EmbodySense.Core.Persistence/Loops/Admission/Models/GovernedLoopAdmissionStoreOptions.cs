using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Loops.Admission.Models;

/// <summary>Configures bounded governed-loop admission persistence and optional recovery observation.</summary>
public sealed class GovernedLoopAdmissionStoreOptions
{
    /// <summary>Maximum immutable terminal outcomes retained without eviction.</summary>
    public const int MaximumTerminalOutcomes = 8_192;

    /// <summary>Maximum UTF-8 bytes accepted for one authenticated admission ledger.</summary>
    public const int MaximumArtifactUtf8Bytes = 16 * 1024 * 1024;

    /// <summary>Gets the configured immutable terminal-outcome ceiling.</summary>
    public int MaxTerminalOutcomes { get; init; } = MaximumTerminalOutcomes;

    /// <summary>Gets the configured authenticated-document byte ceiling.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = MaximumArtifactUtf8Bytes;

    /// <summary>Gets an optional observer invoked after each named durable boundary.</summary>
    /// <remarks>Observer failures model process loss and are never proof that an outcome did not commit.</remarks>
    public Func<GovernedLoopAdmissionPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked inside retained-handle path operations.</summary>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
