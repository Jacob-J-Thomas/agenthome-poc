using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Orchestrates exact effect-reconciliation cases without invoking the original actuator or a registered probe.</summary>
/// <remarks>
/// Implementations are stateless across calls and may be used concurrently. The canonical case store owns
/// compare-exchange serialization; dependency failures become closed operation statuses, while caller cancellation
/// is propagated before or during a dependency call. No method grants dispatch or retry eligibility.
/// </remarks>
public interface IGovernedLoopEffectReconciliationService
{
    /// <summary>Opens one exact durable reconciliation-required effect.</summary>
    /// <param name="request">The server-composed exact effect, frontier, contract, and evidence-source request.</param>
    /// <param name="cancellationToken">The caller token; cancellation is propagated and no mutation is attempted after cancellation.</param>
    /// <returns>An applied or replayed case result, or a closed status for invalid, unauthorized, stale, corrupt, or unavailable input.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    Task<GovernedLoopEffectReconciliationOperationResult> OpenAsync(GovernedLoopEffectReconciliationOpenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact immutable case without mutation.</summary>
    /// <param name="request">The exact immutable case reference to read.</param>
    /// <param name="cancellationToken">The caller token; cancellation is propagated during the canonical read.</param>
    /// <returns>A detached found case or a closed not-found, invalid, corrupt, or unavailable status.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    Task<GovernedLoopEffectReconciliationOperationResult> ReadAsync(GovernedLoopEffectReconciliationCaseReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Derives and appends one assessment from current authoritative observations.</summary>
    /// <param name="request">The exact expected case reference and bounded operator-safe detail.</param>
    /// <param name="cancellationToken">The caller token; cancellation is propagated and no compare-exchange is attempted after cancellation.</param>
    /// <returns>An applied or replayed immutable assessment, or a closed status when the case, current input, authority, or optimistic head is not exact.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    Task<GovernedLoopEffectReconciliationOperationResult> AssessAsync(GovernedLoopEffectReconciliationAssessmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Appends one legal disposition for the current assessment.</summary>
    /// <param name="request">The exact expected case reference, legal disposition, and bounded operator-safe detail.</param>
    /// <param name="cancellationToken">The caller token; cancellation is propagated and no compare-exchange is attempted after cancellation.</param>
    /// <returns>An applied or replayed immutable disposition, or a closed status when the case, assessment, authority, or optimistic head is not exact.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    Task<GovernedLoopEffectReconciliationOperationResult> DisposeAsync(GovernedLoopEffectReconciliationDispositionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Publishes one proof-backed immutable resolution and direct reconciled effect successor.</summary>
    /// <param name="request">The exact expected case reference and bounded operator-safe detail.</param>
    /// <param name="cancellationToken">The caller token; cancellation is propagated and no compare-exchange is attempted after cancellation.</param>
    /// <returns>An applied or replayed resolution with its reconciled successor, or a closed unresolved/conflict status.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    Task<GovernedLoopEffectReconciliationOperationResult> ResolveAsync(GovernedLoopEffectReconciliationResolutionRequest request, CancellationToken cancellationToken = default);
}
