using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Reselects bounded retained schedule admissions without minting a new queue or run identity.</summary>
internal sealed class GovernedLoopScheduleAdmissionRetryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _pageSize;
    private readonly ICustomLoopRunStore _runs;
    private readonly ITriggerQueueQueryPort _triggerQueue;
    private readonly TriggerWorkerService _triggerWorker;
    private TriggerDeliveryId? _cursor;

    internal GovernedLoopScheduleAdmissionRetryService(
        ICustomLoopRunStore runs,
        ITriggerQueueQueryPort triggerQueue,
        TriggerWorkerService triggerWorker,
        int pageSize)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _triggerQueue = triggerQueue ?? throw new ArgumentNullException(nameof(triggerQueue));
        _triggerWorker = triggerWorker ?? throw new ArgumentNullException(nameof(triggerWorker));
        if (pageSize is < 1 or > GovernedLoopLocalWorkRunnerOptions.MaximumCandidateReadLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _pageSize = pageSize;
    }

    internal async Task<GovernedLoopLocalWorkResult> RetryOnceAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-clock-corrupt");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ScheduleRunAdmissionEvidence> page;
            try
            {
                page = await _runs.ListPendingScheduleAdmissionsAsync(_cursor, _pageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException)
            {
                return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-evidence-corrupt");
            }
            catch
            {
                return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-retry-evidence-unavailable");
            }

            if (page is null || page.Count > _pageSize)
            {
                return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-page-corrupt");
            }

            if (page.Count == 0)
            {
                _cursor = null;
                return Result(GovernedLoopLocalWorkResultStatus.Empty, "schedule-retry-empty");
            }

            foreach (var evidence in page)
            {
                if (!TryValidateCandidate(evidence, out var envelope, out var attempt))
                {
                    return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-candidate-corrupt");
                }

                _cursor = envelope!.DeliveryId;
                var result = await RetryCandidateAsync(evidence!, envelope, attempt!, observedAtUtc, cancellationToken).ConfigureAwait(false);
                if (result.Status != GovernedLoopLocalWorkResultStatus.Empty)
                {
                    return result;
                }
            }

            if (page.Count < _pageSize)
            {
                _cursor = null;
            }

            return Result(GovernedLoopLocalWorkResultStatus.Empty, "schedule-retry-blocked");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GovernedLoopLocalWorkResult> RetryCandidateAsync(
        ScheduleRunAdmissionEvidence evidence,
        TriggerDeliveryEnvelope envelope,
        ScheduleRunAdmissionAttempt attempt,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (observedAtUtc < attempt.RecordedAtUtc)
        {
            return Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, "schedule-retry-clock-rollback");
        }

        CustomLoopRunMonitor? blocker;
        try
        {
            blocker = await _runs.GetMonitorAsync(attempt.BlockingRunId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-blocker-corrupt");
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-retry-blocker-unavailable");
        }

        if (blocker is null)
        {
            return Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, "schedule-retry-blocker-missing");
        }

        if (!IsTerminal(blocker.Summary))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Empty, "schedule-retry-blocker-active");
        }

        TriggerQueueSnapshot? snapshot;
        try
        {
            snapshot = await _triggerQueue.GetSnapshotAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-retry-queue-unavailable");
        }

        if (!GovernedLoopLocalWorkRunner.IsValidTriggerSnapshot(snapshot))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-queue-corrupt");
        }

        var matches = snapshot!.Entries.Where(entry => entry.DeliveryId.Equals(envelope.DeliveryId)).ToArray();
        if (matches.Length != 1 || !MatchesTerminalOverlapRejection(matches[0], evidence, envelope, attempt))
        {
            return Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, "schedule-retry-queue-mismatch");
        }

        var dispatch = await _triggerWorker.RetryScheduleOverlapAsync(
            envelope,
            matches[0].Dispatch!,
            observedAtUtc,
            cancellationToken).ConfigureAwait(false);
        ScheduleRunAdmissionEvidence? refreshed;
        try
        {
            refreshed = await _runs.GetScheduleAdmissionAsync(envelope.DeliveryId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-result-corrupt");
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "schedule-retry-result-unavailable");
        }

        if (!MatchesRefreshedEvidence(evidence, refreshed, attempt))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-result-mismatch");
        }

        var disposition = refreshed!.Attempts[^1].Disposition;
        return dispatch.Outcome switch
        {
            TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal
                when disposition == ScheduleRunAdmissionDisposition.RunCreated
                => Result(GovernedLoopLocalWorkResultStatus.Completed, "schedule-retry-materialized"),
            TriggerDispatchOutcome.Rejected
                when disposition is ScheduleRunAdmissionDisposition.OverlapDeferred or ScheduleRunAdmissionDisposition.OverlapSerialized
                => Result(GovernedLoopLocalWorkResultStatus.Completed, "schedule-retry-retained"),
            TriggerDispatchOutcome.NeedsReview
                => Result(GovernedLoopLocalWorkResultStatus.AttentionRequired, "schedule-retry-needs-review"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "schedule-retry-outcome-corrupt")
        };
    }

    private static bool TryValidateCandidate(
        ScheduleRunAdmissionEvidence? evidence,
        out TriggerDeliveryEnvelope? envelope,
        out ScheduleRunAdmissionAttempt? attempt)
    {
        envelope = null;
        attempt = null;
        if (!ScheduleRunAdmissionEvidenceValidator.IsValid(evidence)
            || evidence!.Attempts.Count == 0
            || evidence.Attempts[^1] is not { } latest
            || latest.Disposition is not (ScheduleRunAdmissionDisposition.OverlapDeferred or ScheduleRunAdmissionDisposition.OverlapSerialized)
            || !CustomLoopArtifactIdentifier.IsValid(latest.BlockingRunId)
            || !TriggerDeliveryJson.TryDeserialize(evidence.CanonicalEnvelope, out envelope, out _)
            || envelope is null
            || envelope.Kind != TriggerKind.Time
            || envelope.ScheduleExecutionDirective is null
            || !TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _)
            || !string.Equals(envelopeHash, evidence.CanonicalEnvelopeHash, StringComparison.Ordinal)
            || !string.Equals(envelope.Loop.LoopId, evidence.LoopId, StringComparison.Ordinal))
        {
            envelope = null;
            return false;
        }

        attempt = latest;
        return true;
    }

    private static bool MatchesTerminalOverlapRejection(
        TriggerQueueEntry entry,
        ScheduleRunAdmissionEvidence evidence,
        TriggerDeliveryEnvelope envelope,
        ScheduleRunAdmissionAttempt attempt)
    {
        var dispatch = entry.Dispatch;
        var lease = entry.WorkerLease;
        return entry.DeduplicationId.Equals(envelope.DeduplicationId)
            && string.Equals(entry.LoopId, evidence.LoopId, StringComparison.Ordinal)
            && string.Equals(entry.CanonicalEnvelopeHash, evidence.CanonicalEnvelopeHash, StringComparison.Ordinal)
            && entry.State == TriggerQueueEntryState.DispatchRejected
            && entry.TerminalReason == TriggerQueueTerminalReason.DispatchRejected
            && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
            && lease?.ReleasedAtUtc is not null
            && dispatch?.Outcome == TriggerDispatchOutcome.Rejected
            && dispatch.OutcomeRecordedAtUtc is not null
            && dispatch.GovernedInvocation is null
            && string.Equals(dispatch.OperationId, attempt.AdmissionOperationId, StringComparison.Ordinal)
            && string.Equals(
                dispatch.RequestHash,
                TriggerWorkerRequestHash.Compute(envelope, lease, dispatch.AuthorityEvidenceHash),
                StringComparison.Ordinal);
    }

    private static bool MatchesRefreshedEvidence(
        ScheduleRunAdmissionEvidence original,
        ScheduleRunAdmissionEvidence? refreshed,
        ScheduleRunAdmissionAttempt originalAttempt)
        => ScheduleRunAdmissionEvidenceValidator.IsValid(refreshed)
            && string.Equals(refreshed!.CanonicalEnvelope, original.CanonicalEnvelope, StringComparison.Ordinal)
            && string.Equals(refreshed.CanonicalEnvelopeHash, original.CanonicalEnvelopeHash, StringComparison.Ordinal)
            && string.Equals(refreshed.LoopId, original.LoopId, StringComparison.Ordinal)
            && refreshed.Attempts.Count >= original.Attempts.Count
            && refreshed.Attempts[^1] is { } latest
            && string.Equals(latest.AdmissionOperationId, originalAttempt.AdmissionOperationId, StringComparison.Ordinal)
            && string.Equals(latest.CandidateRunId, originalAttempt.CandidateRunId, StringComparison.Ordinal);

    private static bool IsTerminal(CustomLoopRunSummary summary)
        => summary.IsDeleted
            || summary.CompletedAtUtc is not null
                && summary.Status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled;

    private static GovernedLoopLocalWorkResult Result(GovernedLoopLocalWorkResultStatus status, string reasonCode)
        => new(status, reasonCode);
}
