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
            if (!TryBuild(read.Run, command.Reservation, out var retained, out var expected))
            {
                return Result(HumanReviewDecisionActionStoreMutationStatus.Invalid);
            }
            if (retained!.Wake is not null)
            {
                return ExactWake(retained, expected!) ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
            }
            if (!CanPublishFresh(read.Run, retained)) return Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);

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
        if (read.Status != HumanReviewDecisionActionStoreMutationStatus.Committed || read.Run is null || !TryBuild(read.Run, command.Reservation, out var retained, out var expected)) return null;
        return retained!.Wake is null ? null : ExactWake(retained, expected!) ? Result(HumanReviewDecisionActionStoreMutationStatus.Replayed) : Result(HumanReviewDecisionActionStoreMutationStatus.Conflict);
    }

    private async Task<(HumanReviewDecisionActionStoreMutationStatus Status, CustomLoopRunRecord? Run)> ReadAsync(string runId, CancellationToken cancellationToken)
    {
        try { var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false); return run is null ? (HumanReviewDecisionActionStoreMutationStatus.NotFound, null) : (HumanReviewDecisionActionStoreMutationStatus.Committed, run); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FormatException) { return (HumanReviewDecisionActionStoreMutationStatus.Invalid, null); }
        catch { return (HumanReviewDecisionActionStoreMutationStatus.Unavailable, null); }
    }

    private static bool TryBuild(CustomLoopRunRecord run, HumanReviewDecisionActionReservationReference referenceValue, out HumanReviewDecisionActionState? retained, out HumanReviewDecisionActionState? expected)
    {
        retained = null;
        expected = null;
        try
        {
            retained = FindAction(run, referenceValue);
            if (!CustomLoopRunValidator.Validate(run).IsValid || retained is null || run.HumanReview?.Request is not { } request || retained.Reservation.Decision.Kind is not (HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation)) return false;
            return HumanReviewDecisionActionWakeFactory.TryCreate(request, retained, out expected);
        }
        catch { retained = null; expected = null; return false; }
    }

    private static bool CanPublishFresh(CustomLoopRunRecord run, HumanReviewDecisionActionState retained) => retained.Wake is null && retained.Claims.IsDefaultOrEmpty && retained.Completion is null && retained.Retirement is null && HumanReviewDecisionActionContractValidator.IsCurrentActionHead(run.HumanReview, retained);
    private static HumanReviewDecisionActionState? FindAction(CustomLoopRunRecord run, HumanReviewDecisionActionReservationReference referenceValue) => run.HumanReview?.DecisionActions.FirstOrDefault(value => value is not null && value.Reservation.ReservationId == referenceValue.ReservationId && value.Reservation.ReservationHash == referenceValue.ReservationHash);
    private static bool ExactWake(HumanReviewDecisionActionState retained, HumanReviewDecisionActionState expected) => retained.Wake is not null && expected.Wake is not null && HumanReviewDecisionActionContractHash.MatchesState(retained) && retained.Wake.WakeHash == expected.Wake.WakeHash;
    private static HumanReviewDecisionActionStoreMutationResult Result(HumanReviewDecisionActionStoreMutationStatus status) => new(status);
}
