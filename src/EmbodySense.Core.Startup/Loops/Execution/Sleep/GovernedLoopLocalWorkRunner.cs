using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Composes durable discovery with the canonical schedule, trigger, and wake one-shot services.</summary>
/// <remarks>
/// This adapter owns no background lifetime and performs no retry. It consumes each detached family page in order so a
/// page-bound backlog remains drainable, while every call selects at most one candidate and waits for the subsystem-owned
/// one-shot boundary to report its truthful durable outcome.
/// </remarks>
public sealed class GovernedLoopLocalWorkRunner : IGovernedLoopLocalWorkRunner, IGovernedLoopLocalWorkReadinessProbe
{
    private readonly IGovernedLoopBackgroundWorkSource _backgroundWork;
    private readonly SemaphoreSlim _candidateGate = new(1, 1);
    private readonly IGovernedLoopLocalOneShotServices _oneShot;
    private readonly GovernedLoopLocalWorkRunnerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _triggerHistoryGate = new();
    private readonly Queue<string> _recentTriggerLoopIds = new();
    private readonly Queue<ScheduleId> _schedulePage = new();
    private readonly Queue<GovernedLoopWakeRequest> _wakePage = new();
    private readonly Queue<GovernedLoopWakeReconciliationRequest> _wakeReconciliationPage = new();
    private long _wakeFamilySelection;

    /// <summary>Creates one host-neutral adapter over canonical one-shot services and bounded durable discovery.</summary>
    public GovernedLoopLocalWorkRunner(
        IGovernedLoopBackgroundWorkSource backgroundWork,
        ScheduleRuntimeFacade schedules,
        ICustomLoopRunStore runs,
        ITriggerQueueQueryPort triggerQueue,
        TriggerWorkerService triggers,
        GovernedLoopSleepService sleep,
        GovernedLoopLocalWorkRunnerOptions options,
        TimeProvider? timeProvider = null)
        : this(
            backgroundWork,
            new GovernedLoopLocalOneShotServices(
                schedules,
                runs,
                triggerQueue,
                triggers,
                sleep,
                (options ?? throw new ArgumentNullException(nameof(options))).CandidateReadLimit),
            options,
            timeProvider)
    {
    }

    /// <summary>Creates one runner over an explicit surface-neutral aggregation of the canonical one-shot boundaries.</summary>
    public GovernedLoopLocalWorkRunner(
        IGovernedLoopBackgroundWorkSource backgroundWork,
        IGovernedLoopLocalOneShotServices oneShot,
        GovernedLoopLocalWorkRunnerOptions options,
        TimeProvider? timeProvider = null)
    {
        _backgroundWork = backgroundWork ?? throw new ArgumentNullException(nameof(backgroundWork));
        _oneShot = oneShot ?? throw new ArgumentNullException(nameof(oneShot));
        _options = ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(family))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "work-family-corrupt");
        }

        if (!TryGetUtcNow(out var observedAtUtc, out var clockFailure))
        {
            return clockFailure;
        }

        return family switch
        {
            GovernedLoopLocalWorkFamily.Schedule => await RunScheduleAsync(observedAtUtc, cancellationToken).ConfigureAwait(false),
            GovernedLoopLocalWorkFamily.Trigger => await RunTriggerAsync(observedAtUtc, cancellationToken).ConfigureAwait(false),
            GovernedLoopLocalWorkFamily.Wake => await RunWakeAsync(observedAtUtc, cancellationToken).ConfigureAwait(false),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "work-family-corrupt")
        };
    }

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> ProbeReadinessAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetUtcNow(out var observedAtUtc, out var clockFailure))
        {
            return clockFailure;
        }

        return family switch
        {
            GovernedLoopLocalWorkFamily.Schedule => await ProbeScheduleAsync(observedAtUtc, cancellationToken).ConfigureAwait(false),
            GovernedLoopLocalWorkFamily.Trigger => await ProbeTriggerAsync(observedAtUtc, cancellationToken).ConfigureAwait(false),
            GovernedLoopLocalWorkFamily.Wake => await ProbeWakeAsync(observedAtUtc, cancellationToken).ConfigureAwait(false),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "work-family-corrupt")
        };
    }

    private async Task<GovernedLoopLocalWorkResult> ProbeScheduleAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var source = await _backgroundWork.ReadAsync(GovernedLoopBackgroundWorkFamily.Schedule, observedAtUtc, 1, cancellationToken).ConfigureAwait(false);
            return source is null || !Enum.IsDefined(source.ScheduleStatus)
                ? Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-readiness-corrupt")
                : ClassifyBackgroundStatus(source.ScheduleStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-readiness-unavailable");
        }
    }

    private async Task<GovernedLoopLocalWorkResult> ProbeTriggerAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _oneShot.ReadTriggerQueueAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
            return !IsValidTriggerSnapshot(snapshot)
                ? Result(GovernedLoopLocalWorkResultStatus.Corrupt, "trigger-readiness-corrupt")
                : snapshot!.PersistenceBackpressured
                    ? Result(GovernedLoopLocalWorkResultStatus.Backpressured, "trigger-readiness-backpressured")
                    : Result(GovernedLoopLocalWorkResultStatus.Empty, "trigger-readiness-ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "trigger-readiness-unavailable");
        }
    }

    private async Task<GovernedLoopLocalWorkResult> ProbeWakeAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var source = await _backgroundWork.ReadAsync(GovernedLoopBackgroundWorkFamily.Wake, observedAtUtc, 1, cancellationToken).ConfigureAwait(false);
            if (source is null || !Enum.IsDefined(source.WakeStatus) || !Enum.IsDefined(source.WakeReconciliationStatus))
            {
                return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "wake-readiness-corrupt");
            }

            var wake = ClassifyBackgroundStatus(source.WakeStatus);
            var reconciliation = ClassifyBackgroundStatus(source.WakeReconciliationStatus);
            return wake.Status is GovernedLoopLocalWorkResultStatus.Completed or GovernedLoopLocalWorkResultStatus.Empty
                && reconciliation.Status is GovernedLoopLocalWorkResultStatus.Completed or GovernedLoopLocalWorkResultStatus.Empty
                ? Result(GovernedLoopLocalWorkResultStatus.Empty, "wake-readiness-ready")
                : wake.Status is not (GovernedLoopLocalWorkResultStatus.Completed or GovernedLoopLocalWorkResultStatus.Empty)
                    ? wake
                    : reconciliation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "wake-readiness-unavailable");
        }
    }

    private async Task<GovernedLoopLocalWorkResult> RunScheduleAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var selection = await SelectScheduleAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
        if (selection.Status != GovernedLoopBackgroundWorkReadStatus.Found)
        {
            return ClassifyBackgroundStatus(selection.Status);
        }

        var candidate = selection.Candidate!;
        ScheduleEvaluationResult? evaluation;
        try
        {
            evaluation = await _oneShot.EvaluateScheduleOnceAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-evaluator-unavailable");
        }

        if (!IsValid(evaluation, candidate))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-result-corrupt");
        }

        var exactEvaluation = evaluation!;
        return exactEvaluation.Status switch
        {
            ScheduleEvaluationStatus.Queued
                or ScheduleEvaluationStatus.Replayed
                or ScheduleEvaluationStatus.Rejected
                or ScheduleEvaluationStatus.Skipped
                or ScheduleEvaluationStatus.Deferred
                => Result(GovernedLoopLocalWorkResultStatus.Completed, ScheduleReason(exactEvaluation.Status)),
            ScheduleEvaluationStatus.NotFound
                or ScheduleEvaluationStatus.NotDue
                or ScheduleEvaluationStatus.Disabled
                or ScheduleEvaluationStatus.Exhausted
                => Result(GovernedLoopLocalWorkResultStatus.Empty, ScheduleReason(exactEvaluation.Status)),
            ScheduleEvaluationStatus.Backpressured
                => Result(GovernedLoopLocalWorkResultStatus.Backpressured, "schedule-backpressured"),
            ScheduleEvaluationStatus.Conflict
                => Result(GovernedLoopLocalWorkResultStatus.Conflict, "schedule-conflict"),
            ScheduleEvaluationStatus.PermissionDenied
                or ScheduleEvaluationStatus.ClockRollback
                or ScheduleEvaluationStatus.NeedsReview
                or ScheduleEvaluationStatus.BoundExceeded
                => Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, ScheduleReason(exactEvaluation.Status)),
            ScheduleEvaluationStatus.Unavailable
                => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-unavailable"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-result-corrupt")
        };
    }

    private async Task<GovernedLoopLocalWorkResult> RunTriggerAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        GovernedLoopLocalWorkResult? retainedSchedule;
        try
        {
            retainedSchedule = await _oneShot.RetryScheduleAdmissionOnceAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-retry-service-unavailable");
        }

        if (retainedSchedule is null
            || !Enum.IsDefined(retainedSchedule.Status)
            || !CustomLoopArtifactIdentifier.IsValid(retainedSchedule.ReasonCode, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-result-corrupt");
        }

        if (retainedSchedule.Status != GovernedLoopLocalWorkResultStatus.Empty)
        {
            return retainedSchedule;
        }

        TriggerQueueSnapshot? snapshot;
        try
        {
            snapshot = await _oneShot.ReadTriggerQueueAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "trigger-query-unavailable");
        }

        if (!IsValidTriggerSnapshot(snapshot))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "trigger-snapshot-corrupt");
        }

        if (snapshot!.PersistenceBackpressured)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Backpressured, "trigger-backpressured");
        }

        var selection = new TriggerWorkerSelectionRequest(
            _options.TriggerWorkerId,
            snapshot.Generation,
            observedAtUtc,
            _options.TriggerLeaseDuration,
            GetRecentTriggerLoopIds(),
            _options.MaximumConsecutiveTriggerSelectionsPerLoop);
        TriggerWorkerRunResult? run;
        try
        {
            run = await _oneShot.RunTriggerOnceAsync(new TriggerWorkerRunRequest(selection), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "trigger-request-corrupt");
        }
        catch (Exception)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "trigger-worker-unavailable");
        }

        if (!IsValid(run, snapshot.Quota))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "trigger-result-corrupt");
        }

        if (run!.SelectionStatus == TriggerWorkerSelectionStatus.Acquired && run.Entry is not null)
        {
            RememberTriggerLoop(run.Entry!.LoopId);
        }

        return run.SelectionStatus switch
        {
            TriggerWorkerSelectionStatus.Empty
                => Result(GovernedLoopLocalWorkResultStatus.Empty, "trigger-empty"),
            TriggerWorkerSelectionStatus.RevisionConflict
                => Result(GovernedLoopLocalWorkResultStatus.Conflict, "trigger-revision-conflict"),
            TriggerWorkerSelectionStatus.ClockRollback
                => Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, "trigger-clock-rollback"),
            TriggerWorkerSelectionStatus.Unavailable
                => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "trigger-unavailable"),
            TriggerWorkerSelectionStatus.Acquired => ClassifyTriggerMutation(run.MutationStatus!.Value),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "trigger-result-corrupt")
        };
    }

    private async Task<GovernedLoopLocalWorkResult> RunWakeAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var preferReconciliation = (Interlocked.Increment(ref _wakeFamilySelection) & 1) == 1;
        var preferredFamily = preferReconciliation
            ? GovernedLoopBackgroundWorkFamily.WakeReconciliation
            : GovernedLoopBackgroundWorkFamily.Wake;
        var alternateFamily = preferReconciliation
            ? GovernedLoopBackgroundWorkFamily.Wake
            : GovernedLoopBackgroundWorkFamily.WakeReconciliation;
        var preferred = await SelectWakeAsync(preferredFamily, observedAtUtc, cancellationToken).ConfigureAwait(false);
        var selected = preferred.Candidate is not null || preferred.Reconciliation is not null
            ? preferred
            : await SelectWakeAsync(alternateFamily, observedAtUtc, cancellationToken).ConfigureAwait(false);
        if (selected.Candidate is null && selected.Reconciliation is null)
        {
            return ClassifyBackgroundStatus(CombineWakeStatuses(preferred.Status, selected.Status));
        }

        GovernedLoopWakeResult? result;
        try
        {
            result = selected.Reconciliation is not null
                ? await _oneShot.ReconcileWakeOnceAsync(
                    selected.Reconciliation,
                    cancellationToken).ConfigureAwait(false)
                : await _oneShot.WakeOnceAsync(
                    selected.Candidate!,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "wake-service-unavailable");
        }

        if (!IsValid(result))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "wake-result-corrupt");
        }

        var exactResult = result!;
        return exactResult.Status switch
        {
            GovernedLoopWakeResultStatus.Committed
                or GovernedLoopWakeResultStatus.Duplicate
                or GovernedLoopWakeResultStatus.Late
                or GovernedLoopWakeResultStatus.Stale
                or GovernedLoopWakeResultStatus.Cancelled
                or GovernedLoopWakeResultStatus.Expired
                => Result(GovernedLoopLocalWorkResultStatus.Completed, WakeReason(exactResult.Status)),
            GovernedLoopWakeResultStatus.NotEligible
                or GovernedLoopWakeResultStatus.NotFound
                => Result(GovernedLoopLocalWorkResultStatus.Empty, WakeReason(exactResult.Status)),
            GovernedLoopWakeResultStatus.Conflict
                => Result(GovernedLoopLocalWorkResultStatus.Conflict, "wake-conflict"),
            GovernedLoopWakeResultStatus.Paused
                or GovernedLoopWakeResultStatus.ReviewBlocked
                or GovernedLoopWakeResultStatus.AmbiguousAttempt
                or GovernedLoopWakeResultStatus.Failed
                => Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, WakeReason(exactResult.Status)),
            GovernedLoopWakeResultStatus.Unavailable
                => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "wake-unavailable"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "wake-result-corrupt")
        };
    }

    private async Task<GovernedLoopBackgroundWorkReadResult?> ReadBackgroundAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _backgroundWork.ReadAsync(
                family,
                observedAtUtc,
                _options.CandidateReadLimit,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GovernedLoopBackgroundWorkReadResultFactory.CreateDetached(
                GovernedLoopBackgroundWorkReadStatus.Unavailable,
                [],
                [],
                []);
        }
    }

    private async Task<(GovernedLoopBackgroundWorkReadStatus Status, ScheduleId? Candidate)> SelectScheduleAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await _candidateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schedulePage.Count == 0)
            {
                var read = await ReadBackgroundAsync(
                    GovernedLoopBackgroundWorkFamily.Schedule,
                    observedAtUtc,
                    cancellationToken).ConfigureAwait(false);
                if (!GovernedLoopBackgroundWorkContract.IsValid(read, _options.CandidateReadLimit))
                {
                    return (GovernedLoopBackgroundWorkReadStatus.Corrupt, null);
                }

                if (read!.ScheduleStatus != GovernedLoopBackgroundWorkReadStatus.Found)
                {
                    return (read.ScheduleStatus, null);
                }

                foreach (var candidate in read.ScheduleCandidates)
                {
                    _schedulePage.Enqueue(candidate);
                }
            }

            return (GovernedLoopBackgroundWorkReadStatus.Found, _schedulePage.Dequeue());
        }
        finally
        {
            _candidateGate.Release();
        }
    }

    private async Task<(
        GovernedLoopBackgroundWorkReadStatus Status,
        GovernedLoopWakeRequest? Candidate,
        GovernedLoopWakeReconciliationRequest? Reconciliation)> SelectWakeAsync(
        GovernedLoopBackgroundWorkFamily family,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await _candidateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queueHasCandidate = family switch
            {
                GovernedLoopBackgroundWorkFamily.Wake => _wakePage.Count > 0,
                GovernedLoopBackgroundWorkFamily.WakeReconciliation => _wakeReconciliationPage.Count > 0,
                _ => false
            };
            if (!queueHasCandidate)
            {
                var read = await ReadBackgroundAsync(family, observedAtUtc, cancellationToken).ConfigureAwait(false);
                if (!GovernedLoopBackgroundWorkContract.IsValid(read, _options.CandidateReadLimit))
                {
                    return (GovernedLoopBackgroundWorkReadStatus.Corrupt, null, null);
                }

                var status = family == GovernedLoopBackgroundWorkFamily.Wake
                    ? read!.WakeStatus
                    : read!.WakeReconciliationStatus;
                if (status != GovernedLoopBackgroundWorkReadStatus.Found)
                {
                    return (status, null, null);
                }

                foreach (var candidate in read.WakeCandidates)
                {
                    _wakePage.Enqueue(candidate);
                }

                foreach (var reconciliation in read.WakeReconciliationCandidates)
                {
                    _wakeReconciliationPage.Enqueue(reconciliation);
                }
            }

            return family == GovernedLoopBackgroundWorkFamily.Wake
                ? (GovernedLoopBackgroundWorkReadStatus.Found, _wakePage.Dequeue(), null)
                : (GovernedLoopBackgroundWorkReadStatus.Found, null, _wakeReconciliationPage.Dequeue());
        }
        finally
        {
            _candidateGate.Release();
        }
    }

    private GovernedLoopLocalWorkResult ClassifyBackgroundStatus(GovernedLoopBackgroundWorkReadStatus status)
        => status switch
        {
            GovernedLoopBackgroundWorkReadStatus.Empty
                => Result(GovernedLoopLocalWorkResultStatus.Empty, "background-candidates-empty"),
            GovernedLoopBackgroundWorkReadStatus.Backpressured
                => Result(GovernedLoopLocalWorkResultStatus.Backpressured, "background-candidates-backpressured"),
            GovernedLoopBackgroundWorkReadStatus.Corrupt
                => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "background-candidates-corrupt"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "background-candidates-unavailable")
        };

    private static GovernedLoopBackgroundWorkReadStatus CombineWakeStatuses(
        GovernedLoopBackgroundWorkReadStatus wakeStatus,
        GovernedLoopBackgroundWorkReadStatus reconciliationStatus)
    {
        GovernedLoopBackgroundWorkReadStatus[] statuses = [wakeStatus, reconciliationStatus];
        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Found))
        {
            return GovernedLoopBackgroundWorkReadStatus.Found;
        }

        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Corrupt))
        {
            return GovernedLoopBackgroundWorkReadStatus.Corrupt;
        }

        if (statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Unavailable))
        {
            return GovernedLoopBackgroundWorkReadStatus.Unavailable;
        }

        return statuses.Contains(GovernedLoopBackgroundWorkReadStatus.Backpressured)
            ? GovernedLoopBackgroundWorkReadStatus.Backpressured
            : GovernedLoopBackgroundWorkReadStatus.Empty;
    }

    private static GovernedLoopLocalWorkResult ClassifyTriggerMutation(TriggerWorkerMutationStatus status)
        => status switch
        {
            TriggerWorkerMutationStatus.Committed
                or TriggerWorkerMutationStatus.Replayed
                => Result(GovernedLoopLocalWorkResultStatus.Completed, TriggerReason(status)),
            TriggerWorkerMutationStatus.NotFound
                or TriggerWorkerMutationStatus.RevisionConflict
                or TriggerWorkerMutationStatus.StaleOwner
                => Result(GovernedLoopLocalWorkResultStatus.Conflict, TriggerReason(status)),
            TriggerWorkerMutationStatus.ClockRollback
                => Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, "trigger-clock-rollback"),
            TriggerWorkerMutationStatus.Unavailable
                => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "trigger-unavailable"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "trigger-result-corrupt")
        };

    private IReadOnlyList<string> GetRecentTriggerLoopIds()
    {
        lock (_triggerHistoryGate)
        {
            return _recentTriggerLoopIds.ToArray();
        }
    }

    private void RememberTriggerLoop(string loopId)
    {
        lock (_triggerHistoryGate)
        {
            _recentTriggerLoopIds.Enqueue(loopId);
            while (_recentTriggerLoopIds.Count > TriggerWorkerLimits.MaxRecentLoopIds)
            {
                _recentTriggerLoopIds.Dequeue();
            }
        }
    }

    private bool TryGetUtcNow(
        out DateTimeOffset observedAtUtc,
        out GovernedLoopLocalWorkResult? failure)
    {
        try
        {
            observedAtUtc = _timeProvider.GetUtcNow();
        }
        catch (Exception)
        {
            observedAtUtc = default;
            failure = Result(GovernedLoopLocalWorkResultStatus.Unavailable, "coordinator-clock-unavailable");
            return false;
        }

        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            failure = Result(GovernedLoopLocalWorkResultStatus.Corrupt, "coordinator-clock-corrupt");
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool IsValidTriggerSnapshot(TriggerQueueSnapshot? snapshot)
    {
        if (snapshot is null
            || snapshot.SchemaVersion != TriggerQueueSnapshot.CurrentSchemaVersion
            || snapshot.Generation < 0
            || snapshot.Quota is null
            || snapshot.Entries is null)
        {
            return false;
        }

        try
        {
            TriggerQueueQuotaValidator.Validate(snapshot.Quota);
            var deliveryIds = new HashSet<string>(StringComparer.Ordinal);
            var deduplicationIds = new HashSet<string>(StringComparer.Ordinal);
            var perLoop = new Dictionary<string, int>(StringComparer.Ordinal);
            long queuedBytes = 0;
            long queuedReservations = 0;
            long retainedBytes = 0;
            long retainedReservations = 0;
            var queuedEntries = 0;
            TriggerQueueEntry? previous = null;
            foreach (var entry in snapshot.Entries)
            {
                if (!IsValid(entry, snapshot.Quota)
                    || !deliveryIds.Add(entry.DeliveryId.Value)
                    || !deduplicationIds.Add(entry.DeduplicationId.Value)
                    || previous is not null && Compare(previous, entry) > 0)
                {
                    return false;
                }

                retainedBytes = checked(retainedBytes + entry.SerializedEntryBytes);
                retainedReservations = checked(retainedReservations + entry.RetainedReservationBytes);
                if (IsNonterminal(entry.State))
                {
                    queuedEntries++;
                    queuedBytes = checked(queuedBytes + entry.SerializedEntryBytes);
                    queuedReservations = checked(queuedReservations + entry.QueuedReservationBytes);
                    perLoop[entry.LoopId] = perLoop.GetValueOrDefault(entry.LoopId) + 1;
                }

                previous = entry;
            }

            return snapshot.RetainedEntries == snapshot.Entries.Count
                && snapshot.QueuedEntries == queuedEntries
                && snapshot.QueuedBytes == queuedBytes
                && snapshot.QueuedReservationBytes == queuedReservations
                && snapshot.RetainedBytes == retainedBytes
                && snapshot.RetainedReservationBytes == retainedReservations
                && snapshot.QueuedEntries <= snapshot.Quota.MaxQueuedEntries
                && snapshot.RetainedEntries <= snapshot.Quota.MaxRetainedEntries
                && snapshot.QueuedReservationBytes <= snapshot.Quota.MaxQueuedBytes
                && snapshot.RetainedReservationBytes <= snapshot.Quota.MaxRetainedBytes
                && perLoop.Values.All(count => count <= snapshot.Quota.MaxQueuedEntriesPerLoop)
                && snapshot.DurabilityTombstones is >= 0
                    && snapshot.DurabilityTombstones <= snapshot.Quota.MaxDurabilityTombstones;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsValid(TriggerWorkerRunResult? run, TriggerQueueQuota quota)
        => run is not null
            && Enum.IsDefined(run.SelectionStatus)
            && (run.MutationStatus is null || Enum.IsDefined(run.MutationStatus.Value))
            && (run.SelectionStatus == TriggerWorkerSelectionStatus.Acquired
                ? run.MutationStatus is not null
                    && (run.Entry is null
                        || IsValid(run.Entry, quota))
                    && (run.MutationStatus is not (TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed)
                        || run.Entry is not null)
                    && (run.MutationStatus != TriggerWorkerMutationStatus.NotFound || run.Entry is null)
                : run.MutationStatus is null && run.Entry is null);

    private static bool IsValid(ScheduleEvaluationResult? result, ScheduleId scheduleId)
        => result is not null
            && Enum.IsDefined(result.Status)
            && CustomLoopArtifactIdentifier.IsValid(result.ReasonCode, ScheduleContractLimits.MaxReasonCodeCharacters)
            && (result.State is null
                || result.State.ScheduleId.Equals(scheduleId)
                    && ScheduleContractValidator.ValidateState(result.State).IsValid)
            && (result.Status switch
            {
                ScheduleEvaluationStatus.NotFound => result.State is null,
                ScheduleEvaluationStatus.Unavailable or ScheduleEvaluationStatus.Corrupt => true,
                _ => result.State is not null
            });

    private static bool IsValid(GovernedLoopWakeResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && (result.Evidence is null || GovernedLoopSleepContractValidator.Validate(result.Evidence).IsValid)
            && (!result.ContinuationInvoked
                || result.Evidence is not null
                    && result.Status is GovernedLoopWakeResultStatus.Committed
                        or GovernedLoopWakeResultStatus.AmbiguousAttempt
                        or GovernedLoopWakeResultStatus.Failed)
            && IsWakeShapeValid(result.Status, result.Evidence?.Disposition);

    private static bool IsValid(TriggerQueueEntry entry, TriggerQueueQuota quota)
    {
        if (entry.DeliveryId is null
            || entry.DeduplicationId is null
            || !TriggerDeliveryId.TryParse(entry.DeliveryId.Value, out var deliveryId)
            || deliveryId?.Equals(entry.DeliveryId) != true
            || !TriggerDeduplicationId.TryParse(entry.DeduplicationId.Value, out var deduplicationId)
            || deduplicationId?.Equals(entry.DeduplicationId) != true
            || !CustomLoopArtifactIdentifier.IsValid(entry.LoopId, TriggerDeliveryLimits.MaxLoopIdCharacters)
            || !IsHash(entry.CanonicalEnvelopeHash)
            || entry.SerializedEntryBytes is <= 0
            || entry.RetainedReservationBytes < entry.SerializedEntryBytes
            || entry.RetainedReservationBytes > quota.MaxEntryBytes
            || entry.QueuedReservationBytes is < 0
            || entry.QueuedReservationBytes > entry.RetainedReservationBytes
            || entry.Revision <= 0
            || entry.RecordedAtUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(entry.State)
            || !Enum.IsDefined(entry.TerminalReason)
            || !IsAdmissionShapeValid(entry.AdmissionStatus, entry.AdmissionReason)
            || entry.OrderKey is null
            || entry.OrderKey.EligibleAtUtc.Offset != TimeSpan.Zero
            || entry.OrderKey.AcceptedAtUtc.Offset != TimeSpan.Zero
            || entry.OrderKey.AcceptedAtUtc != entry.RecordedAtUtc
            || entry.OrderKey.EligibleAtUtc < entry.OrderKey.AcceptedAtUtc
            || !Enum.IsDefined(entry.OrderKey.Priority)
            || !string.Equals(entry.OrderKey.DeliveryId, entry.DeliveryId.Value, StringComparison.Ordinal)
            || !IsWorkerEvidenceValid(entry))
        {
            return false;
        }

        if (entry.AdmissionStatus == TriggerAdmissionStatus.NotYetEligible)
        {
            if (entry.WorkerLease is not null || entry.Dispatch is not null)
            {
                return false;
            }

            return entry.State switch
            {
                TriggerQueueEntryState.Queued => entry.TerminalReason == TriggerQueueTerminalReason.None
                    && entry.TerminalAtUtc is null
                    && IsNonterminalReservationValid(entry),
                TriggerQueueEntryState.Backpressured => IsTerminalShapeValid(entry)
                    && entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded
                        or TriggerQueueTerminalReason.QueueBytesExceeded
                        or TriggerQueueTerminalReason.LoopQuotaExceeded,
                TriggerQueueEntryState.Cancelled => IsTerminalShapeValid(entry)
                    && entry.TerminalReason == TriggerQueueTerminalReason.Cancelled,
                TriggerQueueEntryState.Expired => IsTerminalShapeValid(entry)
                    && entry.TerminalReason is TriggerQueueTerminalReason.Expired
                        or TriggerQueueTerminalReason.DeadlineExceeded,
                _ => false
            };
        }

        if (IsNonterminal(entry.State))
        {
            if (entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed)
                || entry.TerminalReason != TriggerQueueTerminalReason.None
                || entry.TerminalAtUtc is not null
                || !IsNonterminalReservationValid(entry))
            {
                return false;
            }

            return entry.State switch
            {
                TriggerQueueEntryState.Queued => entry.Dispatch is null
                    && (entry.WorkerLease is null || entry.WorkerLease.ReleasedAtUtc is not null),
                TriggerQueueEntryState.WorkerOwned => entry.Dispatch is null
                    && entry.WorkerLease is { ReleasedAtUtc: null },
                TriggerQueueEntryState.Dispatching => entry.Dispatch?.Outcome == TriggerDispatchOutcome.IntentRecorded
                    && entry.WorkerLease is { ReleasedAtUtc: null },
                _ => false
            };
        }

        if (!IsTerminalShapeValid(entry))
        {
            return false;
        }

        return entry.State switch
        {
            TriggerQueueEntryState.Rejected => entry.TerminalReason == TriggerQueueTerminalReason.AdmissionRejected
                && entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed)
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Backpressured => entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded
                    or TriggerQueueTerminalReason.QueueBytesExceeded
                    or TriggerQueueTerminalReason.LoopQuotaExceeded
                && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Cancelled => entry.TerminalReason == TriggerQueueTerminalReason.Cancelled
                && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Expired => entry.TerminalReason is TriggerQueueTerminalReason.Expired
                    or TriggerQueueTerminalReason.DeadlineExceeded
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Dispatched => entry.TerminalReason == TriggerQueueTerminalReason.Dispatched
                && entry.Dispatch?.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal
                && entry.WorkerLease?.ReleasedAtUtc is not null,
            TriggerQueueEntryState.DispatchRejected => entry.TerminalReason == TriggerQueueTerminalReason.DispatchRejected
                && entry.Dispatch?.Outcome == TriggerDispatchOutcome.Rejected
                && entry.WorkerLease?.ReleasedAtUtc is not null,
            TriggerQueueEntryState.NeedsReview => entry.TerminalReason == TriggerQueueTerminalReason.AmbiguousDispatch
                && entry.Dispatch?.Outcome == TriggerDispatchOutcome.NeedsReview
                && entry.WorkerLease?.ReleasedAtUtc is not null,
            _ => false
        };
    }

    private static bool IsWakeShapeValid(
        GovernedLoopWakeResultStatus status,
        GovernedLoopWakeDisposition? disposition)
        => status switch
        {
            GovernedLoopWakeResultStatus.Committed => disposition == GovernedLoopWakeDisposition.Committed,
            GovernedLoopWakeResultStatus.Duplicate => disposition is GovernedLoopWakeDisposition.Committed or GovernedLoopWakeDisposition.Duplicate,
            GovernedLoopWakeResultStatus.Late => disposition is not null,
            GovernedLoopWakeResultStatus.Stale => disposition is GovernedLoopWakeDisposition.Stale or GovernedLoopWakeDisposition.Failed,
            GovernedLoopWakeResultStatus.Cancelled => disposition is GovernedLoopWakeDisposition.Cancelled or GovernedLoopWakeDisposition.Failed,
            GovernedLoopWakeResultStatus.Expired => disposition is GovernedLoopWakeDisposition.Expired or GovernedLoopWakeDisposition.Failed,
            GovernedLoopWakeResultStatus.Failed => disposition == GovernedLoopWakeDisposition.Failed,
            GovernedLoopWakeResultStatus.Conflict => disposition is null
                or GovernedLoopWakeDisposition.Conflict
                or GovernedLoopWakeDisposition.Prepared
                or GovernedLoopWakeDisposition.AmbiguousAttempt
                or GovernedLoopWakeDisposition.Failed,
            GovernedLoopWakeResultStatus.Paused => disposition is null
                or GovernedLoopWakeDisposition.Paused
                or GovernedLoopWakeDisposition.Prepared
                or GovernedLoopWakeDisposition.AmbiguousAttempt,
            GovernedLoopWakeResultStatus.ReviewBlocked => disposition is null
                or GovernedLoopWakeDisposition.ReviewBlocked
                or GovernedLoopWakeDisposition.Prepared
                or GovernedLoopWakeDisposition.AmbiguousAttempt,
            GovernedLoopWakeResultStatus.AmbiguousAttempt => disposition is null or GovernedLoopWakeDisposition.Prepared or GovernedLoopWakeDisposition.AmbiguousAttempt,
            GovernedLoopWakeResultStatus.NotEligible
                or GovernedLoopWakeResultStatus.Invalid
                or GovernedLoopWakeResultStatus.NotFound
                or GovernedLoopWakeResultStatus.Unavailable => disposition is null
                    or GovernedLoopWakeDisposition.Prepared
                    or GovernedLoopWakeDisposition.AmbiguousAttempt,
            _ => false
        };

    private static int Compare(TriggerQueueEntry left, TriggerQueueEntry right)
    {
        var comparison = left.OrderKey.EligibleAtUtc.CompareTo(right.OrderKey.EligibleAtUtc);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.OrderKey.Priority.CompareTo(left.OrderKey.Priority);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.OrderKey.AcceptedAtUtc.CompareTo(right.OrderKey.AcceptedAtUtc);
        return comparison != 0
            ? comparison
            : string.Compare(left.OrderKey.DeliveryId, right.OrderKey.DeliveryId, StringComparison.Ordinal);
    }

    private static bool IsAdmissionShapeValid(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
        => status switch
        {
            TriggerAdmissionStatus.Admitted => reason == TriggerAdmissionReason.EvidenceAccepted,
            TriggerAdmissionStatus.Replayed => reason == TriggerAdmissionReason.ExactReplay,
            TriggerAdmissionStatus.Conflicting => reason == TriggerAdmissionReason.IdentityConflict,
            TriggerAdmissionStatus.NotYetEligible => reason == TriggerAdmissionReason.NotBefore,
            TriggerAdmissionStatus.Expired => reason is TriggerAdmissionReason.DeadlineExceeded or TriggerAdmissionReason.Expired,
            TriggerAdmissionStatus.Unauthorized => reason is TriggerAdmissionReason.StaleLoop
                or TriggerAdmissionReason.StaleAdapter
                or TriggerAdmissionReason.ActorMismatch
                or TriggerAdmissionReason.SurfaceMismatch
                or TriggerAdmissionReason.WorkspaceMismatch
                or TriggerAdmissionReason.RoleMismatch
                or TriggerAdmissionReason.AuthorityMismatch
                or TriggerAdmissionReason.StaleAuthority
                or TriggerAdmissionReason.AuthorityBoundary
                or TriggerAdmissionReason.StaleDelivery,
            TriggerAdmissionStatus.Invalid => reason == TriggerAdmissionReason.InvalidEnvelope,
            _ => false
        };

    private static bool IsWorkerEvidenceValid(TriggerQueueEntry entry)
    {
        if (entry.WorkerLease is { } lease
            && (!IsWorkerId(lease.WorkerId)
                || lease.Generation < 1
                || lease.RenewalCount is < 0 or > TriggerWorkerLimits.MaxLeaseRenewals
                || lease.AcquiredAtUtc.Offset != TimeSpan.Zero
                || lease.ExpiresAtUtc.Offset != TimeSpan.Zero
                || lease.AcquiredAtUtc < entry.RecordedAtUtc
                || lease.ExpiresAtUtc <= lease.AcquiredAtUtc
                || lease.ExpiresAtUtc - lease.AcquiredAtUtc > TriggerWorkerLimits.MaxLeaseOwnershipDuration
                || lease.ReleasedAtUtc is { } releasedAtUtc
                    && (releasedAtUtc.Offset != TimeSpan.Zero || releasedAtUtc < lease.AcquiredAtUtc)))
        {
            return false;
        }

        if (entry.Dispatch is { } dispatch)
        {
            var terminal = dispatch.Outcome is TriggerDispatchOutcome.Accepted
                or TriggerDispatchOutcome.Terminal
                or TriggerDispatchOutcome.Rejected
                or TriggerDispatchOutcome.NeedsReview;
            var requiresGovernedInvocation = dispatch.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal;
            if (entry.WorkerLease is not { } dispatchLease
                || !IsOperationId(dispatch.OperationId)
                || !string.Equals(dispatch.OperationId, TriggerWorkerRequestHash.ComputeOperationId(entry.DeliveryId, dispatchLease.Generation), StringComparison.Ordinal)
                || !IsHash(dispatch.RequestHash)
                || !IsHash(dispatch.AuthorityEvidenceHash)
                || dispatch.IntentRecordedAtUtc.Offset != TimeSpan.Zero
                || dispatch.IntentRecordedAtUtc < dispatchLease.AcquiredAtUtc
                || !Enum.IsDefined(dispatch.Outcome)
                || dispatch.Outcome == TriggerDispatchOutcome.None
                || terminal != (dispatch.OutcomeRecordedAtUtc is not null)
                || dispatch.OutcomeRecordedAtUtc is { } outcomeRecordedAtUtc
                    && (outcomeRecordedAtUtc.Offset != TimeSpan.Zero || outcomeRecordedAtUtc < dispatch.IntentRecordedAtUtc)
                || string.IsNullOrWhiteSpace(dispatch.Detail)
                || dispatch.Detail.Length > TriggerWorkerLimits.MaxOutcomeDetailCharacters
                || requiresGovernedInvocation != (dispatch.GovernedInvocation is not null)
                || dispatch.GovernedInvocation is { } governed
                    && (!IsOperationId(governed.OperationId)
                        || !string.Equals(governed.OperationId, dispatch.OperationId, StringComparison.Ordinal)
                        || !IsArtifactId(governed.RunId, TriggerWorkerLimits.MaxGovernedRunIdCharacters)
                        || !IsHash(governed.AdmissionRequestHash)
                        || !string.Equals(governed.LoopId, entry.LoopId, StringComparison.Ordinal)
                        || !IsHash(governed.LoopReferenceHash)))
            {
                return false;
            }
        }

        return entry.WorkerLease is not { } workerLease
            || entry.Dispatch is not null
            || entry.State is not (TriggerQueueEntryState.Dispatched
                or TriggerQueueEntryState.DispatchRejected
                or TriggerQueueEntryState.NeedsReview);
    }

    private static bool IsNonterminalReservationValid(TriggerQueueEntry entry)
        => entry.QueuedReservationBytes >= entry.SerializedEntryBytes;

    private static bool IsTerminalShapeValid(TriggerQueueEntry entry)
        => entry.TerminalAtUtc is { } terminalAtUtc
            && terminalAtUtc.Offset == TimeSpan.Zero
            && terminalAtUtc >= entry.RecordedAtUtc
            && entry.QueuedReservationBytes == 0
            && entry.RetainedReservationBytes == entry.SerializedEntryBytes;

    private static bool HasNoLiveWorker(TriggerQueueEntry entry)
        => entry.Dispatch is null
            && (entry.WorkerLease is null || entry.WorkerLease.ReleasedAtUtc is not null);

    private static bool IsWorkerId(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= TriggerWorkerLimits.MaxWorkerIdCharacters
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsOperationId(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= TriggerWorkerLimits.MaxOperationIdCharacters
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsArtifactId(string value, int maximumLength)
        => !string.IsNullOrEmpty(value)
            && value.Length <= maximumLength
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsNonterminal(TriggerQueueEntryState state)
        => state is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching;

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GovernedLoopLocalWorkRunnerOptions ValidateOptions(GovernedLoopLocalWorkRunnerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.TriggerWorkerId)
            || options.TriggerWorkerId.Length > TriggerWorkerLimits.MaxWorkerIdCharacters
            || options.TriggerWorkerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The trigger worker identity must be one bounded canonical token.", nameof(options));
        }

        if (options.TriggerLeaseDuration < TriggerWorkerLimits.MinLeaseDuration
            || options.TriggerLeaseDuration > TriggerWorkerLimits.MaxLeaseDuration
            || options.MaximumConsecutiveTriggerSelectionsPerLoop is < 1 or > TriggerWorkerLimits.MaxRecentLoopIds
            || options.CandidateReadLimit is < 1 or > GovernedLoopLocalWorkRunnerOptions.MaximumCandidateReadLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Trigger fairness, lease, and candidate bounds are invalid.");
        }

        return options with { };
    }

    private static GovernedLoopLocalWorkResult Result(GovernedLoopLocalWorkResultStatus status, string reasonCode)
        => new(status, reasonCode);

    private static string ScheduleReason(ScheduleEvaluationStatus status)
        => status switch
        {
            ScheduleEvaluationStatus.NotFound => "schedule-not-found",
            ScheduleEvaluationStatus.NotDue => "schedule-not-due",
            ScheduleEvaluationStatus.Disabled => "schedule-disabled",
            ScheduleEvaluationStatus.Exhausted => "schedule-exhausted",
            ScheduleEvaluationStatus.Skipped => "schedule-skipped",
            ScheduleEvaluationStatus.Deferred => "schedule-deferred",
            ScheduleEvaluationStatus.Queued => "schedule-queued",
            ScheduleEvaluationStatus.Replayed => "schedule-replayed",
            ScheduleEvaluationStatus.Rejected => "schedule-rejected",
            ScheduleEvaluationStatus.PermissionDenied => "schedule-permission-denied",
            ScheduleEvaluationStatus.ClockRollback => "schedule-clock-rollback",
            ScheduleEvaluationStatus.NeedsReview => "schedule-needs-review",
            ScheduleEvaluationStatus.BoundExceeded => "schedule-bound-exceeded",
            _ => "schedule-result"
        };

    private static string TriggerReason(TriggerWorkerMutationStatus status)
        => status switch
        {
            TriggerWorkerMutationStatus.Committed => "trigger-committed",
            TriggerWorkerMutationStatus.Replayed => "trigger-replayed",
            TriggerWorkerMutationStatus.NotFound => "trigger-not-found",
            TriggerWorkerMutationStatus.RevisionConflict => "trigger-revision-conflict",
            TriggerWorkerMutationStatus.StaleOwner => "trigger-stale-owner",
            _ => "trigger-result"
        };

    private static string WakeReason(GovernedLoopWakeResultStatus status)
        => status switch
        {
            GovernedLoopWakeResultStatus.Committed => "wake-committed",
            GovernedLoopWakeResultStatus.Duplicate => "wake-duplicate",
            GovernedLoopWakeResultStatus.NotEligible => "wake-not-eligible",
            GovernedLoopWakeResultStatus.Late => "wake-late",
            GovernedLoopWakeResultStatus.Stale => "wake-stale",
            GovernedLoopWakeResultStatus.Cancelled => "wake-cancelled",
            GovernedLoopWakeResultStatus.Expired => "wake-expired",
            GovernedLoopWakeResultStatus.Paused => "wake-paused",
            GovernedLoopWakeResultStatus.ReviewBlocked => "wake-review-blocked",
            GovernedLoopWakeResultStatus.AmbiguousAttempt => "wake-ambiguous-attempt",
            GovernedLoopWakeResultStatus.Failed => "wake-failed",
            GovernedLoopWakeResultStatus.NotFound => "wake-not-found",
            _ => "wake-result"
        };
}
