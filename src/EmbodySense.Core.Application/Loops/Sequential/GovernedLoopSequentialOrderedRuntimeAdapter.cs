using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Fences the existing durable ordered runtime behind exact canonical graph, admission, and dispatcher contracts.</summary>
/// <remarks>
/// This adapter does not admit, refresh, or re-resolve mutable graph or authority state. It only composes the caller's
/// guard-issued immutable anchor and builder-issued plan with the existing ordered runtime that remains responsible for
/// inference, governed tools, audit, cancellation, budgets, recovery, and idempotent terminal publication.
/// </remarks>
public sealed class GovernedLoopSequentialOrderedRuntimeAdapter : IGovernedLoopSequentialOrderedRuntime
{
    private readonly CustomLoopOrderedRunner _orderedRunner;
    private readonly IGovernedLoopSequentialRunEvidenceSource _evidenceSource;
    private readonly IGovernedLoopSequentialOrderedNodeEvidenceRecorder _nodeEvidenceRecorder;
    private readonly IGovernedLoopSequentialAuditRecorder _auditRecorder;

    /// <summary>Creates the canonical fence when one durable adapter implements both evidence protocols.</summary>
    public GovernedLoopSequentialOrderedRuntimeAdapter(
        CustomLoopOrderedRunner orderedRunner,
        IGovernedLoopSequentialRunEvidenceSource evidenceSource,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder nodeEvidenceRecorder)
        : this(
            orderedRunner,
            evidenceSource,
            nodeEvidenceRecorder,
            nodeEvidenceRecorder as IGovernedLoopSequentialAuditRecorder
                ?? throw new ArgumentException("The node-evidence recorder must also implement append-once sequential audits when no separate audit recorder is supplied.", nameof(nodeEvidenceRecorder)))
    {
    }

    /// <summary>Creates the canonical fence over the one existing ordered runtime.</summary>
    public GovernedLoopSequentialOrderedRuntimeAdapter(
        CustomLoopOrderedRunner orderedRunner,
        IGovernedLoopSequentialRunEvidenceSource evidenceSource,
        IGovernedLoopSequentialOrderedNodeEvidenceRecorder nodeEvidenceRecorder,
        IGovernedLoopSequentialAuditRecorder auditRecorder)
    {
        _orderedRunner = orderedRunner ?? throw new ArgumentNullException(nameof(orderedRunner));
        _evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
        _nodeEvidenceRecorder = nodeEvidenceRecorder ?? throw new ArgumentNullException(nameof(nodeEvidenceRecorder));
        _auditRecorder = auditRecorder ?? throw new ArgumentNullException(nameof(auditRecorder));
    }

    /// <summary>Starts exact canonical sequential execution without introducing another traversal runtime.</summary>
    public async Task<CustomLoopOrderedRunResult> RunAsync(
        GovernedLoopSequentialOrderedRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = await ValidatePersistedEvidenceAsync(request.Anchor, cancellationToken).ConfigureAwait(false);
        return failure ?? await _orderedRunner.RunSequentialAsync(request, _nodeEvidenceRecorder, _auditRecorder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Continues from an exact durable resume transition using the original immutable hand-off.</summary>
    public async Task<CustomLoopOrderedRunResult> ResumeAsync(
        GovernedLoopSequentialOrderedResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = await ValidatePersistedEvidenceAsync(request.Anchor, cancellationToken).ConfigureAwait(false);
        return failure ?? await _orderedRunner.ResumeSequentialAsync(request, _nodeEvidenceRecorder, _auditRecorder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-enters the same ordered runtime from exact retained Wait continuation evidence.</summary>
    public async Task<CustomLoopOrderedRunResult> ResumeWaitAsync(
        GovernedLoopSequentialOrderedWaitResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = await ValidatePersistedEvidenceAsync(request.Anchor, cancellationToken).ConfigureAwait(false);
        return failure ?? await _orderedRunner.ResumeWaitSequentialAsync(request, _nodeEvidenceRecorder, _auditRecorder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-enters the same ordered runtime from one exact retained retry dispatch or routed exhaustion.</summary>
    public async Task<CustomLoopOrderedRunResult> ResumeRetryAsync(
        GovernedLoopSequentialOrderedRetryResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = await ValidatePersistedEvidenceAsync(request.Anchor, cancellationToken).ConfigureAwait(false);
        return failure ?? await _orderedRunner.ResumeRetrySequentialAsync(request, _nodeEvidenceRecorder, _auditRecorder, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CustomLoopOrderedRunResult?> ValidatePersistedEvidenceAsync(
        GovernedLoopSequentialRunAnchor? anchor,
        CancellationToken cancellationToken)
    {
        if (anchor is null
            || !GovernedLoopSequentialContractValidator.Validate(anchor.AdapterBinding).IsValid
            || !GovernedLoopSequentialContractValidator.Validate(anchor.InvocationSnapshot).IsValid)
        {
            return Failure(CustomLoopOrderedRunStatus.InvalidState, "The canonical sequential anchor is invalid and no ordered runtime work was dispatched.");
        }

        GovernedLoopSequentialRunEvidence? retained;
        try
        {
            retained = await _evidenceSource.ResolveAsync(
                anchor.AdapterBinding.ExecutionBinding.RunId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(CustomLoopOrderedRunStatus.Failed, $"The immutable canonical run hand-off could not be loaded safely: {exception.GetType().Name}.");
        }

        if (retained is null
            || !GovernedLoopSequentialContractValidator.Validate(retained.AdapterBinding).IsValid
            || !GovernedLoopSequentialContractValidator.Validate(retained.InvocationSnapshot).IsValid
            || !string.Equals(retained.AdapterBinding.ContentHash, anchor.AdapterBinding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(retained.InvocationSnapshot.ContentHash, anchor.InvocationSnapshot.ContentHash, StringComparison.Ordinal))
        {
            return Failure(CustomLoopOrderedRunStatus.InvalidState, "The original immutable canonical admission and invocation hand-off is missing or mismatched; no ordered runtime work was dispatched.");
        }

        return null;
    }

    private static CustomLoopOrderedRunResult Failure(CustomLoopOrderedRunStatus status, string detail)
        => new(status, null, detail);
}
