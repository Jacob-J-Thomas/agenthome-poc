using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

/// <summary>Configures finite immutable graph-authoring persistence bounds and deterministic observers.</summary>
public sealed class GovernedLoopGraphRevisionStoreOptions
{
    /// <summary>Maximum UTF-8 bytes accepted for one canonical graph artifact.</summary>
    public const int MaximumArtifactUtf8Bytes = 128 * 1024 * 1024;

    /// <summary>Maximum UTF-8 bytes accepted for one full-authoring intent.</summary>
    public const int MaximumIntentUtf8Bytes = 32 * 1024;

    /// <summary>Maximum aggregate artifact and intent bytes inspected by one bounded workspace.</summary>
    public const long MaximumWorkspaceUtf8Bytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Maximum immutable graph artifacts retained in one workspace.</summary>
    public const int MaximumArtifacts = 4_096;

    /// <summary>Maximum immutable authoring intents retained in one workspace.</summary>
    public const int MaximumIntents = 8_192;

    /// <summary>Maximum graph-id directories accepted during bounded discovery.</summary>
    public const int MaximumGraphDirectories = 1_024;

    /// <summary>Maximum inert or ready immutable-write staging entries retained after process loss.</summary>
    public const int MaximumStagingEntries = 256;

    /// <summary>Gets the configured graph-artifact byte bound.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = MaximumArtifactUtf8Bytes;

    /// <summary>Gets the configured intent byte bound.</summary>
    public int MaxIntentUtf8Bytes { get; init; } = MaximumIntentUtf8Bytes;

    /// <summary>Gets the configured aggregate workspace byte bound.</summary>
    public long MaxWorkspaceUtf8Bytes { get; init; } = MaximumWorkspaceUtf8Bytes;

    /// <summary>Gets the configured immutable-artifact count bound.</summary>
    public int MaxArtifacts { get; init; } = MaximumArtifacts;

    /// <summary>Gets the configured immutable-intent count bound.</summary>
    public int MaxIntents { get; init; } = MaximumIntents;

    /// <summary>Gets the configured graph-directory count bound.</summary>
    public int MaxGraphDirectories { get; init; } = MaximumGraphDirectories;

    /// <summary>Gets the configured bounded immutable-write staging-entry count.</summary>
    public int MaxStagingEntries { get; init; } = MaximumStagingEntries;

    /// <summary>Gets an optional durable-boundary observer used by deterministic crash tests.</summary>
    public Func<GovernedLoopGraphRevisionPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional retained-handle path observer used by substitution tests.</summary>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
