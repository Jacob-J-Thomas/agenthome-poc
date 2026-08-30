using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewDecisionActionTransitionProcessLossHost
{
    private const int ProcessLossExitCode = 179;

    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string transition, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary) || !Enum.IsDefined(boundary) || boundary == 0) return 2;

        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary));
        var current = await store.GetAsync(runId);
        var action = current?.HumanReview?.DecisionActions.SingleOrDefault(item => item is not null && item.Wake is not null && item.Completion is null && item.Retirement is null);
        if (current is null || action?.Wake is null) return 3;

        var actions = new HumanReviewDecisionActionRunStore(store);
        var result = transition switch
        {
            "claim" when action.Claims.IsEmpty => await actions.ClaimAsync(new(Candidate(current, action, null), Claim(action.Wake, action.Reservation))),
            "completion" when action.Claims is [.., { } active] => await actions.CompleteAsync(CompletionIntent(current, action, active), Completion(action, active)),
            "retirement" when action.Claims is [.., { } active] => await actions.RetireAsync(RetirementIntent(current, action, active), Retirement(action, active)),
            _ => null,
        };
        return result?.Status == HumanReviewDecisionActionStoreMutationStatus.Committed ? 0 : 4;
    }

    private static HumanReviewDecisionActionRecoveryCandidate Candidate(CustomLoopRunRecord run, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaimReference? priorClaim)
        => new(run.Id, run.LifecycleVersion, new(run.HumanReview!.Request.RequestId, run.HumanReview.Request.RequestHash), action.Reservation.Decision, new(action.Wake!.WakeId, action.Wake.WakeHash), action.ExpectedGeneration, action.Wake.ExpiresAtUtc, new(action.Reservation.ReservationId, action.Reservation.ReservationHash), priorClaim);

    private static HumanReviewDecisionActionClaim Claim(HumanReviewDecisionActionWake wake, HumanReviewDecisionActionReservation reservation)
    {
        var claimedAtUtc = wake.PublishedAtUtc.AddMinutes(1);
        return HumanReviewDecisionActionContractHash.ApplyClaim(new(1, "claim-process-loss", new(wake.WakeId, wake.WakeHash), new(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, "worker-claim-process-loss", claimedAtUtc, claimedAtUtc.AddMinutes(5), Provenance("claim-process-loss", claimedAtUtc), string.Empty));
    }

    private static HumanReviewDecisionActionCompletionIntent CompletionIntent(CustomLoopRunRecord run, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim)
        => new(run.Id, run.LifecycleVersion, new(action.Wake!.WakeId, action.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration);

    private static HumanReviewDecisionActionCompletion Completion(HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim)
    {
        var completedAtUtc = claim.ClaimedAtUtc.AddSeconds(1);
        return HumanReviewDecisionActionContractHash.ApplyCompletion(new(1, "completion-process-loss", new(action.Wake!.WakeId, action.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, Disposition(action.Reservation.Decision.Kind), Hash('a'), Hash('b'), completedAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("completion-process-loss", completedAtUtc), string.Empty));
    }

    private static HumanReviewDecisionActionRetirementIntent RetirementIntent(CustomLoopRunRecord run, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim)
        => new(run.Id, run.LifecycleVersion, new(action.Wake!.WakeId, action.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid);

    private static HumanReviewDecisionActionRetirement Retirement(HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim)
    {
        var retiredAtUtc = claim.ClaimedAtUtc.AddSeconds(1);
        return HumanReviewDecisionActionContractHash.ApplyRetirement(new(1, "retirement-process-loss", new(action.Wake!.WakeId, action.Wake.WakeHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid, retiredAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("retirement-process-loss", retiredAtUtc), string.Empty));
    }

    private static HumanReviewDecisionActionDisposition Disposition(HumanReviewDecisionKind kind) => kind switch
    {
        HumanReviewDecisionKind.Reject => HumanReviewDecisionActionDisposition.Rejected,
        HumanReviewDecisionKind.Cancel => HumanReviewDecisionActionDisposition.Cancelled,
        HumanReviewDecisionKind.RequestInformation => HumanReviewDecisionActionDisposition.InformationParked,
        _ => HumanReviewDecisionActionDisposition.Unknown,
    };

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc) => HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Coordinator, "human-review-action-store", correlationId, observedAtUtc, string.Empty));
    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    private static ValueTask ExitAfterBoundaryAsync(CustomLoopRunPublicationBoundary currentBoundary, CustomLoopRunPublicationBoundary requestedBoundary)
    {
        if (currentBoundary == requestedBoundary)
        {
            Console.Error.WriteLine($"The test host process crashed after `{currentBoundary}`.");
            Console.Error.Flush();
            Environment.Exit(ProcessLossExitCode);
        }

        return ValueTask.CompletedTask;
    }
}
