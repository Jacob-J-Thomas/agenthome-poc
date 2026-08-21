using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Executes typed operational controls under current authority and durable before-mutation receipts.</summary>
/// <remarks>Run controls delegate to <see cref="CustomLoopLifecycleService"/>. Queue and schedule controls mutate only their canonical stores. Bounded batches persist their exact target set and each progress successor for restart reconciliation.</remarks>
public sealed class GovernedLoopOperationalControlService : IGovernedLoopOperationalController
{
    private readonly IGovernedLoopOperationalControlAuthorityPort _authority;
    private readonly ICustomLoopLifecycleControlPort _lifecycle;
    private readonly ITriggerQueueCancellationPort _queueCancellation;
    private readonly ITriggerQueueQueryPort _queueQuery;
    private readonly IGovernedLoopOperationalControlReceiptStore _receipts;
    private readonly ICustomLoopRunStore _runs;
    private readonly IScheduleStorePort _schedules;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one application-owned control orchestrator over exact runtime ports.</summary>
    public GovernedLoopOperationalControlService(
        IGovernedLoopOperationalControlAuthorityPort authority,
        IGovernedLoopOperationalControlReceiptStore receipts,
        ITriggerQueueQueryPort queueQuery,
        ITriggerQueueCancellationPort queueCancellation,
        IScheduleStorePort schedules,
        ICustomLoopRunStore runs,
        ICustomLoopLifecycleControlPort lifecycle,
        TimeProvider? timeProvider = null)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _queueQuery = queueQuery ?? throw new ArgumentNullException(nameof(queueQuery));
        _queueCancellation = queueCancellation ?? throw new ArgumentNullException(nameof(queueCancellation));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Executes or exactly replays one caller-owned operation identity.</summary>
    public async Task<GovernedLoopOperationalControlResult> ExecuteAsync(
        GovernedLoopOperationalControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopOperationalContract.IsValid(request))
        {
            return Result(GovernedLoopOperationalControlStatus.Invalid, request?.OperationId ?? string.Empty, request?.Kind ?? default, request?.TargetId ?? string.Empty, "operational-control-request-invalid");
        }

        GovernedLoopOperationalControlAuthority? authority;
        try
        {
            authority = await _authority.ReadAsync(request!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopOperationalControlStatus.Unavailable, request!.OperationId, request.Kind, request.TargetId, "operational-control-authority-unavailable");
        }

        if (!GovernedLoopOperationalContract.IsValid(authority))
        {
            return Result(GovernedLoopOperationalControlStatus.Corrupt, request.OperationId, request.Kind, request.TargetId, "operational-control-authority-corrupt");
        }
        if (!authority!.Permitted
            || !string.Equals(authority.WorkspaceId, request!.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(authority.ActorId, request.ActorId, StringComparison.Ordinal)
            || !string.Equals(authority.SurfaceId, request.SurfaceId, StringComparison.Ordinal)
            || !string.Equals(authority.EvidenceHash, request.ExpectedAuthorityEvidenceHash, StringComparison.Ordinal))
        {
            return Result(GovernedLoopOperationalControlStatus.Unauthorized, request.OperationId, request.Kind, request.TargetId, "operational-control-authority-stale-or-denied");
        }

        var requestHash = GovernedLoopOperationalHash.Request(request);
        var requestedAtUtc = UtcNow(authority.ObservedAtUtc);
        if (requestedAtUtc is null)
        {
            return Result(GovernedLoopOperationalControlStatus.Corrupt, request.OperationId, request.Kind, request.TargetId, "operational-control-clock-corrupt");
        }
        var pending = GovernedLoopOperationalControlReceiptFactory.Create(
            request,
            requestHash,
            authority,
            requestedAtUtc.Value,
            requestedAtUtc.Value,
            GovernedLoopOperationalControlReceiptState.Pending,
            GovernedLoopOperationalControlStatus.OperationInProgress,
            "operational-control-pending",
            Array.AsReadOnly(Array.Empty<GovernedLoopOperationalControlProgress>()));
        GovernedLoopOperationalControlReceiptStoreResult begun;
        try
        {
            begun = await _receipts.BeginAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopOperationalControlStatus.Unavailable, request.OperationId, request.Kind, request.TargetId, "operational-control-receipt-unavailable");
        }

        if (begun.Status != GovernedLoopOperationalControlReceiptStoreStatus.Committed
            && begun.Status != GovernedLoopOperationalControlReceiptStoreStatus.Replayed)
        {
            return Result(Map(begun.Status), request.OperationId, request.Kind, request.TargetId, StoreReason(begun.Status));
        }
        if (begun.Receipt is { } replayed
            && replayed.State is GovernedLoopOperationalControlReceiptState.Complete or GovernedLoopOperationalControlReceiptState.NeedsReview)
        {
            return FromReceipt(replayed, replay: true);
        }

        using var lease = begun.Lease;
        if (lease is null || begun.Receipt is not { } current)
        {
            return Result(GovernedLoopOperationalControlStatus.OperationInProgress, request.OperationId, request.Kind, request.TargetId, "operational-control-operation-in-progress");
        }

        if (!string.Equals(current.RequestHash, requestHash, StringComparison.Ordinal)
            || !string.Equals(current.AuthorityEvidenceHash, authority.EvidenceHash, StringComparison.Ordinal))
        {
            return Result(GovernedLoopOperationalControlStatus.Conflict, request.OperationId, request.Kind, request.TargetId, "operational-control-operation-collision");
        }

        GovernedLoopOperationalControlAuthority? currentAuthority;
        try
        {
            currentAuthority = await _authority.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(current, GovernedLoopOperationalControlStatus.Unavailable, "operational-control-authority-revalidation-unavailable", CancellationToken.None).ConfigureAwait(false);
        }

        if (!GovernedLoopOperationalContract.IsValid(currentAuthority))
        {
            return await CompleteAsync(current, GovernedLoopOperationalControlStatus.Corrupt, "operational-control-authority-revalidation-corrupt", CancellationToken.None).ConfigureAwait(false);
        }
        if (!currentAuthority!.Permitted
            || !string.Equals(currentAuthority.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(currentAuthority.ActorId, request.ActorId, StringComparison.Ordinal)
            || !string.Equals(currentAuthority.SurfaceId, request.SurfaceId, StringComparison.Ordinal)
            || !string.Equals(currentAuthority.EvidenceHash, authority.EvidenceHash, StringComparison.Ordinal)
            || !string.Equals(currentAuthority.EvidenceHash, request.ExpectedAuthorityEvidenceHash, StringComparison.Ordinal))
        {
            return await CompleteAsync(current, GovernedLoopOperationalControlStatus.Unauthorized, "operational-control-authority-changed-before-effect", CancellationToken.None).ConfigureAwait(false);
        }

        return request.Kind switch
        {
            GovernedLoopOperationalControlKind.PauseRun
                or GovernedLoopOperationalControlKind.CancelRun
                or GovernedLoopOperationalControlKind.ResumeRun => await ExecuteRunAsync(request, current, begun.Status == GovernedLoopOperationalControlReceiptStoreStatus.Committed, cancellationToken).ConfigureAwait(false),
            GovernedLoopOperationalControlKind.DisableSchedule
                or GovernedLoopOperationalControlKind.EnableSchedule => await ExecuteScheduleAsync(request, current, begun.Status == GovernedLoopOperationalControlReceiptStoreStatus.Committed, cancellationToken).ConfigureAwait(false),
            GovernedLoopOperationalControlKind.CancelDelivery => await ExecuteDeliveryAsync(request, current, cancellationToken).ConfigureAwait(false),
            GovernedLoopOperationalControlKind.CancelPendingDeliveries => await ExecuteBatchAsync(request, current, cancellationToken).ConfigureAwait(false),
            _ => await CompleteAsync(current, GovernedLoopOperationalControlStatus.Invalid, "operational-control-kind-invalid", cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<GovernedLoopOperationalControlResult> ExecuteRunAsync(
        GovernedLoopOperationalControlRequest request,
        GovernedLoopOperationalControlReceipt receipt,
        bool isFresh,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedRevision > int.MaxValue)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Invalid, "run-control-revision-invalid", cancellationToken).ConfigureAwait(false);
        }

        if (!isFresh)
        {
            return await DelegateRunControlAsync(request, receipt, cancellationToken).ConfigureAwait(false);
        }

        CustomLoopRunMonitor? monitor;
        try
        {
            monitor = await _runs.GetMonitorAsync(request.TargetId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Unavailable, "run-control-source-unavailable", cancellationToken).ConfigureAwait(false);
        }
        if (monitor is null)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.NotFound, "run-control-not-found", cancellationToken).ConfigureAwait(false);
        }
        if (!GovernedLoopOperationalContract.IsHash(monitor.ArtifactHash))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Corrupt, "run-control-evidence-corrupt", cancellationToken).ConfigureAwait(false);
        }
        if (monitor.Summary.LifecycleVersion != request.ExpectedRevision
            || !string.Equals(monitor.ArtifactHash, request.ExpectedEvidenceHash, StringComparison.Ordinal))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Conflict, "run-control-revision-conflict", cancellationToken, monitor.Summary.LifecycleVersion, monitor.ArtifactHash).ConfigureAwait(false);
        }
        if (!CustomLoopLifecycleControlEligibility.IsEligible(request.Kind, monitor.Summary.Status))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Ineligible, "run-control-lifecycle-ineligible", cancellationToken, monitor.Summary.LifecycleVersion, monitor.ArtifactHash).ConfigureAwait(false);
        }

        return await DelegateRunControlAsync(request, receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopOperationalControlResult> DelegateRunControlAsync(
        GovernedLoopOperationalControlRequest request,
        GovernedLoopOperationalControlReceipt receipt,
        CancellationToken cancellationToken)
    {
        CustomLoopControlResult controlled;
        try
        {
            controlled = request.Kind switch
            {
                GovernedLoopOperationalControlKind.PauseRun => await _lifecycle.PauseAsync(new CustomLoopPauseRequest(request.TargetId, checked((int)request.ExpectedRevision), request.OperationId, request.ActorId), cancellationToken).ConfigureAwait(false),
                GovernedLoopOperationalControlKind.CancelRun => await _lifecycle.CancelAsync(new CustomLoopCancelRequest(request.TargetId, checked((int)request.ExpectedRevision), request.OperationId, request.ActorId), cancellationToken).ConfigureAwait(false),
                _ => await _lifecycle.ResumeAsync(new CustomLoopResumeRequest(request.TargetId, checked((int)request.ExpectedRevision), request.OperationId, request.ActorId), cancellationToken).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Unavailable, "run-control-unavailable", CancellationToken.None).ConfigureAwait(false);
        }
        var status = Map(controlled.Status);
        return await CompleteAsync(
            receipt,
            status,
            "run-control-" + Token(controlled.Status),
            CancellationToken.None,
            controlled.Run?.LifecycleVersion,
            null).ConfigureAwait(false);
    }

    private async Task<GovernedLoopOperationalControlResult> ExecuteScheduleAsync(
        GovernedLoopOperationalControlRequest request,
        GovernedLoopOperationalControlReceipt receipt,
        bool isFresh,
        CancellationToken cancellationToken)
    {
        if (!ScheduleId.TryParse(request.TargetId, out var scheduleId))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Invalid, "schedule-control-request-invalid", cancellationToken).ConfigureAwait(false);
        }
        ScheduleStoreReadResult read;
        try
        {
            read = await _schedules.ReadAsync(scheduleId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Unavailable, "schedule-control-unavailable", cancellationToken).ConfigureAwait(false);
        }
        if (read.Status != ScheduleStoreReadStatus.Found || read.Definition is null || read.State is null)
        {
            return await CompleteAsync(receipt, Map(read.Status), "schedule-control-" + Token(read.Status), cancellationToken).ConfigureAwait(false);
        }
        if (!ScheduleContractValidator.ValidateDefinitionStateComposition(read.Definition, read.State).IsValid
            || !ScheduleContractHash.TryComputeState(read.State, out var currentHash, out _))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Corrupt, "schedule-control-corrupt", cancellationToken).ConfigureAwait(false);
        }
        var enabled = request.Kind == GovernedLoopOperationalControlKind.EnableSchedule;
        if (!isFresh
            && read.State.StateRevision == request.ExpectedRevision + 1
            && read.State.Enabled == enabled
            && receipt.State == GovernedLoopOperationalControlReceiptState.Pending)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.NeedsReview, "schedule-control-outcome-ambiguous", cancellationToken, read.State.StateRevision, currentHash).ConfigureAwait(false);
        }
        if (read.State.StateRevision != request.ExpectedRevision
            || !string.Equals(currentHash, request.ExpectedEvidenceHash, StringComparison.Ordinal))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Conflict, "schedule-control-revision-conflict", cancellationToken, read.State.StateRevision, currentHash).ConfigureAwait(false);
        }
        if (enabled && !read.Definition.Enabled)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Ineligible, "schedule-control-definition-disabled", cancellationToken, read.State.StateRevision, currentHash).ConfigureAwait(false);
        }
        if (read.State.Enabled == enabled)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Ineligible, "schedule-control-already-in-state", cancellationToken, read.State.StateRevision, currentHash).ConfigureAwait(false);
        }
        if (read.State.StateRevision >= ScheduleContractLimits.MaxRevision)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Corrupt, "schedule-control-revision-exhausted", cancellationToken).ConfigureAwait(false);
        }
        var replacement = read.State with { StateRevision = read.State.StateRevision + 1, Enabled = enabled };
        ScheduleStoreMutationResult mutation;
        try
        {
            mutation = await _schedules.CompareExchangeAsync(new ScheduleStateCompareExchange(read.State, replacement), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Unavailable, "schedule-control-unavailable", CancellationToken.None).ConfigureAwait(false);
        }
        if (!TryValidateScheduleMutation(
                read.Definition,
                read.State,
                replacement,
                mutation,
                out var mutationStatus,
                out var mutationReason,
                out var mutationRevision,
                out var mutationHash))
        {
            return await CompleteAsync(
                receipt,
                GovernedLoopOperationalControlStatus.Corrupt,
                "schedule-control-mutation-evidence-corrupt",
                CancellationToken.None,
                mutationRevision,
                mutationHash).ConfigureAwait(false);
        }
        if (mutation.ExactReplay)
        {
            return await CompleteAsync(
                receipt,
                GovernedLoopOperationalControlStatus.NeedsReview,
                "schedule-control-outcome-ambiguous",
                CancellationToken.None,
                mutationRevision,
                mutationHash).ConfigureAwait(false);
        }
        return await CompleteAsync(
            receipt,
            mutationStatus,
            mutationReason,
            CancellationToken.None,
            mutationRevision,
            mutationHash).ConfigureAwait(false);
    }

    private async Task<GovernedLoopOperationalControlResult> ExecuteDeliveryAsync(
        GovernedLoopOperationalControlRequest request,
        GovernedLoopOperationalControlReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!TriggerDeliveryId.TryParse(request.TargetId, out var deliveryId))
        {
            return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Invalid, "delivery-control-request-invalid", cancellationToken).ConfigureAwait(false);
        }
        var progress = receipt.Progress.Count == 0
            ? new GovernedLoopOperationalControlProgress(request.TargetId, request.ExpectedRevision, request.ExpectedEvidenceHash, GovernedLoopOperationalControlStatus.OperationInProgress, null, null, "delivery-control-captured")
            : receipt.Progress[0];
        if (receipt.Progress.Count == 0)
        {
            var captured = GovernedLoopOperationalControlReceiptFactory.Successor(
                receipt,
                UtcNow(receipt.UpdatedAtUtc) ?? receipt.UpdatedAtUtc,
                GovernedLoopOperationalControlReceiptState.Mutating,
                GovernedLoopOperationalControlStatus.OperationInProgress,
                "delivery-control-target-captured",
                Array.AsReadOnly(new[] { progress }));
            var stored = await StoreAsync(receipt.ContentHash, captured, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return Result(GovernedLoopOperationalControlStatus.NeedsReview, request.OperationId, request.Kind, request.TargetId, "delivery-control-capture-ambiguous");
            }
            receipt = stored;
        }
        var reconciled = await ApplyDeliveryAsync(deliveryId!, progress, cancellationToken).ConfigureAwait(false);
        var outcome = reconciled.Status;
        var state = outcome == GovernedLoopOperationalControlStatus.NeedsReview
            ? GovernedLoopOperationalControlReceiptState.NeedsReview
            : GovernedLoopOperationalControlReceiptState.Complete;
        var terminal = GovernedLoopOperationalControlReceiptFactory.Successor(
            receipt,
            UtcNow(receipt.UpdatedAtUtc) ?? receipt.UpdatedAtUtc,
            state,
            outcome,
            reconciled.ReasonCode,
            Array.AsReadOnly(new[] { reconciled }));
        var completed = await StoreAsync(receipt.ContentHash, terminal, CancellationToken.None).ConfigureAwait(false);
        return completed is null
            ? Result(GovernedLoopOperationalControlStatus.NeedsReview, request.OperationId, request.Kind, request.TargetId, "delivery-control-outcome-ambiguous")
            : FromReceipt(completed, replay: false);
    }

    private async Task<GovernedLoopOperationalControlResult> ExecuteBatchAsync(
        GovernedLoopOperationalControlRequest request,
        GovernedLoopOperationalControlReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.State == GovernedLoopOperationalControlReceiptState.Pending)
        {
            TriggerQueueSnapshot snapshot;
            try
            {
                var observedAtUtc = UtcNow(receipt.UpdatedAtUtc);
                if (observedAtUtc is null)
                {
                    return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Corrupt, "delivery-batch-clock-corrupt", cancellationToken).ConfigureAwait(false);
                }
                snapshot = await _queueQuery.GetSnapshotAsync(observedAtUtc.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Unavailable, "delivery-batch-source-unavailable", cancellationToken).ConfigureAwait(false);
            }
            if (!TriggerQueueSnapshotEvidenceContract.IsValid(snapshot))
            {
                return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Corrupt, "delivery-batch-evidence-corrupt", cancellationToken).ConfigureAwait(false);
            }
            var catalogHash = GovernedLoopOperationalHash.QueueCatalog(snapshot.Generation, snapshot.QueuedEntries, snapshot.QueuedReservationBytes, snapshot.RetainedEntries, snapshot.RetainedReservationBytes, snapshot.PersistenceBackpressured);
            if (snapshot.Generation != request.ExpectedRevision || !string.Equals(catalogHash, request.ExpectedEvidenceHash, StringComparison.Ordinal))
            {
                return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Conflict, "delivery-batch-revision-conflict", cancellationToken, snapshot.Generation, catalogHash).ConfigureAwait(false);
            }
            var targets = snapshot.Entries
                .Where(item => string.Equals(item.LoopId, request.TargetId, StringComparison.Ordinal) && IsNonterminal(item.State))
                .OrderBy(item => item.DeliveryId.Value, StringComparer.Ordinal)
                .ToArray();
            if (targets.Length > request.MaximumBatchItems)
            {
                return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Backpressured, "delivery-batch-bound-exceeded", cancellationToken, snapshot.Generation, catalogHash).ConfigureAwait(false);
            }
            if (targets.Length == 0)
            {
                return await CompleteAsync(receipt, GovernedLoopOperationalControlStatus.Applied, "delivery-batch-empty", cancellationToken, snapshot.Generation, catalogHash).ConfigureAwait(false);
            }
            var progress = Array.AsReadOnly(targets.Select(item => new GovernedLoopOperationalControlProgress(
                item.DeliveryId.Value,
                item.Revision,
                item.CanonicalEnvelopeHash,
                GovernedLoopOperationalControlStatus.OperationInProgress,
                null,
                null,
                "delivery-batch-target-captured")).ToArray());
            var captured = GovernedLoopOperationalControlReceiptFactory.Successor(
                receipt,
                UtcNow(receipt.UpdatedAtUtc) ?? receipt.UpdatedAtUtc,
                GovernedLoopOperationalControlReceiptState.Mutating,
                GovernedLoopOperationalControlStatus.OperationInProgress,
                "delivery-batch-targets-captured",
                progress);
            var stored = await StoreAsync(receipt.ContentHash, captured, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return Result(GovernedLoopOperationalControlStatus.NeedsReview, request.OperationId, request.Kind, request.TargetId, "delivery-batch-capture-ambiguous");
            }
            receipt = stored;
        }

        var current = receipt;
        for (var index = 0; index < current.Progress.Count; index++)
        {
            if (current.Progress[index].Status != GovernedLoopOperationalControlStatus.OperationInProgress)
            {
                continue;
            }
            if (!TriggerDeliveryId.TryParse(current.Progress[index].TargetId, out var deliveryId))
            {
                return await CompleteAsync(current, GovernedLoopOperationalControlStatus.Corrupt, "delivery-batch-target-corrupt", CancellationToken.None).ConfigureAwait(false);
            }
            var progress = await ApplyDeliveryAsync(deliveryId!, current.Progress[index], cancellationToken).ConfigureAwait(false);
            var all = current.Progress.Select((item, position) => position == index ? progress : item).ToArray();
            var successor = GovernedLoopOperationalControlReceiptFactory.Successor(
                current,
                UtcNow(current.UpdatedAtUtc) ?? current.UpdatedAtUtc,
                GovernedLoopOperationalControlReceiptState.Mutating,
                GovernedLoopOperationalControlStatus.OperationInProgress,
                "delivery-batch-progress-retained",
                Array.AsReadOnly(all));
            var stored = await StoreAsync(current.ContentHash, successor, CancellationToken.None).ConfigureAwait(false);
            if (stored is null)
            {
                return Result(GovernedLoopOperationalControlStatus.NeedsReview, request.OperationId, request.Kind, request.TargetId, "delivery-batch-progress-ambiguous");
            }
            current = stored;
        }

        var applied = current.Progress.Count(item => item.Status == GovernedLoopOperationalControlStatus.Applied);
        var review = current.Progress.Count(item => item.Status == GovernedLoopOperationalControlStatus.NeedsReview);
        var failures = current.Progress.Count - applied - review;
        var outcome = review > 0 ? GovernedLoopOperationalControlStatus.NeedsReview
            : failures == 0 ? GovernedLoopOperationalControlStatus.Applied
            : applied > 0 ? GovernedLoopOperationalControlStatus.PartiallyApplied
            : AggregateBatchFailure(current.Progress.Select(item => item.Status));
        var state = outcome == GovernedLoopOperationalControlStatus.NeedsReview
            ? GovernedLoopOperationalControlReceiptState.NeedsReview
            : GovernedLoopOperationalControlReceiptState.Complete;
        var terminal = GovernedLoopOperationalControlReceiptFactory.Successor(
            current,
            UtcNow(current.UpdatedAtUtc) ?? current.UpdatedAtUtc,
            state,
            outcome,
            "delivery-batch-" + Token(outcome),
            current.Progress);
        var completed = await StoreAsync(current.ContentHash, terminal, CancellationToken.None).ConfigureAwait(false);
        return completed is null
            ? Result(GovernedLoopOperationalControlStatus.NeedsReview, request.OperationId, request.Kind, request.TargetId, "delivery-batch-outcome-ambiguous")
            : FromReceipt(completed, replay: false);
    }

    private static GovernedLoopOperationalControlStatus AggregateBatchFailure(IEnumerable<GovernedLoopOperationalControlStatus> statuses)
        => statuses.OrderByDescending(BatchFailurePriority).FirstOrDefault(GovernedLoopOperationalControlStatus.Conflict);

    private static int BatchFailurePriority(GovernedLoopOperationalControlStatus status)
        => status switch
        {
            GovernedLoopOperationalControlStatus.Corrupt => 6,
            GovernedLoopOperationalControlStatus.Unavailable => 5,
            GovernedLoopOperationalControlStatus.Backpressured => 4,
            GovernedLoopOperationalControlStatus.Conflict => 3,
            GovernedLoopOperationalControlStatus.Ineligible => 2,
            GovernedLoopOperationalControlStatus.NotFound => 1,
            _ => 0
        };

    private async Task<GovernedLoopOperationalControlProgress> ApplyDeliveryAsync(
        TriggerDeliveryId deliveryId,
        GovernedLoopOperationalControlProgress progress,
        CancellationToken cancellationToken)
    {
        TriggerQueueSnapshot snapshot;
        try
        {
            var observedAtUtc = UtcNow(DateTimeOffset.MinValue);
            if (observedAtUtc is null)
            {
                return progress with { Status = GovernedLoopOperationalControlStatus.Corrupt, ReasonCode = "delivery-control-clock-corrupt" };
            }
            snapshot = await _queueQuery.GetSnapshotAsync(observedAtUtc.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.Unavailable, ReasonCode = "delivery-control-source-unavailable" };
        }
        if (!TriggerQueueSnapshotEvidenceContract.IsValid(snapshot))
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.Corrupt, ReasonCode = "delivery-control-evidence-corrupt" };
        }
        var current = snapshot.Entries.SingleOrDefault(item => item.DeliveryId.Equals(deliveryId));
        if (current is null)
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.NotFound, ReasonCode = "delivery-control-not-found" };
        }
        if (current.State == TriggerQueueEntryState.Cancelled
            && current.Revision == progress.ExpectedRevision + 1
            && string.Equals(current.CanonicalEnvelopeHash, progress.ExpectedEvidenceHash, StringComparison.Ordinal))
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.NeedsReview, CurrentRevision = current.Revision, CurrentEvidenceHash = current.CanonicalEnvelopeHash, ReasonCode = "delivery-control-outcome-ambiguous" };
        }
        if (current.State == TriggerQueueEntryState.NeedsReview
            && current.Revision == progress.ExpectedRevision + 1
            && string.Equals(current.CanonicalEnvelopeHash, progress.ExpectedEvidenceHash, StringComparison.Ordinal))
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.NeedsReview, CurrentRevision = current.Revision, CurrentEvidenceHash = current.CanonicalEnvelopeHash, ReasonCode = "delivery-control-ambiguous-dispatch" };
        }
        if (current.Revision != progress.ExpectedRevision
            || !string.Equals(current.CanonicalEnvelopeHash, progress.ExpectedEvidenceHash, StringComparison.Ordinal))
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.Conflict, CurrentRevision = current.Revision, CurrentEvidenceHash = current.CanonicalEnvelopeHash, ReasonCode = "delivery-control-revision-conflict" };
        }
        if (!IsNonterminal(current.State))
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.Ineligible, CurrentRevision = current.Revision, CurrentEvidenceHash = current.CanonicalEnvelopeHash, ReasonCode = "delivery-control-already-terminal" };
        }
        TriggerQueueCancellationResult mutation;
        try
        {
            var cancelledAtUtc = UtcNow(current.RecordedAtUtc);
            if (cancelledAtUtc is null)
            {
                return progress with { Status = GovernedLoopOperationalControlStatus.Corrupt, ReasonCode = "delivery-control-clock-corrupt" };
            }
            mutation = await _queueCancellation.CancelAsync(deliveryId, progress.ExpectedRevision, cancelledAtUtc.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.Unavailable, ReasonCode = "delivery-control-unavailable" };
        }
        return MapDeliveryMutation(progress, current, mutation);
    }

    private static GovernedLoopOperationalControlProgress MapDeliveryMutation(
        GovernedLoopOperationalControlProgress progress,
        TriggerQueueEntry expected,
        TriggerQueueCancellationResult? mutation)
    {
        if (mutation is null
            || !Enum.IsDefined(mutation.Status)
            || mutation.Entry is { } entry
                && (!TriggerQueueSnapshotEvidenceContract.IsValid(entry)
                    || !SameDeliveryIdentity(expected, entry)))
        {
            return progress with { Status = GovernedLoopOperationalControlStatus.Corrupt, ReasonCode = "delivery-control-mutation-evidence-corrupt" };
        }

        return mutation.Status switch
        {
            TriggerQueueCancellationStatus.Cancelled when mutation.Entry is { } cancelled
                && cancelled.Revision == progress.ExpectedRevision + 1
                && string.Equals(cancelled.CanonicalEnvelopeHash, progress.ExpectedEvidenceHash, StringComparison.Ordinal)
                && cancelled.State == TriggerQueueEntryState.Cancelled
                => progress with
                {
                    Status = GovernedLoopOperationalControlStatus.Applied,
                    CurrentRevision = cancelled.Revision,
                    CurrentEvidenceHash = cancelled.CanonicalEnvelopeHash,
                    ReasonCode = "delivery-control-cancelled"
                },
            TriggerQueueCancellationStatus.Cancelled when mutation.Entry is { } ambiguous
                && ambiguous.Revision == progress.ExpectedRevision + 1
                && string.Equals(ambiguous.CanonicalEnvelopeHash, progress.ExpectedEvidenceHash, StringComparison.Ordinal)
                && ambiguous.State == TriggerQueueEntryState.NeedsReview
                => progress with
                {
                    Status = GovernedLoopOperationalControlStatus.NeedsReview,
                    CurrentRevision = ambiguous.Revision,
                    CurrentEvidenceHash = ambiguous.CanonicalEnvelopeHash,
                    ReasonCode = "delivery-control-ambiguous-dispatch"
                },
            TriggerQueueCancellationStatus.AlreadyTerminal when mutation.Entry is { } terminal
                && !IsNonterminal(terminal.State)
                => progress with
                {
                    Status = GovernedLoopOperationalControlStatus.Ineligible,
                    CurrentRevision = terminal.Revision,
                    CurrentEvidenceHash = terminal.CanonicalEnvelopeHash,
                    ReasonCode = "delivery-control-already-terminal"
                },
            TriggerQueueCancellationStatus.NotFound when mutation.Entry is null
                => progress with { Status = GovernedLoopOperationalControlStatus.NotFound, ReasonCode = "delivery-control-not-found" },
            TriggerQueueCancellationStatus.RevisionConflict when mutation.Entry is { } conflict
                && conflict.Revision != progress.ExpectedRevision
                => progress with
                {
                    Status = GovernedLoopOperationalControlStatus.Conflict,
                    CurrentRevision = conflict.Revision,
                    CurrentEvidenceHash = conflict.CanonicalEnvelopeHash,
                    ReasonCode = "delivery-control-revision-conflict"
                },
            TriggerQueueCancellationStatus.PersistenceBackpressured when mutation.Entry is null
                => progress with { Status = GovernedLoopOperationalControlStatus.Backpressured, ReasonCode = "delivery-control-persistence-backpressured" },
            TriggerQueueCancellationStatus.Unavailable when mutation.Entry is null
                => progress with { Status = GovernedLoopOperationalControlStatus.Unavailable, ReasonCode = "delivery-control-unavailable" },
            _ => progress with { Status = GovernedLoopOperationalControlStatus.Corrupt, ReasonCode = "delivery-control-mutation-evidence-corrupt" }
        };
    }

    private static bool SameDeliveryIdentity(TriggerQueueEntry expected, TriggerQueueEntry observed)
        => expected.DeliveryId.Equals(observed.DeliveryId)
            && expected.DeduplicationId.Equals(observed.DeduplicationId)
            && string.Equals(expected.LoopId, observed.LoopId, StringComparison.Ordinal)
            && string.Equals(expected.CanonicalEnvelopeHash, observed.CanonicalEnvelopeHash, StringComparison.Ordinal)
            && Equals(expected.OrderKey, observed.OrderKey)
            && expected.RecordedAtUtc == observed.RecordedAtUtc
            && expected.AdmissionStatus == observed.AdmissionStatus
            && expected.AdmissionReason == observed.AdmissionReason
            && string.Equals(expected.WorkspaceId, observed.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(expected.TargetGraphId, observed.TargetGraphId, StringComparison.Ordinal)
            && string.Equals(expected.TargetRevisionId, observed.TargetRevisionId, StringComparison.Ordinal);

    private static bool TryValidateScheduleMutation(
        ScheduleDefinition definition,
        ScheduleState expected,
        ScheduleState replacement,
        ScheduleStoreMutationResult? mutation,
        out GovernedLoopOperationalControlStatus status,
        out string reason,
        out long? currentRevision,
        out string? currentHash)
    {
        status = GovernedLoopOperationalControlStatus.Corrupt;
        reason = "schedule-control-mutation-evidence-corrupt";
        currentRevision = mutation?.CurrentState?.StateRevision;
        currentHash = null;
        if (mutation is null || !Enum.IsDefined(mutation.Status))
        {
            return false;
        }

        if (mutation.CurrentState is { } current)
        {
            if (!ScheduleContractValidator.ValidateDefinitionStateComposition(definition, current).IsValid
                || !ScheduleContractHash.TryComputeState(current, out currentHash, out _))
            {
                return false;
            }
        }

        if (!ScheduleContractHash.TryComputeState(expected, out var expectedHash, out _)
            || !ScheduleContractHash.TryComputeState(replacement, out var replacementHash, out _))
        {
            return false;
        }

        if (mutation.ExactReplay)
        {
            return mutation.Status == ScheduleStoreMutationStatus.Applied
                && string.Equals(currentHash, replacementHash, StringComparison.Ordinal);
        }

        switch (mutation.Status)
        {
            case ScheduleStoreMutationStatus.Applied when string.Equals(currentHash, replacementHash, StringComparison.Ordinal):
                status = GovernedLoopOperationalControlStatus.Applied;
                reason = "schedule-control-applied";
                return true;
            case ScheduleStoreMutationStatus.Conflict when currentHash is null
                || !string.Equals(currentHash, expectedHash, StringComparison.Ordinal):
                status = GovernedLoopOperationalControlStatus.Conflict;
                reason = "schedule-control-conflict";
                return true;
            case ScheduleStoreMutationStatus.Unavailable:
                status = GovernedLoopOperationalControlStatus.Unavailable;
                reason = "schedule-control-unavailable";
                return true;
            case ScheduleStoreMutationStatus.Corrupt:
                status = GovernedLoopOperationalControlStatus.Corrupt;
                reason = "schedule-control-corrupt";
                return true;
            case ScheduleStoreMutationStatus.Backpressured:
                status = GovernedLoopOperationalControlStatus.Backpressured;
                reason = "schedule-control-backpressured";
                return true;
            default:
                return false;
        }
    }

    private async Task<GovernedLoopOperationalControlResult> CompleteAsync(
        GovernedLoopOperationalControlReceipt receipt,
        GovernedLoopOperationalControlStatus outcome,
        string reasonCode,
        CancellationToken cancellationToken,
        long? currentRevision = null,
        string? currentEvidenceHash = null)
    {
        var state = outcome == GovernedLoopOperationalControlStatus.NeedsReview
            ? GovernedLoopOperationalControlReceiptState.NeedsReview
            : GovernedLoopOperationalControlReceiptState.Complete;
        var terminal = GovernedLoopOperationalControlReceiptFactory.Successor(
            receipt,
            UtcNow(receipt.UpdatedAtUtc) ?? receipt.UpdatedAtUtc,
            state,
            outcome,
            reasonCode,
            receipt.Progress);
        var stored = await StoreAsync(receipt.ContentHash, terminal, cancellationToken).ConfigureAwait(false);
        return stored is null
            ? Result(GovernedLoopOperationalControlStatus.NeedsReview, receipt.OperationId, receipt.Kind, receipt.TargetId, "operational-control-outcome-ambiguous", currentRevision, currentEvidenceHash)
            : FromReceipt(stored, replay: false, currentRevision, currentEvidenceHash);
    }

    private async Task<GovernedLoopOperationalControlReceipt?> StoreAsync(
        string expectedHash,
        GovernedLoopOperationalControlReceipt replacement,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _receipts.CompareExchangeAsync(expectedHash, replacement, cancellationToken).ConfigureAwait(false);
            return result.Status is GovernedLoopOperationalControlReceiptStoreStatus.Committed or GovernedLoopOperationalControlReceiptStoreStatus.Replayed
                ? result.Receipt
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private DateTimeOffset? UtcNow(DateTimeOffset notBefore)
    {
        try
        {
            var value = _timeProvider.GetUtcNow();
            return GovernedLoopOperationalContract.IsUtc(value) && value >= notBefore ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GovernedLoopOperationalControlResult FromReceipt(
        GovernedLoopOperationalControlReceipt receipt,
        bool replay,
        long? currentRevision = null,
        string? currentEvidenceHash = null)
    {
        var outcome = replay && receipt.Outcome == GovernedLoopOperationalControlStatus.Applied
            ? GovernedLoopOperationalControlStatus.Replayed
            : receipt.Outcome;
        return Result(
            outcome,
            receipt.OperationId,
            receipt.Kind,
            receipt.TargetId,
            replay ? "operational-control-terminal-replay" : receipt.ReasonCode,
            currentRevision,
            currentEvidenceHash,
            receipt.ContentHash,
            receipt.Progress.Count,
            receipt.Progress.Count(item => item.Status == GovernedLoopOperationalControlStatus.Applied),
            receipt.Progress.Count(item => item.Status == GovernedLoopOperationalControlStatus.NeedsReview));
    }

    private static GovernedLoopOperationalControlResult Result(
        GovernedLoopOperationalControlStatus status,
        string operationId,
        GovernedLoopOperationalControlKind kind,
        string targetId,
        string reasonCode,
        long? currentRevision = null,
        string? currentEvidenceHash = null,
        string? receiptHash = null,
        int matchedCount = 0,
        int appliedCount = 0,
        int needsReviewCount = 0)
        => new(status, operationId, kind, targetId, reasonCode, currentRevision, currentEvidenceHash, receiptHash, matchedCount, appliedCount, needsReviewCount);

    private static GovernedLoopOperationalControlStatus Map(GovernedLoopOperationalControlReceiptStoreStatus status)
        => status switch
        {
            GovernedLoopOperationalControlReceiptStoreStatus.Conflict => GovernedLoopOperationalControlStatus.Conflict,
            GovernedLoopOperationalControlReceiptStoreStatus.OperationInProgress => GovernedLoopOperationalControlStatus.OperationInProgress,
            GovernedLoopOperationalControlReceiptStoreStatus.Backpressured => GovernedLoopOperationalControlStatus.Backpressured,
            GovernedLoopOperationalControlReceiptStoreStatus.Corrupt => GovernedLoopOperationalControlStatus.Corrupt,
            _ => GovernedLoopOperationalControlStatus.Unavailable
        };

    private static GovernedLoopOperationalControlStatus Map(CustomLoopControlStatus status)
        => status switch
        {
            CustomLoopControlStatus.PauseRequested
                or CustomLoopControlStatus.Paused
                or CustomLoopControlStatus.CancelRequested
                or CustomLoopControlStatus.Cancelled
                or CustomLoopControlStatus.Resumed
                or CustomLoopControlStatus.Waiting
                or CustomLoopControlStatus.Completed => GovernedLoopOperationalControlStatus.Applied,
            CustomLoopControlStatus.Conflict => GovernedLoopOperationalControlStatus.Conflict,
            CustomLoopControlStatus.NotFound => GovernedLoopOperationalControlStatus.NotFound,
            CustomLoopControlStatus.OperationInProgress => GovernedLoopOperationalControlStatus.OperationInProgress,
            CustomLoopControlStatus.NeedsReview or CustomLoopControlStatus.AuditWarning => GovernedLoopOperationalControlStatus.NeedsReview,
            CustomLoopControlStatus.InvalidState => GovernedLoopOperationalControlStatus.Ineligible,
            _ => GovernedLoopOperationalControlStatus.Unavailable
        };

    private static GovernedLoopOperationalControlStatus Map(ScheduleStoreReadStatus status)
        => status switch
        {
            ScheduleStoreReadStatus.NotFound => GovernedLoopOperationalControlStatus.NotFound,
            ScheduleStoreReadStatus.Backpressured => GovernedLoopOperationalControlStatus.Backpressured,
            ScheduleStoreReadStatus.Corrupt => GovernedLoopOperationalControlStatus.Corrupt,
            _ => GovernedLoopOperationalControlStatus.Unavailable
        };

    private static string StoreReason(GovernedLoopOperationalControlReceiptStoreStatus status)
        => "operational-control-receipt-" + Token(status);

    private static bool IsNonterminal(TriggerQueueEntryState state)
        => state is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching;

    private static string Token<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        var token = new System.Text.StringBuilder(text.Length + 4);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsUpper(character) && index > 0)
            {
                token.Append('-');
            }
            token.Append(char.ToLowerInvariant(character));
        }
        return token.ToString();
    }
}
