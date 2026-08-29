using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Application.HumanInput.Continuations;

/// <summary>Submits one exact discovered Human Input response candidate through the canonical continuation boundary.</summary>
/// <remarks>
/// Implementations must retain or reconcile the exact durable wake and ordered-continuation outcome before reporting a
/// submitted, replayed, or retired result. This port owns no response discovery, worker lifetime, queue, lease, or wake
/// ledger.
/// </remarks>
public interface IHumanInputResponseContinuationWakePort
{
    /// <summary>Processes one exact canonical candidate without accepting response content from the caller.</summary>
    /// <param name="candidate">The reread canonical run and Human Input checkpoint identity.</param>
    /// <param name="cancellationToken">Cancels before a durable continuation outcome is established.</param>
    /// <returns>The closed durable continuation disposition.</returns>
    Task<HumanInputResponseContinuationWakeResult> WakeAsync(
        HumanInputResponseContinuationCandidate? candidate,
        CancellationToken cancellationToken = default);
}
