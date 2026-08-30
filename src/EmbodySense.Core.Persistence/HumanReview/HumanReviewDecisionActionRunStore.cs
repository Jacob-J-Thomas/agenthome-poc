using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.HumanReview;

/// <summary>Persists and recovers non-approval Human Review actions only through the canonical custom-loop run store.</summary>
/// <remarks>This adapter owns no queue, index, timer, or second ledger. Every mutation replaces one complete run through its existing optimistic compare-exchange boundary and response-unknown results are classified by canonical reread.</remarks>
public sealed class HumanReviewDecisionActionRunStore : IHumanReviewDecisionActionRecoveryStore
{
    private readonly ICustomLoopRunStore _runs;
    private readonly IGovernedLoopGraphRevisionStore? _graphs;

    /// <summary>Initializes the publication and mutation adapter when no graph reread source has been composed yet.</summary>
    /// <remarks>Claimed-candidate rereads fail closed as unavailable until the host supplies the immutable graph source through the other constructor.</remarks>
    public HumanReviewDecisionActionRunStore(ICustomLoopRunStore runs)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    /// <summary>Initializes the action adapter over the sole run store and immutable graph reread source.</summary>
    public HumanReviewDecisionActionRunStore(ICustomLoopRunStore runs, IGovernedLoopGraphRevisionStore graphs)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewDecisionActionState action, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId) || action is null) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        var read = await ReadRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        if (read.Run is not { } current) return Result(HumanReviewDecisionActionStoreMutationStatus.NotFound);
        if (!TryAction(current, runId, new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash), out var review, out var index, out var retained)) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        if (retained.Wake is not null) return SameAction(retained, action) ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (current.LifecycleVersion != expectedLifecycleVersion || !HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(review.Request, retained, action).IsValid) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        return await UpdateAsync(current, review, index, action, action.Wake!.PublishedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionStoreMutationResult> ClaimAsync(HumanReviewDecisionActionClaimIntent intent, CancellationToken cancellationToken = default)
    {
        if (!Valid(intent)) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        var read = await ReadRunAsync(intent.Candidate.RunId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        if (read.Run is not { } current) return Result(HumanReviewDecisionActionStoreMutationStatus.NotFound);
        if (!TryAction(current, intent.Candidate.RunId, intent.Candidate.Reservation, out var review, out var index, out var retained) || retained.Wake is null) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (retained.Completion is not null || retained.Retirement is not null || !Matches(retained, intent.Candidate) || current.LifecycleVersion != intent.Candidate.ExpectedLifecycleVersion) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        var active = retained.Claims.IsDefaultOrEmpty ? null : retained.Claims[^1];
        if (active is not null && active.ClaimId == intent.Claim.ClaimId) return active.ClaimHash == intent.Claim.ClaimHash ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (retained.Claims.Any(claim => claim.ClaimId == intent.Claim.ClaimId)) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        var successor = HumanReviewDecisionActionContractHash.ApplyState(retained with { Claims = [.. retained.Claims, intent.Claim], StateHash = string.Empty });
        if (!HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(review.Request, retained, successor).IsValid) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        return await UpdateAsync(current, review, index, successor, intent.Claim.ClaimedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionStoreMutationResult> CompleteAsync(HumanReviewDecisionActionCompletionIntent intent, HumanReviewDecisionActionCompletion completion, CancellationToken cancellationToken = default)
    {
        if (!Valid(intent) || completion is null) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        var read = await ReadRunAsync(intent.RunId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        if (read.Run is not { } current) return Result(HumanReviewDecisionActionStoreMutationStatus.NotFound);
        if (!TryAction(current, intent.RunId, intent.Reservation, out var review, out var index, out var retained) || retained.Wake is null) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (retained.Completion is not null) return retained.Completion.CompletionHash == completion.CompletionHash ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (current.LifecycleVersion != intent.ExpectedLifecycleVersion || retained.Retirement is not null || retained.Claims.IsDefaultOrEmpty || !Matches(intent, retained, completion)) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        var successor = HumanReviewDecisionActionContractHash.ApplyState(retained with { Completion = completion, StateHash = string.Empty });
        if (!HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(review.Request, retained, successor).IsValid) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        return await UpdateAsync(current, review, index, successor, completion.CompletedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionStoreMutationResult> RetireAsync(HumanReviewDecisionActionRetirementIntent intent, HumanReviewDecisionActionRetirement retirement, CancellationToken cancellationToken = default)
    {
        if (!Valid(intent) || retirement is null) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        var read = await ReadRunAsync(intent.RunId, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure) return Result(failure);
        if (read.Run is not { } current) return Result(HumanReviewDecisionActionStoreMutationStatus.NotFound);
        if (!TryAction(current, intent.RunId, intent.Reservation, out var review, out var index, out var retained) || retained.Wake is null) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (retained.Retirement is not null) return retained.Retirement.RetirementHash == retirement.RetirementHash ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        if (current.LifecycleVersion != intent.ExpectedLifecycleVersion || retained.Completion is not null || !Matches(intent, retained, retirement)) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        var successor = HumanReviewDecisionActionContractHash.ApplyState(retained with { Retirement = retirement, StateHash = string.Empty });
        if (!HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(review.Request, retained, successor).IsValid) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        return await UpdateAsync(current, review, index, successor, retirement.RetiredAtUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > CustomLoopLimits.MaxRecentRunsPageSize || !Utc(observedAtUtc)) return Page(HumanReviewDecisionActionRecoveryPageStatus.Invalid);
        CustomLoopRunPage source;
        try { source = await _runs.ListPageAsync(new CustomLoopRunPageRequest(maximumCount, null, scanCursor), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ArgumentException) { return Page(HumanReviewDecisionActionRecoveryPageStatus.Invalid); }
        catch (FormatException) { return Page(HumanReviewDecisionActionRecoveryPageStatus.Invalid); }
        catch { return Page(HumanReviewDecisionActionRecoveryPageStatus.Unavailable); }
        if (source?.Items is null || source.Items.Count > maximumCount) return Page(HumanReviewDecisionActionRecoveryPageStatus.Invalid);
        var candidates = new List<HumanReviewDecisionActionRecoveryCandidate>();
        foreach (var summary in source.Items)
        {
            if (summary is null || string.IsNullOrWhiteSpace(summary.Id)) return Page(HumanReviewDecisionActionRecoveryPageStatus.Invalid);
            var read = await ReadRunAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            if (read.Failure == HumanReviewDecisionActionStoreMutationStatus.Invalid) return Page(HumanReviewDecisionActionRecoveryPageStatus.Invalid);
            if (read.Failure is not null) return Page(HumanReviewDecisionActionRecoveryPageStatus.Unavailable);
            if (read.Run is { } run) candidates.AddRange(Candidates(run, observedAtUtc));
        }
        return new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, candidates, source.ContinuationCursor ?? (source.Items.Count == 0 ? null : TailCursor(source.Items[^1])), source.ContinuationCursor is not null);
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionCandidateReadResult> ReadAsync(HumanReviewDecisionActionCandidateQuery query, CancellationToken cancellationToken = default)
    {
        if (!Valid(query)) return new(HumanReviewDecisionActionCandidateReadStatus.Corrupt);
        var read = await ReadRunAsync(query.RunId, cancellationToken).ConfigureAwait(false);
        if (read.Failure == HumanReviewDecisionActionStoreMutationStatus.Invalid) return new(HumanReviewDecisionActionCandidateReadStatus.Corrupt);
        if (read.Failure is not null) return new(HumanReviewDecisionActionCandidateReadStatus.Unavailable);
        if (read.Run is null) return new(HumanReviewDecisionActionCandidateReadStatus.Missing);
        if (!TryAction(read.Run, query.RunId, query.Reservation, out _, out _, out var action) || action.Wake is null || action.Claims.IsDefaultOrEmpty || !Equals(query.Decision, action.Reservation.Decision) || !Equals(query.Wake, Reference(action.Wake)) || !Equals(query.Claim, Reference(action.Claims[^1])) || query.ExpectedGeneration != action.ExpectedGeneration) return new(HumanReviewDecisionActionCandidateReadStatus.Stale);
        if (read.Run.SequentialAdapterBinding is not { } binding) return new(HumanReviewDecisionActionCandidateReadStatus.Corrupt);
        if (_graphs is null) return new(HumanReviewDecisionActionCandidateReadStatus.Unavailable);
        try
        {
            var artifactRead = await _graphs.ReadArtifactAsync(binding.ExecutionBinding.Revision, cancellationToken).ConfigureAwait(false);
            if (artifactRead is null || artifactRead.Status is GovernedLoopRevisionStoreReadStatus.Unavailable or GovernedLoopRevisionStoreReadStatus.Ambiguous) return new(HumanReviewDecisionActionCandidateReadStatus.Unavailable);
            if (artifactRead.Status != GovernedLoopRevisionStoreReadStatus.Ready || artifactRead.Artifact is null) return new(HumanReviewDecisionActionCandidateReadStatus.Missing);
            if (artifactRead.Artifact.ArtifactHash != binding.GraphArtifactHash || artifactRead.Artifact.LayoutHash != binding.GraphLayoutHash) return new(HumanReviewDecisionActionCandidateReadStatus.Stale);
            return new(HumanReviewDecisionActionCandidateReadStatus.Current, new HumanReviewDecisionActionCandidate(new HumanReviewContinuationCandidate(read.Run, artifactRead.Artifact, null, null), action, action.Claims[^1]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(HumanReviewDecisionActionCandidateReadStatus.Unavailable); }
    }

    private async Task<HumanReviewDecisionActionStoreMutationResult> UpdateAsync(CustomLoopRunRecord current, HumanReviewRunState review, int index, HumanReviewDecisionActionState action, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken)
    {
        CustomLoopRunRecord next;
        try
        {
            var nextReview = review with { DecisionActions = review.DecisionActions.SetItem(index, action) };
            next = current with { LifecycleVersion = checked(current.LifecycleVersion + 1), UpdatedAtUtc = updatedAtUtc < current.UpdatedAtUtc ? current.UpdatedAtUtc : updatedAtUtc, HumanReview = nextReview };
            if (!CustomLoopRunValidator.ValidateUpdate(current, next).IsValid) return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        }
        catch { return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid); }
        try
        {
            var committed = await _runs.UpdateAsync(next, current.LifecycleVersion, cancellationToken).ConfigureAwait(false);
            if (committed.Status == CustomLoopRunStoreStatus.Updated && committed.Run is not null && CustomLoopRunValidator.HasSameDurableVersion(next, committed.Run)) return Result(HumanReviewDecisionActionStoreMutationStatus.Committed);
            if (committed.Status == CustomLoopRunStoreStatus.LimitExceeded) return Result(HumanReviewDecisionActionStoreMutationStatus.LimitExceeded);
            if (committed.Status == CustomLoopRunStoreStatus.NotFound) return Result(HumanReviewDecisionActionStoreMutationStatus.NotFound);
            return await ReconcileAsync(next, action).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return await ReconcileAsync(next, action).ConfigureAwait(false); }
    }

    private async Task<HumanReviewDecisionActionStoreMutationResult> ReconcileAsync(CustomLoopRunRecord expected, HumanReviewDecisionActionState action)
    {
        try
        {
            var recovered = await _runs.GetAsync(expected.Id, CancellationToken.None).ConfigureAwait(false);
            if (recovered is null) return Result(HumanReviewDecisionActionStoreMutationStatus.NotFound);
            var retained = recovered.HumanReview?.DecisionActions.FirstOrDefault(value => value.Reservation.ReservationHash == action.Reservation.ReservationHash);
            return CustomLoopRunValidator.HasSameDurableVersion(expected, recovered) || retained is not null && Descends(action, retained) ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
        }
        catch { return Result(HumanReviewDecisionActionStoreMutationStatus.Unavailable); }
    }

    private async Task<(CustomLoopRunRecord? Run, HumanReviewDecisionActionStoreMutationStatus? Failure)> ReadRunAsync(string runId, CancellationToken cancellationToken)
    {
        try { return (await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false), null); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FormatException) { return (null, HumanReviewDecisionActionStoreMutationStatus.Invalid); }
        catch { return (null, HumanReviewDecisionActionStoreMutationStatus.Unavailable); }
    }

    private static bool TryAction(CustomLoopRunRecord run, string runId, HumanReviewDecisionActionReservationReference reservation, out HumanReviewRunState review, out int index, out HumanReviewDecisionActionState action)
    {
        review = null!; action = null!; index = -1;
        try
        {
            if (run.Id != runId || run.HumanReview is not { } candidate || !CustomLoopRunValidator.Validate(run).IsValid) return false;
            for (var current = 0; current < candidate.DecisionActions.Length; current++)
            {
                var value = candidate.DecisionActions[current];
                if (value.Reservation.ReservationId == reservation.ReservationId && value.Reservation.ReservationHash == reservation.ReservationHash) { review = candidate; index = current; action = value; return true; }
            }
            return false;
        }
        catch { return false; }
    }

    private static IEnumerable<HumanReviewDecisionActionRecoveryCandidate> Candidates(CustomLoopRunRecord run, DateTimeOffset observedAtUtc)
    {
        if (!CustomLoopRunValidator.Validate(run).IsValid || run.IsTerminal || run.Status != CustomLoopRunStatus.Paused || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked || run.HumanReview is not { } review) return [];
        var candidates = new List<HumanReviewDecisionActionRecoveryCandidate>();
        foreach (var action in review.DecisionActions)
        {
            if (action is null || action.Wake is null || action.Completion is not null || action.Retirement is not null) continue;
            var priorClaim = action.Claims.IsDefaultOrEmpty ? null : action.Claims[^1];
            if (priorClaim is not null && observedAtUtc <= priorClaim.LeaseExpiresAtUtc) continue;
            candidates.Add(new(run.Id, run.LifecycleVersion, new(review.Request.RequestId, review.Request.RequestHash), action.Reservation.Decision, Reference(action.Wake), action.ExpectedGeneration, action.Wake.ExpiresAtUtc, Reference(action.Reservation), priorClaim is null ? null : Reference(priorClaim)));
        }

        return candidates;
    }

    private static bool Valid(HumanReviewDecisionActionClaimIntent? intent) => intent is not null && intent.Candidate is not null && intent.Claim is not null && Equals(intent.Candidate.Wake, intent.Claim.Wake) && Equals(intent.Candidate.Reservation, intent.Claim.Reservation) && intent.Candidate.ExpectedGeneration == intent.Claim.ExpectedGeneration;
    private static bool Valid(HumanReviewDecisionActionCompletionIntent? value) => value is not null && CustomLoopArtifactIdentifier.IsValid(value.RunId) && value.ExpectedLifecycleVersion >= 1 && value.ExpectedGeneration >= 1 && Valid(value.Wake) && Valid(value.Claim) && Valid(value.Reservation);
    private static bool Valid(HumanReviewDecisionActionRetirementIntent? value) => value is not null && CustomLoopArtifactIdentifier.IsValid(value.RunId) && value.ExpectedLifecycleVersion >= 1 && value.ExpectedGeneration >= 1 && Valid(value.Wake) && Valid(value.Reservation) && (value.Claim is null || Valid(value.Claim)) && Enum.IsDefined(value.Outcome) && value.Outcome is not HumanReviewContinuationOutcome.Unknown and not HumanReviewContinuationOutcome.Completed && Enum.IsDefined(value.Reason) && value.Reason != HumanReviewDecisionActionRetirementReason.Unknown;
    private static bool Valid(HumanReviewDecisionActionCandidateQuery? value) => value is not null && CustomLoopArtifactIdentifier.IsValid(value.RunId) && value.Request is not null && value.Decision is not null && value.Wake is not null && value.Claim is not null && value.Reservation is not null && value.ExpectedGeneration >= 1;
    private static bool Valid(HumanReviewDecisionActionWakeReference? value) => value is not null && HumanReviewIdentifier.IsValid(value.WakeId) && HumanReviewContractHash.IsSha256(value.WakeHash);
    private static bool Valid(HumanReviewDecisionActionClaimReference? value) => value is not null && HumanReviewIdentifier.IsValid(value.ClaimId) && HumanReviewContractHash.IsSha256(value.ClaimHash);
    private static bool Valid(HumanReviewDecisionActionReservationReference? value) => value is not null && HumanReviewIdentifier.IsValid(value.ReservationId) && HumanReviewContractHash.IsSha256(value.ReservationHash);
    private static bool Matches(HumanReviewDecisionActionState action, HumanReviewDecisionActionRecoveryCandidate candidate) => action.Wake is not null && action.Wake.WakeHash == candidate.Wake.WakeHash && action.ExpectedGeneration == candidate.ExpectedGeneration && action.Wake.ExpiresAtUtc == candidate.WakeExpiresAtUtc && Equals(action.Reservation.Decision, candidate.Decision) && (candidate.PriorClaim is null ? action.Claims.IsDefaultOrEmpty : !action.Claims.IsDefaultOrEmpty && Equals(Reference(action.Claims[^1]), candidate.PriorClaim));
    private static bool Matches(HumanReviewDecisionActionCompletionIntent intent, HumanReviewDecisionActionState action, HumanReviewDecisionActionCompletion completion) => intent.ExpectedGeneration == action.ExpectedGeneration && Equals(intent.Wake, Reference(action.Wake!)) && Equals(intent.Claim, Reference(action.Claims[^1])) && Equals(intent.Reservation, Reference(action.Reservation)) && Equals(intent.Wake, completion.Wake) && Equals(intent.Claim, completion.Claim) && Equals(intent.Reservation, completion.Reservation) && completion.ExpectedGeneration == intent.ExpectedGeneration;
    private static bool Matches(HumanReviewDecisionActionRetirementIntent intent, HumanReviewDecisionActionState action, HumanReviewDecisionActionRetirement retirement) => intent.ExpectedGeneration == action.ExpectedGeneration && Equals(intent.Wake, Reference(action.Wake!)) && Equals(intent.Reservation, Reference(action.Reservation)) && Equals(intent.Wake, retirement.Wake) && Equals(intent.Reservation, retirement.Reservation) && retirement.ExpectedGeneration == intent.ExpectedGeneration && (intent.Claim is null ? intent.Outcome == HumanReviewContinuationOutcome.Expired && action.Claims.IsDefaultOrEmpty : !action.Claims.IsDefaultOrEmpty && Equals(intent.Claim, Reference(action.Claims[^1])) && retirement.RetiredAtUtc < action.Claims[^1].LeaseExpiresAtUtc);
    private static bool SameAction(HumanReviewDecisionActionState expected, HumanReviewDecisionActionState actual) => expected.StateHash == actual.StateHash || Descends(expected, actual);
    private static bool Descends(HumanReviewDecisionActionState expected, HumanReviewDecisionActionState actual) => expected.Reservation.ReservationHash == actual.Reservation.ReservationHash && (expected.Wake is null || actual.Wake?.WakeHash == expected.Wake.WakeHash) && expected.Claims.Length <= actual.Claims.Length && expected.Claims.Select((claim, index) => claim.ClaimHash == actual.Claims[index].ClaimHash).All(value => value) && (expected.Completion is null || actual.Completion?.CompletionHash == expected.Completion.CompletionHash) && (expected.Retirement is null || actual.Retirement?.RetirementHash == expected.Retirement.RetirementHash);
    private static HumanReviewDecisionActionReservationReference Reference(HumanReviewDecisionActionReservation value) => new(value.ReservationId, value.ReservationHash);
    private static HumanReviewDecisionActionWakeReference Reference(HumanReviewDecisionActionWake value) => new(value.WakeId, value.WakeHash);
    private static HumanReviewDecisionActionClaimReference Reference(HumanReviewDecisionActionClaim value) => new(value.ClaimId, value.ClaimHash);
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static HumanReviewDecisionActionRecoveryPage Page(HumanReviewDecisionActionRecoveryPageStatus status) => new(status, [], null, false);
    private static string TailCursor(CustomLoopRunSummary value) => CustomLoopRunPageCursorCodec.Encode(new CustomLoopRunPageCursor(value.CreatedAtUtc, value.Id, null));
    private static HumanReviewDecisionActionStoreMutationResult Result(HumanReviewDecisionActionStoreMutationStatus status) => new(status);
}
