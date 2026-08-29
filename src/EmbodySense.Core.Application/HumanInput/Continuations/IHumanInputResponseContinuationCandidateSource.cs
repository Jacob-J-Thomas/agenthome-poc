using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Application.HumanInput.Continuations;

/// <summary>Pages canonical run state to discover Human Input checkpoints that may require idempotent response wake reconciliation.</summary>
public interface IHumanInputResponseContinuationCandidateSource
{
    /// <summary>Reads the next exclusive stable scan page without claiming, waking, or dispatching a continuation.</summary>
    /// <param name="maximumCount">The bounded number of checkpoint ordinals examined after at most one canonical run read.</param>
    /// <param name="scanCursor">The opaque exclusive prior source cursor, or null at the start of a fresh scan.</param>
    /// <param name="observedAtUtc">The trusted UTC observation instant used only to reject malformed calls.</param>
    /// <param name="cancellationToken">Cancels the bounded page read.</param>
    /// <returns>A detached page whose cursor never wraps and whose lower-key tail must be observed before a fresh scan.</returns>
    Task<HumanInputResponseContinuationRecoveryPage> ListCandidatesAsync(
        int maximumCount,
        string? scanCursor,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}
