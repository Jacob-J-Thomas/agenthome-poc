using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Orchestrates exact effect-reconciliation cases without invoking the original actuator or a registered probe.</summary>
public interface IGovernedLoopEffectReconciliationService
{
    /// <summary>Opens one exact durable reconciliation-required effect.</summary>
    Task<GovernedLoopEffectReconciliationOperationResult> OpenAsync(GovernedLoopEffectReconciliationOpenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact immutable case without mutation.</summary>
    Task<GovernedLoopEffectReconciliationOperationResult> ReadAsync(GovernedLoopEffectReconciliationCaseReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Derives and appends one assessment from current authoritative observations.</summary>
    Task<GovernedLoopEffectReconciliationOperationResult> AssessAsync(GovernedLoopEffectReconciliationAssessmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Appends one legal disposition for the current assessment.</summary>
    Task<GovernedLoopEffectReconciliationOperationResult> DisposeAsync(GovernedLoopEffectReconciliationDispositionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Publishes one proof-backed immutable resolution and direct reconciled effect successor.</summary>
    Task<GovernedLoopEffectReconciliationOperationResult> ResolveAsync(GovernedLoopEffectReconciliationResolutionRequest request, CancellationToken cancellationToken = default);
}
