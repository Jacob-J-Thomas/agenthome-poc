using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Enumerates bounded detached schedule, wake, and wake-reconciliation pages for the local coordinator.</summary>
/// <remarks>
/// Persistence implementations enumerate their durable catalogs and return validated detached values. A successful page
/// remains <c>Found</c> when more candidates exist and exposes that overload through its family-specific truncated-page
/// posture; actual persistence backpressure remains a separate failure status. Startup consumers must process the emitted
/// page before requesting its successor and must not scan persistence files directly. Trigger-queue candidates remain
/// owned by <c>ITriggerQueueQueryPort</c>.
/// </remarks>
public interface IGovernedLoopBackgroundWorkSource
{
    /// <summary>Reads the next stable page of at most <paramref name="pageMax"/> candidates from one family.</summary>
    Task<GovernedLoopBackgroundWorkReadResult?> ReadAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        int pageMax,
        CancellationToken cancellationToken = default);
}
