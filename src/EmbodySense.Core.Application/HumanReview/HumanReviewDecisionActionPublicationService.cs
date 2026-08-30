using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Derives and publishes one deterministic non-approval action wake from a canonical accepted decision reservation.</summary>
/// <remarks>The service owns no queue, timer, or second state store. An uncertain write is reconciled only by a canonical reread.</remarks>
public sealed class HumanReviewDecisionActionPublicationService : IHumanReviewDecisionActionPublicationService
{
    private const int MaximumAttempts = 3;
    private readonly ICustomLoopRunStore _runs;
    private readonly IHumanReviewDecisionActionPublicationStore _actions;

    /// <summary>Initializes the publisher over the sole canonical run and action compare-exchange boundaries.</summary>
    public HumanReviewDecisionActionPublicationService(ICustomLoopRunStore runs, IHumanReviewDecisionActionPublicationStore actions)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(HumanReviewDecisionActionPublicationCommand command, CancellationToken cancellationToken = default)
    {
        if (command is null || !CustomLoopArtifactIdentifier.IsValid(command.RunId) || command.Reservation is null)
        {
            return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
        }

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var read = await ReadAsync(command.RunId, cancellationToken).ConfigureAwait(false);
            if (read.Status != HumanReviewDecisionActionStoreMutationStatus.Committed || read.Run is null)
            {
                return Result(read.Status);
            }
            if (!TryBuild(read.Run, command.Reservation, out var expected))
            {
                return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
            }
            if (FindAction(read.Run, command.Reservation) is { } retained && retained.Wake is not null)
            {
                return ExactWake(retained, expected!) ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
            }

            try
            {
                var result = await _actions.PublishAsync(command.RunId, read.Run.LifecycleVersion, expected!, cancellationToken).ConfigureAwait(false);
                if (result is not null && result.Status is HumanReviewDecisionActionStoreMutationStatus.Committed or HumanReviewDecisionActionStoreMutationStatus.Replayed or HumanReviewDecisionActionStoreMutationStatus.NotFound or HumanReviewDecisionActionStoreMutationStatus.Invalid or HumanReviewDecisionActionStoreMutationStatus.LimitExceeded)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var reconciled = await ReconcileAsync(command).ConfigureAwait(false);
                if (reconciled is not null) return reconciled;
                throw;
            }
            catch
            {
                // Reread below is the only response-unknown reconciliation mechanism.
            }

            var responseUnknown = await ReconcileAsync(command).ConfigureAwait(false);
            if (responseUnknown is not null) return responseUnknown;
            cancellationToken.ThrowIfCancellationRequested();
        }

        return (await ReconcileAsync(command).ConfigureAwait(false)) ?? Result(HumanReviewDecisionActionStoreMutationStatus.Unavailable);
    }

    private async Task<HumanReviewDecisionActionStoreMutationResult?> ReconcileAsync(HumanReviewDecisionActionPublicationCommand command)
    {
        var read = await ReadAsync(command.RunId, CancellationToken.None).ConfigureAwait(false);
        if (read.Status != HumanReviewDecisionActionStoreMutationStatus.Committed || read.Run is null || !TryBuild(read.Run, command.Reservation, out var expected)) return null;
        var retained = FindAction(read.Run, command.Reservation);
        return retained?.Wake is null ? null : ExactWake(retained, expected!) ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
    }

    private async Task<(HumanReviewDecisionActionStoreMutationStatus Status, CustomLoopRunRecord? Run)> ReadAsync(string runId, CancellationToken cancellationToken)
    {
        try { var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false); return run is null ? (HumanReviewDecisionActionStoreMutationStatus.NotFound, null) : (HumanReviewDecisionActionStoreMutationStatus.Committed, run); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FormatException) { return (HumanReviewDecisionActionStoreMutationStatus.Invalid, null); }
        catch { return (HumanReviewDecisionActionStoreMutationStatus.Unavailable, null); }
    }

    private static bool TryBuild(CustomLoopRunRecord run, HumanReviewDecisionActionReservationReference referenceValue, out HumanReviewDecisionActionState? expected)
    {
        expected = null;
        try
        {
            var retained = FindAction(run, referenceValue);
            if (!CustomLoopRunValidator.Validate(run).IsValid || retained is null || retained.Completion is not null || retained.Retirement is not null || !retained.Claims.IsDefaultOrEmpty || run.HumanReview?.Request is not { } request || retained.Reservation.Decision.Kind is not (HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation)) return false;
            var wakeId = Id("action-wake", retained.Reservation.ReservationHash);
            var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-action-publisher", wakeId, retained.Reservation.ReservedAtUtc, string.Empty));
            var wake = HumanReviewDecisionActionContractHash.ApplyWake(new HumanReviewDecisionActionWake(1, wakeId, retained.Reservation.Request, retained.Reservation.Decision, referenceValue, retained.BindingHash, retained.ExpectedGeneration, retained.Reservation.ReservedAtUtc, request.Timing.ExpiresAtUtc, provenance, string.Empty));
            var candidate = HumanReviewDecisionActionContractHash.ApplyState(retained with { Wake = wake, StateHash = string.Empty });
            if (retained.Wake is null && !HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, retained, candidate).IsValid) return false;
            expected = candidate;
            return true;
        }
        catch { return false; }
    }

    private static HumanReviewDecisionActionState? FindAction(CustomLoopRunRecord run, HumanReviewDecisionActionReservationReference referenceValue) => run.HumanReview?.DecisionActions.FirstOrDefault(value => value is not null && value.Reservation.ReservationId == referenceValue.ReservationId && value.Reservation.ReservationHash == referenceValue.ReservationHash);
    private static bool ExactWake(HumanReviewDecisionActionState retained, HumanReviewDecisionActionState expected) => retained.Wake is not null && expected.Wake is not null && HumanReviewDecisionActionContractHash.MatchesState(retained) && retained.Wake.WakeHash == expected.Wake.WakeHash;
    private static HumanReviewDecisionActionStoreMutationResult Result(HumanReviewDecisionActionStoreMutationStatus status) => new(status);
    private static string Id(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
