using EmbodySense.Core.Common.Loops.Execution.Effects;

namespace EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;

/// <summary>Configures finite workspace-local effect-attempt evidence retention.</summary>
public sealed record GovernedLoopEffectAttemptStoreOptions
{
    /// <summary>Gets or initializes the maximum retained stable operation identities.</summary>
    public int MaxAttempts { get; init; } = 4_096;

    /// <summary>Gets or initializes the maximum canonical UTF-8 bytes retained for one attempt head.</summary>
    public int MaxRecordUtf8Bytes { get; init; } = GovernedLoopEffectAttemptContractLimits.MaxRecordUtf8Bytes;

    /// <summary>Gets or initializes the maximum canonical UTF-8 bytes retained across all attempt heads.</summary>
    public long MaxStoreUtf8Bytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Gets or initializes the maximum immutable protocol versions retained for one attempt.</summary>
    public int MaxVersionsPerAttempt { get; init; } = 8;
}
