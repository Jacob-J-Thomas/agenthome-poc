using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Reads a bounded current effect-certainty snapshot from canonical server-owned state without resuming, claiming, mutating, or dispatching an effect.</summary>
public interface IGovernedLoopEffectCertaintySnapshotSource
{
    /// <summary>Reads the current exact certainty for one immutable effect-attempt identity and preparation expectation.</summary>
    /// <remarks>Implementations must re-read canonical durable truth and return a detached result. They must never call a transition or lease-acquisition API to serve this query, echo untrusted fields as proof, or expose raw effect payloads.</remarks>
    /// <param name="query">The exact identity and value-free preparation expected by the Human Review continuation.</param>
    /// <param name="cancellationToken">Cancels the read before it completes.</param>
    /// <returns>A closed missing, corrupt, unavailable, stale, or current certainty result.</returns>
    Task<GovernedLoopEffectCertaintySnapshotResult> ReadAsync(GovernedLoopEffectCertaintySnapshotQuery query, CancellationToken cancellationToken = default);
}
