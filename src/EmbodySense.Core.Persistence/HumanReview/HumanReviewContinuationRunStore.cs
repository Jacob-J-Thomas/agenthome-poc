using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.HumanReview.Models;

namespace EmbodySense.Core.Persistence.HumanReview;

/// <summary>Atomically persists strict Human Review continuation state through the one canonical custom-loop run store.</summary>
/// <remarks>
/// This adapter owns no ledger, queue, timer, or recovery scan. It only transforms one already-admitted Human Review run by
/// publishing a wake, appending a claim, or recording one terminal result through whole-run compare-exchange. Every uncertain
/// publication result is reread from the canonical run before a caller can treat it as unavailable.
/// </remarks>
public sealed class HumanReviewContinuationRunStore
{
    private readonly ICustomLoopRunStore _runs;

    /// <summary>Initializes the continuation adapter over the one canonical custom-loop run store.</summary>
    /// <param name="runs">The run store that owns the complete persisted review transaction boundary.</param>
    public HumanReviewContinuationRunStore(ICustomLoopRunStore runs) => _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    /// <summary>Publishes the exact initial wake-only continuation state for one accepted approval reservation.</summary>
    /// <param name="runId">The exact canonical run identity.</param>
    /// <param name="expectedLifecycleVersion">The whole-run version observed before constructing <paramref name="continuation"/>.</param>
    /// <param name="continuation">The canonical wake-only continuation state to publish.</param>
    /// <param name="cancellationToken">Cancels before a definitive persistence result is available.</param>
    /// <returns>A committed, replayed, conflict, missing, invalid, unavailable, or quota result.</returns>
    public async Task<HumanReviewContinuationMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationState continuation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(continuation);
        var read = await TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        var current = read.Run;
        if (current is null) return Result(HumanReviewContinuationMutationStatus.NotFound);
        if (!TryGetApprovedReview(current, runId, out var review)) return Result(HumanReviewContinuationMutationStatus.Invalid);

        if (review.Continuation is not null)
        {
            if (!IsInitialPublication(review, continuation)) return Result(HumanReviewContinuationMutationStatus.Invalid, current);
            return IsContinuationDescendant(continuation, review.Continuation)
                ? Result(HumanReviewContinuationMutationStatus.Replayed, current)
                : Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        if (!IsInitialPublication(review, continuation)) return Result(HumanReviewContinuationMutationStatus.Invalid, current);
        if (current.LifecycleVersion != expectedLifecycleVersion) return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        return await UpdateAsync(current, review with { Continuation = continuation }, continuation.Wake.PublishedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends one exact claim only when the current continuation remains nonterminal and the contract proves a strict-expiry takeover.</summary>
    /// <param name="runId">The exact canonical run identity.</param>
    /// <param name="expectedLifecycleVersion">The whole-run version observed before constructing <paramref name="claim"/>.</param>
    /// <param name="claim">The exact canonical claim to append.</param>
    /// <param name="cancellationToken">Cancels before a definitive persistence result is available.</param>
    /// <returns>A committed, replayed, conflict, missing, invalid, unavailable, or quota result.</returns>
    public async Task<HumanReviewContinuationMutationResult> ClaimAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(claim);
        var read = await TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        var current = read.Run;
        if (current is null) return Result(HumanReviewContinuationMutationStatus.NotFound);
        if (!TryGetApprovedReview(current, runId, out var review) || review.Continuation is not { } continuation) return Result(HumanReviewContinuationMutationStatus.Invalid);

        if (continuation.Completion is not null || continuation.Retirement is not null)
        {
            return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        var active = continuation.Claims.IsDefaultOrEmpty ? null : continuation.Claims[^1];
        if (active is not null && string.Equals(active.ClaimId, claim.ClaimId, StringComparison.Ordinal))
        {
            return HumanReviewContinuationReplayClassifier.ClassifyClaim(active, claim) == HumanReviewContinuationReplayDisposition.ExactReplay
                ? Result(HumanReviewContinuationMutationStatus.Replayed, current)
                : Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        if (continuation.Claims.Any(item => string.Equals(item?.ClaimId, claim.ClaimId, StringComparison.Ordinal))) return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        if (current.LifecycleVersion != expectedLifecycleVersion) return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        var successor = HumanReviewContinuationContractHash.ApplyState(continuation with { Claims = [.. continuation.Claims, claim], StateHash = string.Empty });
        if (!IsClaimSuccessor(review, continuation, successor)) return Result(HumanReviewContinuationMutationStatus.Invalid, current);
        return await UpdateAsync(current, review with { Continuation = successor }, claim.ClaimedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one exact terminal completion only for the current appended claim and exact released receipt chain.</summary>
    /// <param name="runId">The exact canonical run identity.</param>
    /// <param name="expectedLifecycleVersion">The whole-run version observed before constructing <paramref name="completion"/>.</param>
    /// <param name="completion">The exact canonical completion to record.</param>
    /// <param name="cancellationToken">Cancels before a definitive persistence result is available.</param>
    /// <returns>A committed, replayed, conflict, missing, invalid, unavailable, or quota result.</returns>
    public async Task<HumanReviewContinuationMutationResult> CompleteAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(completion);
        var read = await TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        var current = read.Run;
        if (current is null) return Result(HumanReviewContinuationMutationStatus.NotFound);
        if (!TryGetApprovedReview(current, runId, out var review) || review.Continuation is not { } continuation) return Result(HumanReviewContinuationMutationStatus.Invalid);

        if (continuation.Completion is not null)
        {
            return HumanReviewContinuationReplayClassifier.ClassifyCompletion(continuation.Completion, completion) == HumanReviewContinuationReplayDisposition.ExactReplay
                ? Result(HumanReviewContinuationMutationStatus.Replayed, current)
                : Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        if (current.LifecycleVersion != expectedLifecycleVersion || continuation.Retirement is not null || continuation.Claims.IsDefaultOrEmpty || !Equals(continuation.Claims[^1] is { } active ? new HumanReviewContinuationClaimReference(active.ClaimId, active.ClaimHash) : null, completion.Claim))
        {
            return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        var successor = HumanReviewContinuationContractHash.ApplyState(continuation with { Completion = completion, StateHash = string.Empty });
        if (!IsTerminalSuccessor(review, continuation, successor)) return Result(HumanReviewContinuationMutationStatus.Invalid, current);
        return await UpdateAsync(current, review with { Continuation = successor }, completion.CompletedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one fail-closed retirement only when the supplied exact active claim still fences the current continuation state.</summary>
    /// <param name="runId">The exact canonical run identity.</param>
    /// <param name="expectedLifecycleVersion">The whole-run version observed before constructing <paramref name="retirement"/>.</param>
    /// <param name="claim">The exact active claim that must still be the final append-only claim.</param>
    /// <param name="retirement">The exact canonical non-completion retirement to record.</param>
    /// <param name="cancellationToken">Cancels before a definitive persistence result is available.</param>
    /// <returns>A committed, replayed, conflict, missing, invalid, unavailable, or quota result.</returns>
    public async Task<HumanReviewContinuationMutationResult> RetireAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationClaimReference claim, HumanReviewContinuationRetirement retirement, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(retirement);
        var read = await TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        var current = read.Run;
        if (current is null) return Result(HumanReviewContinuationMutationStatus.NotFound);
        if (!TryGetApprovedReview(current, runId, out var review) || review.Continuation is not { } continuation) return Result(HumanReviewContinuationMutationStatus.Invalid);

        if (continuation.Claims.IsDefaultOrEmpty
            || !Equals(new HumanReviewContinuationClaimReference(continuation.Claims[^1].ClaimId, continuation.Claims[^1].ClaimHash), claim))
        {
            return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        if (continuation.Retirement is not null)
        {
            return HumanReviewContinuationReplayClassifier.ClassifyRetirement(continuation.Retirement, retirement) == HumanReviewContinuationReplayDisposition.ExactReplay
                ? Result(HumanReviewContinuationMutationStatus.Replayed, current)
                : Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        if (current.LifecycleVersion != expectedLifecycleVersion
            || continuation.Completion is not null)
        {
            return Result(HumanReviewContinuationMutationStatus.Conflict, current);
        }

        var successor = HumanReviewContinuationContractHash.ApplyState(continuation with { Retirement = retirement, StateHash = string.Empty });
        if (!IsTerminalSuccessor(review, continuation, successor)) return Result(HumanReviewContinuationMutationStatus.Invalid, current);
        return await UpdateAsync(current, review with { Continuation = successor }, retirement.RetiredAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewContinuationMutationResult> UpdateAsync(CustomLoopRunRecord current, HumanReviewRunState review, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken)
    {
        CustomLoopRunRecord next;
        try
        {
            next = current with { LifecycleVersion = checked(current.LifecycleVersion + 1), UpdatedAtUtc = updatedAtUtc, HumanReview = review };
            if (!CustomLoopRunValidator.ValidateUpdate(current, next).IsValid) return Result(HumanReviewContinuationMutationStatus.Invalid, current);
        }
        catch
        {
            return Result(HumanReviewContinuationMutationStatus.Invalid, current);
        }

        try
        {
            var committed = await _runs.UpdateAsync(next, current.LifecycleVersion, cancellationToken).ConfigureAwait(false);
            if (committed.Status == CustomLoopRunStoreStatus.Updated && committed.Run is not null && CustomLoopRunValidator.HasSameDurableVersion(next, committed.Run))
            {
                return Result(HumanReviewContinuationMutationStatus.Committed, committed.Run);
            }

            if (committed.Status == CustomLoopRunStoreStatus.LimitExceeded) return Result(HumanReviewContinuationMutationStatus.LimitExceeded);
            if (committed.Status == CustomLoopRunStoreStatus.NotFound) return Result(HumanReviewContinuationMutationStatus.NotFound);
            return await ReconcileAsync(next).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await ReconcileAsync(next).ConfigureAwait(false);
        }
    }

    private async Task<HumanReviewContinuationMutationResult> ReconcileAsync(CustomLoopRunRecord expected)
    {
        try
        {
            var recovered = await _runs.GetAsync(expected.Id, CancellationToken.None).ConfigureAwait(false);
            if (recovered is null) return Result(HumanReviewContinuationMutationStatus.NotFound);
            return CustomLoopRunValidator.HasSameDurableVersion(expected, recovered)
                || TryGetApprovedReview(expected, expected.Id, out var expectedReview)
                && TryGetApprovedReview(recovered, expected.Id, out var recoveredReview)
                && IsContinuationDescendant(expectedReview.Continuation, recoveredReview.Continuation)
                ? Result(HumanReviewContinuationMutationStatus.Replayed, recovered)
                : Result(HumanReviewContinuationMutationStatus.Conflict, recovered);
        }
        catch
        {
            return Result(HumanReviewContinuationMutationStatus.Unavailable);
        }
    }

    private async Task<(CustomLoopRunRecord? Run, HumanReviewContinuationMutationStatus? Failure)> TryReadAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            return (await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return (null, HumanReviewContinuationMutationStatus.Invalid);
        }
        catch
        {
            return (null, HumanReviewContinuationMutationStatus.Unavailable);
        }
    }

    private static bool TryGetApprovedReview(CustomLoopRunRecord run, string runId, out HumanReviewRunState review)
    {
        review = null!;
        try
        {
            if (!string.Equals(run.Id, runId, StringComparison.Ordinal)
                || run.HumanReview is not { } candidate
                || candidate.ContinuationReservation is null
                || candidate.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve
                || !CustomLoopRunValidator.Validate(run).IsValid)
            {
                return false;
            }

            review = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInitialPublication(HumanReviewRunState review, HumanReviewContinuationState continuation)
        => continuation.Claims.IsEmpty
            && continuation.Completion is null
            && continuation.Retirement is null
            && HumanReviewContinuationStateTransitionValidator.ValidateTransition(review.Request, review.ContinuationReservation, null, continuation).IsValid;

    private static bool IsClaimSuccessor(HumanReviewRunState review, HumanReviewContinuationState previous, HumanReviewContinuationState successor)
        => HumanReviewContinuationStateTransitionValidator.ValidateTransition(review.Request, review.ContinuationReservation, previous, successor).IsValid;

    private static bool IsTerminalSuccessor(HumanReviewRunState review, HumanReviewContinuationState previous, HumanReviewContinuationState successor)
        => HumanReviewContinuationStateTransitionValidator.ValidateTransition(review.Request, review.ContinuationReservation, previous, successor).IsValid;

    private static bool IsContinuationDescendant(HumanReviewContinuationState? expected, HumanReviewContinuationState? retained)
    {
        if (expected is null
            || retained is null
            || !HumanReviewContinuationContractHash.MatchesState(expected)
            || !HumanReviewContinuationContractHash.MatchesState(retained)
            || HumanReviewContinuationReplayClassifier.ClassifyWake(expected.Wake, retained.Wake) != HumanReviewContinuationReplayDisposition.ExactReplay
            || expected.Claims.Length > retained.Claims.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Claims.Length; index++)
        {
            if (HumanReviewContinuationReplayClassifier.ClassifyClaim(expected.Claims[index], retained.Claims[index]) != HumanReviewContinuationReplayDisposition.ExactReplay) return false;
        }

        if (expected.Completion is not null)
        {
            return retained.Retirement is null
                && HumanReviewContinuationReplayClassifier.ClassifyCompletion(expected.Completion, retained.Completion) == HumanReviewContinuationReplayDisposition.ExactReplay;
        }

        return expected.Retirement is null || retained.Completion is null
            && HumanReviewContinuationReplayClassifier.ClassifyRetirement(expected.Retirement, retained.Retirement) == HumanReviewContinuationReplayDisposition.ExactReplay;
    }

    private static HumanReviewContinuationMutationResult Result(HumanReviewContinuationMutationStatus status, CustomLoopRunRecord? run = null) => new(status, run);
}
