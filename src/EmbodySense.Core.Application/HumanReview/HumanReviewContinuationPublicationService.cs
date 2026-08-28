using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Derives and publishes one deterministic wake-only continuation from canonical accepted approval state.</summary>
/// <remarks>The service owns no queue, timer, lease, or second store. It rereads the complete canonical run around every uncertain response and accepts only the exact deterministic wake or a fail-closed conflict.</remarks>
public sealed class HumanReviewContinuationPublicationService : IHumanReviewContinuationPublicationService
{
    private const int MaximumAttempts = 3;
    private readonly ICustomLoopRunStore _runs;
    private readonly IHumanReviewContinuationPublicationStore _continuations;

    /// <summary>Initializes the publisher over the sole run and continuation compare-exchange boundaries.</summary>
    /// <param name="runs">The canonical complete custom-loop run store.</param>
    /// <param name="continuations">The canonical wake-only continuation publication boundary.</param>
    public HumanReviewContinuationPublicationService(ICustomLoopRunStore runs, IHumanReviewContinuationPublicationStore continuations)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _continuations = continuations ?? throw new ArgumentNullException(nameof(continuations));
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId))
        {
            return Result(HumanReviewContinuationStoreMutationStatus.Invalid);
        }

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var read = await ReadAsync(runId, cancellationToken).ConfigureAwait(false);
            if (read.Status != HumanReviewContinuationStoreMutationStatus.Committed || read.Run is null)
            {
                return Result(read.Status);
            }

            if (!TryBuild(read.Run, out var expected))
            {
                return Result(HumanReviewContinuationStoreMutationStatus.Invalid);
            }

            if (read.Run.HumanReview?.Continuation is { } existingContinuation)
            {
                return HasExactPublishedWake(existingContinuation, expected!)
                    ? Result(HumanReviewContinuationStoreMutationStatus.Replayed)
                    : Result(HumanReviewContinuationStoreMutationStatus.Conflict);
            }

            try
            {
                var published = await _continuations.PublishAsync(runId, read.Run.LifecycleVersion, expected!, cancellationToken).ConfigureAwait(false);
                if (published is not null && Enum.IsDefined(published.Status))
                {
                    if (published.Status is HumanReviewContinuationStoreMutationStatus.Committed or HumanReviewContinuationStoreMutationStatus.Replayed
                        or HumanReviewContinuationStoreMutationStatus.NotFound or HumanReviewContinuationStoreMutationStatus.Invalid
                        or HumanReviewContinuationStoreMutationStatus.LimitExceeded)
                    {
                        return published;
                    }
                    if (published.Status == HumanReviewContinuationStoreMutationStatus.Conflict && attempt + 1 == MaximumAttempts)
                    {
                        return published;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cancellationReconciliation = await ReconcileUncertainPublicationAsync(runId).ConfigureAwait(false);
                if (cancellationReconciliation is not null)
                {
                    return cancellationReconciliation;
                }

                throw;
            }
            catch
            {
                // The canonical reread below decides whether the write committed, diverged, or remained unavailable.
            }

            var reconciled = await ReconcileUncertainPublicationAsync(runId).ConfigureAwait(false);
            if (reconciled is not null)
            {
                return reconciled;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var finalRead = await ReadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (finalRead.Status != HumanReviewContinuationStoreMutationStatus.Committed || finalRead.Run is null)
        {
            return Result(finalRead.Status == HumanReviewContinuationStoreMutationStatus.Committed
                ? HumanReviewContinuationStoreMutationStatus.Unavailable
                : finalRead.Status);
        }
        if (!TryBuild(finalRead.Run, out var finalExpected))
        {
            return Result(HumanReviewContinuationStoreMutationStatus.Invalid);
        }
        return finalRead.Run.HumanReview?.Continuation is { } retained
            ? HasExactPublishedWake(retained, finalExpected!)
                ? Result(HumanReviewContinuationStoreMutationStatus.Replayed)
                : Result(HumanReviewContinuationStoreMutationStatus.Conflict)
            : Result(HumanReviewContinuationStoreMutationStatus.Unavailable);
    }

    private async Task<HumanReviewContinuationStoreMutationResult?> ReconcileUncertainPublicationAsync(string runId)
    {
        var read = await ReadAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (read.Status != HumanReviewContinuationStoreMutationStatus.Committed || read.Run is null || !TryBuild(read.Run, out var expected))
        {
            return null;
        }

        return read.Run.HumanReview?.Continuation is { } retained
            ? HasExactPublishedWake(retained, expected!)
                ? Result(HumanReviewContinuationStoreMutationStatus.Replayed)
                : Result(HumanReviewContinuationStoreMutationStatus.Conflict)
            : null;
    }

    private async Task<(HumanReviewContinuationStoreMutationStatus Status, CustomLoopRunRecord? Run)> ReadAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            return run is null
                ? (HumanReviewContinuationStoreMutationStatus.NotFound, null)
                : (HumanReviewContinuationStoreMutationStatus.Committed, run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return (HumanReviewContinuationStoreMutationStatus.Invalid, null);
        }
        catch
        {
            return (HumanReviewContinuationStoreMutationStatus.Unavailable, null);
        }
    }

    private static bool TryBuild(CustomLoopRunRecord run, out HumanReviewContinuationState? continuation)
    {
        continuation = null;
        try
        {
            if (!CustomLoopRunValidator.Validate(run).IsValid
                || run.Status != CustomLoopRunStatus.Paused
                || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
                || run.HumanReview is not { } review
                || review.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve
                || review.ContinuationReservation is not { } reservation
                || run.SequentialAdapterBinding is not { } adapter
                || !GovernedLoopSequentialContractValidator.Validate(adapter).IsValid
                || !HumanReviewContractSnapshot.TryCaptureRequest(review.Request, out var request, out _) || request is null
                || !HumanReviewContractValidator.ValidateContinuationReservation(request, reservation).IsValid
                || !Equals(reservation.Decision, new HumanReviewDecisionReference(
                    review.AcceptedTerminalDecision.DecisionId,
                    review.AcceptedTerminalDecision.DecisionOperationId,
                    review.AcceptedTerminalDecision.Kind,
                    review.AcceptedTerminalDecision.DecisionHash))
                || adapter.ExecutionBinding.ExecutionGeneration < 1
                || reservation.ReservedAtUtc > request.Timing.ExpiresAtUtc)
            {
                return false;
            }

            var wakeId = Id("wake", reservation.ReservationHash);
            var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(
                HumanReviewProvenanceKind.Coordinator,
                "human-review-continuation-publisher",
                wakeId,
                reservation.ReservedAtUtc,
                string.Empty));
            var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
                1,
                wakeId,
                new HumanReviewRequestReference(request.RequestId, request.RequestHash),
                reservation.Decision,
                new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
                request.Binding.BindingHash,
                adapter.ExecutionBinding.ExecutionGeneration,
                reservation.ReservedAtUtc,
                request.Timing.ExpiresAtUtc,
                provenance,
                string.Empty));
            var candidate = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(
                1,
                wake,
                ImmutableArray<HumanReviewContinuationClaim>.Empty,
                null,
                null,
                string.Empty));
            if (!HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, null, candidate).IsValid)
            {
                return false;
            }

            continuation = candidate;
            return true;
        }
        catch
        {
            continuation = null;
            return false;
        }
    }

    private static bool HasExactPublishedWake(HumanReviewContinuationState retained, HumanReviewContinuationState expected)
    {
        try
        {
            return HumanReviewContinuationContractHash.MatchesState(retained)
                && HumanReviewContinuationReplayClassifier.ClassifyWake(retained.Wake, expected.Wake) == HumanReviewContinuationReplayDisposition.ExactReplay;
        }
        catch
        {
            return false;
        }
    }

    private static HumanReviewContinuationStoreMutationResult Result(HumanReviewContinuationStoreMutationStatus status) => new(status);

    private static string Id(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
