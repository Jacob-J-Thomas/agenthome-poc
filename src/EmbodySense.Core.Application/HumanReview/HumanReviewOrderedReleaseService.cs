using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Applies every Human Review decision through one whole-run compare-exchange and the one ordered runtime.</summary>
/// <remarks>The two release ports stay separate for their recovery coordinators, but both calls converge here before a frontier may change. A durable release receipt always precedes ordered re-entry. A pre-dispatch-effect receipt is reread before graph transition validation; that check is read-only and fails closed, so uncertain effects cannot reach a compare-exchange or runtime re-entry. Replays return the retained terminal artifact and never redispatch an uncertain external effect.</remarks>
public sealed class HumanReviewOrderedReleaseService : IHumanReviewContinuationReleasePort, IHumanReviewDecisionActionReleasePort
{
    private readonly ICustomLoopRunStore _runs;
    private readonly IGovernedLoopWaitOrderedResumePort _contextResolver;
    private readonly IGovernedLoopSequentialOrderedRuntime _orderedRuntime;
    private readonly IHumanReviewContinuationAuthoritySource? _authority;
    private readonly IHumanReviewCurrentEffectAttemptEvidenceSource? _effectEvidence;
    private readonly IGovernedLoopEffectCertaintySnapshotSource? _effectCertainty;
    private readonly TimeProvider _clock;

    /// <summary>Initializes the authoritative Human Review release seam.</summary>
    /// <param name="runs">The canonical whole-run compare-exchange store.</param>
    /// <param name="contextResolver">The immutable admitted graph, anchor, and plan resolver.</param>
    /// <param name="orderedRuntime">The one existing ordered runtime used only after durable release.</param>
    /// <param name="clock">The trusted UTC clock used for terminal receipts.</param>
    /// <param name="authority">The optional current authority reread required to release an approval. A missing source fails approval closed.</param>
    /// <param name="effectEvidence">The optional exact current effect-evidence reread for pre-dispatch effect approvals.</param>
    /// <param name="effectCertainty">The optional current effect-certainty reread for pre-dispatch effect approvals.</param>
    public HumanReviewOrderedReleaseService(
        ICustomLoopRunStore runs,
        IGovernedLoopWaitOrderedResumePort contextResolver,
        IGovernedLoopSequentialOrderedRuntime orderedRuntime,
        TimeProvider clock,
        IHumanReviewContinuationAuthoritySource? authority = null,
        IHumanReviewCurrentEffectAttemptEvidenceSource? effectEvidence = null,
        IGovernedLoopEffectCertaintySnapshotSource? effectCertainty = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _orderedRuntime = orderedRuntime ?? throw new ArgumentNullException(nameof(orderedRuntime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _authority = authority;
        _effectEvidence = effectEvidence;
        _effectCertainty = effectCertainty;
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationReleaseResult> ReleaseAsync(HumanReviewContinuationActionIntent action, HumanReviewContinuationCompletionIntent completion, CancellationToken cancellationToken = default)
    {
        if (!IsContinuationIntent(action, completion)) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var run = await ReadAsync(action.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
        if (TryContinuationReplay(run, action, out var replay)) return await ReplayContinuationAsync(run, action, replay!, cancellationToken).ConfigureAwait(false);
        if (!TryGetContinuation(run, action, out var review, out var state, out var claim)) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var currentReview = review!;
        var activeClaim = claim!;
        if (!TryNow(run.UpdatedAtUtc, activeClaim.ClaimedAtUtc, activeClaim.LeaseExpiresAtUtc, out var now)) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
        if (now >= state!.Wake.ExpiresAtUtc || now >= currentReview.Request.Timing.ExpiresAtUtc) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
        // This reread is intentionally before context reconstruction and graph/re-entry validation. It is read-only
        // and must reject a stale effect receipt before this service can construct, compare-exchange, or hand off any release transition.
        if (action.Action == HumanReviewContinuationAction.ReleaseEffect
            && !await HasExactNotStartedEffectAsync(action, currentReview.Request, cancellationToken).ConfigureAwait(false)) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
        var context = await ResolveContextAsync(run, cancellationToken).ConfigureAwait(false);
        if (context is null) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
        if (action.Action == HumanReviewContinuationAction.ReleaseEffect)
        {
            if (!HasExactPreDispatchEffectNode(run, currentReview.Request, context, out var effectNode, out var effectActivation)) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
            if (!await HasCurrentAuthorityAsync(currentReview.Request, run.SequentialAdapterBinding!, context.Artifact, cancellationToken).ConfigureAwait(false)) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
            // The first certainty read above rejects stale input before context work. Re-read the same volatile effect
            // evidence as the final operation before the whole-run compare-exchange so no graph or authority work can
            // stretch a conclusively-not-dispatched proof across the durable release boundary.
            if (!await HasExactNotStartedEffectAsync(action, currentReview.Request, cancellationToken).ConfigureAwait(false)) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
            return await ReleaseEffectAsync(run, currentReview, state!, activeClaim, action, completion, context, effectNode!, effectActivation!, now, cancellationToken).ConfigureAwait(false);
        }
        if (!HasExactReviewNode(run, currentReview.Request, context, out var node, out var activation)) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
        if (!await HasCurrentAuthorityAsync(currentReview.Request, run.SequentialAdapterBinding!, context.Artifact, cancellationToken).ConfigureAwait(false)) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);

        var reviewActivation = activation!;
        var pruning = CreatePruning(run, context.Plan, reviewActivation, GovernedLoopControlCondition.Success, now, action.ReleaseReceipt!.ReleaseOperationId);
        if (pruning is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
        var baseEvent = TerminalEvent(run, pruning.Value.Events.Count, reviewActivation, now, CustomLoopRunEventKind.NodeAttemptCompleted, "The exact approved Human Review continuation completed its blocked activation before ordered re-entry.");
        var selectedEdges = context.Plan.ControlEdges.Where(edge => reviewActivation.OutgoingControlEdgeIds.Contains(edge.Id, StringComparer.Ordinal) && edge.Condition == GovernedLoopControlCondition.Success).Select(edge => edge.Id).Order(StringComparer.Ordinal).ToArray();
        var skippedEdges = reviewActivation.OutgoingControlEdgeIds.Except(selectedEdges, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var terminalEvent = AttachEvidence(baseEvent, run.SequentialAdapterBinding!, reviewActivation, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed, GovernedLoopControlCondition.Success, selectedEdges, skippedEdges);
        var transition = GovernedLoopSequentialFrontierMachine.CompleteReviewBlockedHumanReview(run.Frontier, run.SequentialAdapterBinding, context.Plan, node, reviewActivation, reviewActivation.Attempt!.Value, reviewActivation.AttemptOperationId, terminalEvent.EventId, terminalEvent.SequentialNodeEvidence!.OutcomeArtifactHash, now, pruning.Value.References);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var completionId = Id("human-review-completion", action.ReleaseReceipt!.ReleaseOperationId);
        var resultHash = GovernedLoopHumanReviewReleaseReceiptHash.Compute(action.ReleaseReceipt.ReleaseOperationId, CustomLoopSequentialOutcomeArtifactHash.Compute(terminalEvent), transition.Frontier.Payload.ContentHash);
        var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-release", completionId, now, string.Empty));
        if (!HumanReviewContinuationCompletionIntentFactory.TryCreate(completion, currentReview.Request, state!.Wake, currentReview.ContinuationReservation, activeClaim, completionId, resultHash, transition.Frontier.Payload.ContentHash, now, ImmutableArray<HumanReviewRedactedPreview>.Empty, provenance, out var durableCompletion)
            || durableCompletion is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var nextState = HumanReviewContinuationContractHash.ApplyState(state with { Completion = durableCompletion, StateHash = string.Empty });
        var candidate = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = now,
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = now },
            Frontier = transition.Frontier,
            HumanReview = currentReview with { Continuation = nextState },
            Events = [.. run.Events, .. pruning.Value.Events, terminalEvent, LifecycleEvent(run, pruning.Value.Events.Count + 1, action.ReleaseReceipt.ReleaseOperationId, now, "The exact Human Review approval is durable before ordered continuation.")],
        };
        if (!CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var persisted = await PersistAsync(run, candidate, cancellationToken).ConfigureAwait(false);
        if (persisted.Run is null) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
        if (!TryContinuationReplay(persisted.Run, action, out var retainedCompletion) || retainedCompletion is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
        return await CompleteContinuationHandoffAsync(persisted.Run, context, action.ReleaseReceipt.ReleaseOperationId, retainedCompletion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewContinuationReleaseResult> ReleaseEffectAsync(
        CustomLoopRunRecord run,
        HumanReviewRunState review,
        HumanReviewContinuationState state,
        HumanReviewContinuationClaim claim,
        HumanReviewContinuationActionIntent action,
        HumanReviewContinuationCompletionIntent completion,
        GovernedLoopWaitOrderedContext context,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transition = GovernedLoopSequentialFrontierMachine.ReleaseReviewBlockedRecoverableAction(
            run.Frontier,
            run.SequentialAdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt!.Value,
            activation.AttemptOperationId,
            now);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var effectReceiptHash = action.ReleaseReceipt!.EffectReceiptHash;
        var completionId = Id("human-review-completion", action.ReleaseReceipt.ReleaseOperationId);
        var resultHash = GovernedLoopHumanReviewReleaseReceiptHash.Compute(action.ReleaseReceipt.ReleaseOperationId, effectReceiptHash!, transition.Frontier.Payload.ContentHash);
        var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-release", completionId, now, string.Empty));
        if (!HumanReviewContinuationCompletionIntentFactory.TryCreate(completion, review.Request, state.Wake, review.ContinuationReservation, claim, completionId, resultHash, transition.Frontier.Payload.ContentHash, now, ImmutableArray<HumanReviewRedactedPreview>.Empty, provenance, out var durableCompletion)
            || durableCompletion is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var nextState = HumanReviewContinuationContractHash.ApplyState(state with { Completion = durableCompletion, StateHash = string.Empty });
        var candidate = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = now,
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = now },
            Frontier = transition.Frontier,
            HumanReview = review with { Continuation = nextState },
            Events = [.. run.Events, LifecycleEvent(run, 0, action.ReleaseReceipt.ReleaseOperationId, now, "The exact pre-dispatch Human Review release is durable before the original recoverable Action re-enters ordered execution.")],
        };
        if (!CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);

        var persisted = await PersistAsync(run, candidate, cancellationToken).ConfigureAwait(false);
        if (persisted.Run is null) return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
        if (!TryContinuationReplay(persisted.Run, action, out var retainedCompletion) || retainedCompletion is null) return Continuation(HumanReviewContinuationReleaseStatus.Invalid);
        return await CompleteContinuationHandoffAsync(persisted.Run, context, action.ReleaseReceipt.ReleaseOperationId, retainedCompletion, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default)
    {
        if (!IsDecisionIntent(intent)) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);

        var run = await ReadAsync(intent.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null) return Action(HumanReviewDecisionActionReleaseStatus.Unavailable);
        if (TryDecisionReplay(run, intent, out var replay)) return await ReplayDecisionActionAsync(run, intent, replay!, cancellationToken).ConfigureAwait(false);
        if (!TryGetDecisionAction(run, intent, out var review, out var decisionAction, out var claim)) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        var context = await ResolveContextAsync(run, cancellationToken).ConfigureAwait(false);
        if (context is null) return Action(HumanReviewDecisionActionReleaseStatus.Unavailable);
        var exactNode = review!.Request.Purpose == HumanReviewPurpose.PreDispatchEffect
            ? HasExactPreDispatchEffectNode(run, review.Request, context, out var node, out var activation)
            : HasExactReviewNode(run, review.Request, context, out node, out activation);
        if (!exactNode) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        if (!TryNow(run.UpdatedAtUtc, claim!.ClaimedAtUtc, claim.LeaseExpiresAtUtc, out var now)) return Action(HumanReviewDecisionActionReleaseStatus.Unavailable);
        if (now >= decisionAction!.Wake!.ExpiresAtUtc || now >= review!.Request.Timing.ExpiresAtUtc) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);

        return intent.Decision.Kind switch
        {
            HumanReviewDecisionKind.RequestInformation => await ParkForInformationAsync(run, review!, decisionAction!, claim, intent, now, cancellationToken).ConfigureAwait(false),
            HumanReviewDecisionKind.Reject => await RejectAsync(run, review!, decisionAction!, claim, intent, context, node!, activation!, now, cancellationToken).ConfigureAwait(false),
            HumanReviewDecisionKind.Cancel => await CancelAsync(run, review!, decisionAction!, claim, intent, now, cancellationToken).ConfigureAwait(false),
            _ => Action(HumanReviewDecisionActionReleaseStatus.Invalid),
        };
    }

    private async Task<HumanReviewDecisionActionReleaseResult> ParkForInformationAsync(CustomLoopRunRecord run, HumanReviewRunState review, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim, HumanReviewDecisionActionIntent intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var frontierHash = run.Frontier!.Payload.ContentHash;
        var completion = CreateActionCompletion(intent, claim, HumanReviewDecisionActionDisposition.InformationParked, GovernedLoopHumanReviewReleaseReceiptHash.Compute(intent.ActionOperationId, frontierHash, frontierHash), frontierHash, now);
        return completion is null ? Action(HumanReviewDecisionActionReleaseStatus.Invalid) : await PersistActionAsync(run, UpdateDecisionAction(run, review, action, completion, now, run.Frontier, run.Status, []), intent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewDecisionActionReleaseResult> RejectAsync(
        CustomLoopRunRecord run,
        HumanReviewRunState review,
        HumanReviewDecisionActionState action,
        HumanReviewDecisionActionClaim claim,
        HumanReviewDecisionActionIntent intent,
        GovernedLoopWaitOrderedContext context,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pruning = CreatePruning(run, context.Plan, activation, GovernedLoopControlCondition.Failure, now, intent.ActionOperationId);
        if (pruning is null) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        var baseEvent = TerminalEvent(run, pruning.Value.Events.Count, activation, now, CustomLoopRunEventKind.NodeAttemptFailed, "The exact rejected Human Review decision failed its blocked activation without dispatching a dependent node.");
        var selectedEdges = context.Plan.ControlEdges.Where(edge => activation.OutgoingControlEdgeIds.Contains(edge.Id, StringComparer.Ordinal) && edge.Condition == GovernedLoopControlCondition.Failure).Select(edge => edge.Id).Order(StringComparer.Ordinal).ToArray();
        var skippedEdges = activation.OutgoingControlEdgeIds.Except(selectedEdges, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var terminalEvent = AttachEvidence(baseEvent, run.SequentialAdapterBinding!, activation, CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection, CustomLoopSequentialNodeDisposition.Rejected, GovernedLoopControlCondition.Failure, selectedEdges, skippedEdges);
        var transition = review.Request.Purpose == HumanReviewPurpose.PreDispatchEffect
            ? GovernedLoopSequentialFrontierMachine.FailReviewBlockedRecoverableAction(run.Frontier, run.SequentialAdapterBinding, context.Plan, node, activation, activation.Attempt!.Value, activation.AttemptOperationId, terminalEvent.EventId, terminalEvent.SequentialNodeEvidence!.OutcomeArtifactHash, now, pruning.Value.References)
            : GovernedLoopSequentialFrontierMachine.FailReviewBlockedHumanReview(run.Frontier, run.SequentialAdapterBinding, context.Plan, node, activation, activation.Attempt!.Value, activation.AttemptOperationId, terminalEvent.EventId, terminalEvent.SequentialNodeEvidence!.OutcomeArtifactHash, now, pruning.Value.References);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        var completion = CreateActionCompletion(intent, claim, HumanReviewDecisionActionDisposition.Rejected, GovernedLoopHumanReviewReleaseReceiptHash.Compute(intent.ActionOperationId, CustomLoopSequentialOutcomeArtifactHash.Compute(terminalEvent), transition.Frontier.Payload.ContentHash), transition.Frontier.Payload.ContentHash, now);
        if (completion is null) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);

        var hasFailureRoute = selectedEdges.Length > 0;
        var nextStatus = hasFailureRoute ? CustomLoopRunStatus.Running : CustomLoopRunStatus.Failed;
        var candidate = UpdateDecisionAction(
            run,
            review,
            action,
            completion,
            now,
            transition.Frontier,
            nextStatus,
            [.. pruning.Value.Events, terminalEvent, LifecycleEvent(run, pruning.Value.Events.Count + 1, intent.ActionOperationId, now, hasFailureRoute ? "The exact rejected Human Review decision is durable before the authored Failure route re-enters ordered execution." : "The exact rejected Human Review decision failed the canonical run because no authored Failure route exists.")],
            hasFailureRoute ? null : "human-review-rejected",
            hasFailureRoute ? null : "The exact rejected Human Review decision failed the canonical run because no authored Failure route exists.");
        return await PersistRejectedActionAsync(run, candidate, intent, context, hasFailureRoute, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewDecisionActionReleaseResult> CancelAsync(CustomLoopRunRecord run, HumanReviewRunState review, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim, HumanReviewDecisionActionIntent intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transition = GovernedLoopSequentialFrontierMachine.CancelCurrent(run.Frontier, run.SequentialAdapterBinding, now);
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is null) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        var completion = CreateActionCompletion(intent, claim, HumanReviewDecisionActionDisposition.Cancelled, GovernedLoopHumanReviewReleaseReceiptHash.Compute(intent.ActionOperationId, transition.Frontier.Payload.ContentHash, transition.Frontier.Payload.ContentHash), transition.Frontier.Payload.ContentHash, now);
        return completion is null ? Action(HumanReviewDecisionActionReleaseStatus.Invalid) : await PersistActionAsync(run, UpdateDecisionAction(run, review, action, completion, now, transition.Frontier, CustomLoopRunStatus.Cancelled, [LifecycleEvent(run, 0, intent.ActionOperationId, now, "The exact cancelled Human Review decision cancelled the canonical run.")]), intent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewDecisionActionReleaseResult> PersistActionAsync(CustomLoopRunRecord current, CustomLoopRunRecord candidate, HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken)
    {
        if (!CustomLoopRunValidator.ValidateUpdate(current, candidate).IsValid) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        var persisted = await PersistAsync(current, candidate, cancellationToken).ConfigureAwait(false);
        return persisted.Run is null ? Action(HumanReviewDecisionActionReleaseStatus.Unavailable) : TryDecisionReplay(persisted.Run, intent, out var completion) ? Action(HumanReviewDecisionActionReleaseStatus.Completed, completion) : Action(HumanReviewDecisionActionReleaseStatus.Invalid);
    }

    private async Task<HumanReviewDecisionActionReleaseResult> PersistRejectedActionAsync(
        CustomLoopRunRecord current,
        CustomLoopRunRecord candidate,
        HumanReviewDecisionActionIntent intent,
        GovernedLoopWaitOrderedContext context,
        bool reenterFailureRoute,
        CancellationToken cancellationToken)
    {
        if (!CustomLoopRunValidator.ValidateUpdate(current, candidate).IsValid) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        var persisted = await PersistAsync(current, candidate, cancellationToken).ConfigureAwait(false);
        if (persisted.Run is null) return Action(HumanReviewDecisionActionReleaseStatus.Unavailable);
        if (!TryDecisionReplay(persisted.Run, intent, out var completion)) return Action(HumanReviewDecisionActionReleaseStatus.Invalid);
        return reenterFailureRoute
            ? await CompleteDecisionHandoffAsync(persisted.Run, context, intent.ActionOperationId, completion!, cancellationToken).ConfigureAwait(false)
            : Action(HumanReviewDecisionActionReleaseStatus.Completed, completion);
    }

    private static CustomLoopRunRecord UpdateDecisionAction(CustomLoopRunRecord run, HumanReviewRunState review, HumanReviewDecisionActionState action, HumanReviewDecisionActionCompletion completion, DateTimeOffset now, GovernedLoopFrontierPosture frontier, CustomLoopRunStatus status, IReadOnlyList<CustomLoopRunEvent> appended, string? failureCode = null, string? failureDetail = null)
    {
        var completed = HumanReviewDecisionActionContractHash.ApplyState(action with { Completion = completion, StateHash = string.Empty });
        var index = review.DecisionActions.IndexOf(action);
        return run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = now,
            Status = status,
            CompletedAtUtc = status is CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled ? now : null,
            ExecutionClock = status switch
            {
                CustomLoopRunStatus.Paused => run.ExecutionClock,
                CustomLoopRunStatus.Running => run.ExecutionClock with { ActiveSinceUtc = now },
                _ => run.ExecutionClock with { ActiveSinceUtc = null },
            },
            FailureCode = failureCode,
            FailureDetail = failureDetail,
            Frontier = frontier,
            HumanReview = review with { DecisionActions = review.DecisionActions.SetItem(index, completed) },
            Events = [.. run.Events, .. appended],
        };
    }

    private static HumanReviewDecisionActionCompletion? CreateActionCompletion(HumanReviewDecisionActionIntent intent, HumanReviewDecisionActionClaim claim, HumanReviewDecisionActionDisposition disposition, string resultHash, string frontierReceiptHash, DateTimeOffset now)
    {
        try
        {
            var completionId = Id("human-review-action-completion", intent.ActionOperationId);
            var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-release", completionId, now, string.Empty));
            return HumanReviewDecisionActionContractHash.ApplyCompletion(new HumanReviewDecisionActionCompletion(HumanReviewDecisionActionCompletion.CurrentSchemaVersion, completionId, intent.Wake, intent.Claim, intent.Reservation, intent.ExpectedGeneration, disposition, resultHash, frontierReceiptHash, now, ImmutableArray<HumanReviewRedactedPreview>.Empty, provenance, string.Empty));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<(CustomLoopRunRecord? Run, bool Committed)> PersistAsync(CustomLoopRunRecord current, CustomLoopRunRecord candidate, CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _runs.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken).ConfigureAwait(false);
            if (persisted.Status == CustomLoopRunStoreStatus.Updated && persisted.Run is not null && CustomLoopRunValidator.HasSameDurableVersion(candidate, persisted.Run)) return (persisted.Run, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return (await ReadAsync(current.Id, CancellationToken.None).ConfigureAwait(false), false);
    }

    private async Task<GovernedLoopWaitOrderedContext?> ResolveContextAsync(CustomLoopRunRecord run, CancellationToken cancellationToken)
    {
        try
        {
            var context = await _contextResolver.ResolveAsync(run, cancellationToken).ConfigureAwait(false);
            return context is not null && MatchesContext(run, context) ? context : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HumanReviewContinuationReleaseResult> ReplayContinuationAsync(CustomLoopRunRecord run, HumanReviewContinuationActionIntent action, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken)
    {
        if (!IsOrderedHandoffPending(run, action.ReleaseReceipt!.ReleaseOperationId)) return Continuation(HumanReviewContinuationReleaseStatus.Completed, completion);
        var context = await ResolveContextAsync(run, cancellationToken).ConfigureAwait(false);
        return context is null
            ? Continuation(HumanReviewContinuationReleaseStatus.Unavailable)
            : await CompleteContinuationHandoffAsync(run, context, action.ReleaseReceipt.ReleaseOperationId, completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewDecisionActionReleaseResult> ReplayDecisionActionAsync(CustomLoopRunRecord run, HumanReviewDecisionActionIntent intent, HumanReviewDecisionActionCompletion completion, CancellationToken cancellationToken)
    {
        if (!IsOrderedHandoffPending(run, intent.ActionOperationId)) return Action(HumanReviewDecisionActionReleaseStatus.Completed, completion);
        var context = await ResolveContextAsync(run, cancellationToken).ConfigureAwait(false);
        return context is null
            ? Action(HumanReviewDecisionActionReleaseStatus.Unavailable)
            : await CompleteDecisionHandoffAsync(run, context, intent.ActionOperationId, completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewContinuationReleaseResult> CompleteContinuationHandoffAsync(CustomLoopRunRecord run, GovernedLoopWaitOrderedContext context, string operationId, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken)
    {
        if (!IsOrderedHandoffPending(run, operationId) || await ReenterAsync(run, context, operationId, cancellationToken).ConfigureAwait(false))
        {
            return Continuation(HumanReviewContinuationReleaseStatus.Completed, completion);
        }

        return Continuation(HumanReviewContinuationReleaseStatus.Unavailable);
    }

    private async Task<HumanReviewDecisionActionReleaseResult> CompleteDecisionHandoffAsync(CustomLoopRunRecord run, GovernedLoopWaitOrderedContext context, string operationId, HumanReviewDecisionActionCompletion completion, CancellationToken cancellationToken)
    {
        if (!IsOrderedHandoffPending(run, operationId) || await ReenterAsync(run, context, operationId, cancellationToken).ConfigureAwait(false))
        {
            return Action(HumanReviewDecisionActionReleaseStatus.Completed, completion);
        }

        return Action(HumanReviewDecisionActionReleaseStatus.Unavailable);
    }

    private static bool IsOrderedHandoffPending(CustomLoopRunRecord run, string operationId)
        => run.Status == CustomLoopRunStatus.Running
            && run.Events.LastOrDefault() is { Kind: CustomLoopRunEventKind.LifecycleChanged } lifecycle
            && string.Equals(lifecycle.EventId, operationId, StringComparison.Ordinal);

    private async Task<bool> ReenterAsync(CustomLoopRunRecord run, GovernedLoopWaitOrderedContext context, string operationId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _orderedRuntime.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(GovernedLoopSequentialOrderedResumeRequest.CurrentSchemaVersion, context.Anchor, context.Plan, context.Artifact, run.LifecycleVersion, operationId, run.AdmissionActor), cancellationToken).ConfigureAwait(false);
            var retained = await ReadAsync(run.Id, cancellationToken).ConfigureAwait(false);
            return retained is not null && !IsOrderedHandoffPending(retained, operationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CustomLoopRunRecord?> ReadAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            return CustomLoopRunValidator.Validate(run).IsValid ? run : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> HasCurrentAuthorityAsync(HumanReviewRequest request, GovernedLoopSequentialAdapterBinding binding, GovernedLoopGraphRevisionArtifact artifact, CancellationToken cancellationToken)
    {
        if (_authority is null) return false;
        try
        {
            var authority = await _authority.ReadAsync(new HumanReviewContinuationAuthorityQuery(request.Binding, binding, artifact), cancellationToken).ConfigureAwait(false);
            return authority?.Status == HumanReviewContinuationAuthorityReadStatus.Current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> HasExactNotStartedEffectAsync(HumanReviewContinuationActionIntent action, HumanReviewRequest request, CancellationToken cancellationToken)
    {
        if (_effectEvidence is null || _effectCertainty is null || request.Binding.EffectAttempt is null || action.EffectQuery is null) return false;
        try
        {
            var evidence = await _effectEvidence.ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(request.Binding, request.Binding.EffectAttempt), cancellationToken).ConfigureAwait(false);
            if (evidence?.Status != HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current || evidence.Evidence is null
                || !HumanReviewEffectReleaseContract.TryCaptureExpectation(evidence.Evidence.Identity, evidence.Evidence.Preparation, out var identity, out var preparation, out _)
                || identity is null || preparation is null
                || !Equals(action.EffectQuery.Identity, identity) || !Equals(action.EffectQuery.Preparation, preparation)) return false;
            var certainty = await _effectCertainty.ReadAsync(action.EffectQuery, cancellationToken).ConfigureAwait(false);
            return HumanReviewEffectReleaseReadStatusProjection.Project(action.EffectQuery, certainty) == HumanReviewEffectReleaseReadStatus.ExactNotStarted
                && certainty?.Snapshot is { } snapshot
                && string.Equals(snapshot.SnapshotHash, action.ReleaseReceipt?.EffectReceiptHash, StringComparison.Ordinal)
                && string.Equals(request.Binding.TargetHash, preparation.TargetFingerprint, StringComparison.Ordinal)
                && string.Equals(request.Binding.PreconditionHash, preparation.PreconditionEvidenceHash, StringComparison.Ordinal)
                && string.Equals(request.Binding.PayloadHash, preparation.InputFingerprint, StringComparison.Ordinal)
                && string.Equals(request.Binding.TargetHash, preparation.ReviewTargetHash, StringComparison.Ordinal)
                && string.Equals(request.Binding.PreconditionHash, preparation.ReviewPreconditionHash, StringComparison.Ordinal)
                && string.Equals(request.Binding.PayloadHash, preparation.ReviewPayloadHash, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private bool TryNow(DateTimeOffset runUpdatedAtUtc, DateTimeOffset claimedAtUtc, DateTimeOffset leaseExpiresAtUtc, out DateTimeOffset now)
    {
        try
        {
            now = _clock.GetUtcNow();
            return now != default && now.Offset == TimeSpan.Zero && now >= runUpdatedAtUtc && now >= claimedAtUtc && now < leaseExpiresAtUtc;
        }
        catch
        {
            now = default;
            return false;
        }
    }

    private static bool IsContinuationIntent(HumanReviewContinuationActionIntent? action, HumanReviewContinuationCompletionIntent? completion)
        => action is
        {
            Action: HumanReviewContinuationAction.ReleaseContinuation or HumanReviewContinuationAction.ReleaseEffect,
            Wake: not null,
            Claim: not null,
            Reservation: not null,
            ExpectedGeneration: > 0,
            ReleaseReceipt: not null,
        }
        && completion is not null
        && string.Equals(action.RunId, completion.RunId, StringComparison.Ordinal)
        && action.ExpectedLifecycleVersion == completion.ExpectedLifecycleVersion
        && Equals(action.Request, completion.Request)
        && Equals(action.Wake, completion.Wake)
        && Equals(action.Claim, completion.Claim)
        && Equals(action.Reservation, completion.Reservation)
        && action.ExpectedGeneration == completion.ExpectedGeneration
        && Equals(action.ReleaseReceipt, completion.ReleaseReceipt)
        && ((action.Action == HumanReviewContinuationAction.ReleaseContinuation
                && action.EffectQuery is null
                && action.ReleaseReceipt.Kind == HumanReviewContinuationReleaseKind.Continuation)
            || (action.Action == HumanReviewContinuationAction.ReleaseEffect
                && action.EffectQuery is not null
                && action.ReleaseReceipt.Kind == HumanReviewContinuationReleaseKind.PreDispatchEffect));

    private static bool IsDecisionIntent(HumanReviewDecisionActionIntent? intent)
        => intent is
        {
            Request: not null,
            Decision: { Kind: HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation },
            Wake: not null,
            Claim: not null,
            Reservation: not null,
            ExpectedGeneration: > 0,
        }
        && CustomLoopArtifactIdentifier.IsValid(intent.RunId)
        && intent.ExpectedLifecycleVersion >= 1
        && HumanReviewIdentifier.IsValid(intent.ActionOperationId);

    private static bool MatchesContext(CustomLoopRunRecord run, GovernedLoopWaitOrderedContext context)
    {
        if (run.SequentialAdapterBinding is not { } binding
            || !Equals(context.Anchor.AdapterBinding, binding)
            || !Equals(context.Plan.Revision, binding.ExecutionBinding.Revision)
            || !string.Equals(context.Plan.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(context.Plan.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(context.Artifact.ArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(context.Artifact.LayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)) return false;

        try
        {
            var rebuilt = GovernedLoopSequentialPlanBuilder.Build(context.Artifact);
            return rebuilt.Status == GovernedLoopSequentialPlanBuildStatus.Ready
                && rebuilt.Plan is not null
                && SamePlan(rebuilt.Plan, context.Plan);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasExactReviewNode(CustomLoopRunRecord run, HumanReviewRequest request, GovernedLoopWaitOrderedContext context, out GovernedLoopSequentialPlanNode? node, out GovernedLoopNodeExecutionEvidence? activation)
    {
        node = null;
        activation = ExactReviewActivation(run, request);
        if (activation is null) return false;
        node = context.Plan.Nodes.ElementAtOrDefault(activation.PlanOrdinal);
        var activationNodeId = activation.NodeId;
        var graphNodes = context.Artifact.Graph.Nodes.Where(item => string.Equals(item.Id, activationNodeId, StringComparison.Ordinal)).Take(2).ToArray();
        var graphNode = graphNodes.Length == 1 ? graphNodes[0] : null;
        return node is not null
            && graphNode is not null
            && run.SequentialAdapterBinding is { } binding
            && GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, context.Plan)
            && string.Equals(node.NodeId, activation.NodeId, StringComparison.Ordinal)
            && Equals(node.Descriptor, graphNode.Descriptor)
            && GovernedLoopSequentialNodeDescriptors.IsHumanReview(node.Descriptor)
            && HumanReviewOrderedNodeBindingContract.Matches(request.Binding, binding, graphNode);
    }

    private static bool HasExactPreDispatchEffectNode(CustomLoopRunRecord run, HumanReviewRequest request, GovernedLoopWaitOrderedContext context, out GovernedLoopSequentialPlanNode? node, out GovernedLoopNodeExecutionEvidence? activation)
    {
        node = null;
        activation = ExactReviewActivation(run, request);
        if (request.Purpose != HumanReviewPurpose.PreDispatchEffect
            || request.Binding.EffectAttempt is null
            || activation is null) return false;

        var reviewedActivation = activation;
        node = context.Plan.Nodes.ElementAtOrDefault(reviewedActivation.PlanOrdinal);
        var graphNodes = context.Artifact.Graph.Nodes.Where(item => string.Equals(item.Id, reviewedActivation.NodeId, StringComparison.Ordinal)).Take(2).ToArray();
        var graphNode = graphNodes.Length == 1 ? graphNodes[0] : null;
        return node is not null
            && graphNode is not null
            && run.SequentialAdapterBinding is { } binding
            && GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, context.Plan)
            && string.Equals(node.NodeId, reviewedActivation.NodeId, StringComparison.Ordinal)
            && Equals(node.Descriptor, graphNode.Descriptor)
            && GovernedLoopSequentialNodeDescriptors.IsRecoverableAction(node.Descriptor)
            && HumanReviewContractHash.MatchesEffectAttempt(request.Binding.EffectAttempt)
            && activation.Attempt is not null
            && !string.IsNullOrEmpty(activation.AttemptOperationId);
    }

    private static bool SamePlan(GovernedLoopSequentialPlan expected, GovernedLoopSequentialPlan actual)
        => expected.SchemaVersion == actual.SchemaVersion
            && Equals(expected.Revision, actual.Revision)
            && string.Equals(expected.GraphArtifactHash, actual.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(expected.GraphLayoutHash, actual.GraphLayoutHash, StringComparison.Ordinal)
            && expected.SchedulerPolicy.MaximumConcurrency == actual.SchedulerPolicy.MaximumConcurrency
            && expected.SchedulerPolicy.ReadyOrdering == actual.SchedulerPolicy.ReadyOrdering
            && expected.SchedulerPolicy.AllowsParallelEffectfulNodes == actual.SchedulerPolicy.AllowsParallelEffectfulNodes
            && expected.Nodes.Count == actual.Nodes.Count
            && expected.Nodes.Zip(actual.Nodes).All(pair => SamePlanNode(pair.First, pair.Second))
            && expected.ControlEdges.SequenceEqual(actual.ControlEdges)
            && expected.Components.Count == actual.Components.Count
            && expected.Components.Zip(actual.Components).All(pair => SameComponent(pair.First, pair.Second));

    private static bool SamePlanNode(GovernedLoopSequentialPlanNode expected, GovernedLoopSequentialPlanNode actual)
        => expected.StaticOrdinal == actual.StaticOrdinal
            && expected.Ordinal == actual.Ordinal
            && string.Equals(expected.NodeId, actual.NodeId, StringComparison.Ordinal)
            && Equals(expected.Descriptor, actual.Descriptor)
            && string.Equals(expected.ComponentId, actual.ComponentId, StringComparison.Ordinal)
            && string.Equals(expected.CycleId, actual.CycleId, StringComparison.Ordinal)
            && expected.ComponentTraversalOrdinal == actual.ComponentTraversalOrdinal
            && expected.IncomingControlEdgeIds.SequenceEqual(actual.IncomingControlEdgeIds, StringComparer.Ordinal)
            && expected.OutgoingControlEdgeIds.SequenceEqual(actual.OutgoingControlEdgeIds, StringComparer.Ordinal)
            && expected.Parameters.Count == actual.Parameters.Count
            && expected.Parameters.All(pair => actual.Parameters.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal))
            && Equals(expected.RetryPolicy, actual.RetryPolicy)
            && string.Equals(expected.IncomingControlEdgeId, actual.IncomingControlEdgeId, StringComparison.Ordinal)
            && string.Equals(expected.OutgoingControlEdgeId, actual.OutgoingControlEdgeId, StringComparison.Ordinal);

    private static bool SameComponent(GovernedLoopTopologyComponent expected, GovernedLoopTopologyComponent actual)
        => expected.StaticOrdinal == actual.StaticOrdinal
            && string.Equals(expected.ComponentId, actual.ComponentId, StringComparison.Ordinal)
            && string.Equals(expected.CycleId, actual.CycleId, StringComparison.Ordinal)
            && expected.IsCyclic == actual.IsCyclic
            && expected.NodeIds.SequenceEqual(actual.NodeIds, StringComparer.Ordinal)
            && expected.MaximumIterations == actual.MaximumIterations
            && expected.MaximumDurationMilliseconds == actual.MaximumDurationMilliseconds;

    private static bool TryGetContinuation(CustomLoopRunRecord run, HumanReviewContinuationActionIntent action, out HumanReviewRunState? review, out HumanReviewContinuationState? state, out HumanReviewContinuationClaim? claim)
    {
        review = run.HumanReview;
        state = review?.Continuation;
        claim = state?.Claims.IsDefaultOrEmpty == false ? state.Claims[^1] : null;
        return run.LifecycleVersion == action.ExpectedLifecycleVersion
            && run.Status == CustomLoopRunStatus.Paused
            && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            && review is not null
            && state is not null
            && state.Completion is null
            && state.Retirement is null
            && review.ContinuationReservation is not null
            && Equals(action.Request, new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash))
            && Equals(action.Decision, review.AcceptedTerminalDecision is null ? null : Reference(review.AcceptedTerminalDecision))
            && Equals(action.Wake, new HumanReviewContinuationWakeReference(state.Wake.WakeId, state.Wake.WakeHash))
            && Equals(action.Claim, claim is null ? null : new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash))
            && Equals(action.Reservation, new HumanReviewContinuationReservationReference(review.ContinuationReservation.ReservationId, review.ContinuationReservation.ReservationHash))
            && action.ExpectedGeneration == state.Wake.ExpectedGeneration
            && HumanReviewContinuationReleaseOperationId.Matches(action.ReleaseReceipt?.ReleaseOperationId, action.Request, action.Wake, action.Reservation, state.Wake.ExpectedGeneration, action.ReleaseReceipt?.Kind ?? HumanReviewContinuationReleaseKind.Unknown);
    }

    private static bool TryGetDecisionAction(CustomLoopRunRecord run, HumanReviewDecisionActionIntent intent, out HumanReviewRunState? review, out HumanReviewDecisionActionState? action, out HumanReviewDecisionActionClaim? claim)
    {
        review = run.HumanReview;
        action = review?.DecisionActions.SingleOrDefault(item => item is not null && Equals(Reference(item.Reservation), intent.Reservation));
        claim = action?.Claims.IsDefaultOrEmpty == false ? action.Claims[^1] : null;
        return run.LifecycleVersion == intent.ExpectedLifecycleVersion
            && run.Status == CustomLoopRunStatus.Paused
            && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            && review is not null
            && action is not null
            && action.Completion is null
            && action.Retirement is null
            && action.Wake is not null
            && HumanReviewDecisionActionContractValidator.IsCurrentActionHead(review, action)
            && Equals(intent.Request, new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash))
            && Equals(intent.Decision, action.Reservation.Decision)
            && Equals(intent.Wake, Reference(action.Wake))
            && Equals(intent.Claim, claim is null ? null : Reference(claim))
            && intent.ExpectedGeneration == action.ExpectedGeneration
            && string.Equals(intent.ActionOperationId, Id("action-operation", action.Reservation.ReservationHash), StringComparison.Ordinal);
    }

    private static bool TryContinuationReplay(CustomLoopRunRecord run, HumanReviewContinuationActionIntent action, out HumanReviewContinuationCompletion? completion)
    {
        var review = run.HumanReview;
        var state = review?.Continuation;
        completion = state?.Completion;
        var receipt = completion?.ReleaseReceipt;
        var activeClaim = state?.Claims.IsDefaultOrEmpty == false ? state.Claims[^1] : null;
        var terminal = ExactReviewTerminal(run, review?.Request, CustomLoopRunEventKind.NodeAttemptCompleted);
        var expectedResult = receipt?.Kind switch
        {
            HumanReviewContinuationReleaseKind.Continuation when terminal is not null => GovernedLoopHumanReviewReleaseReceiptHash.Compute(receipt.ReleaseOperationId, CustomLoopSequentialOutcomeArtifactHash.Compute(terminal), receipt.FrontierReceiptHash),
            HumanReviewContinuationReleaseKind.PreDispatchEffect when receipt.EffectReceiptHash is not null => GovernedLoopHumanReviewReleaseReceiptHash.Compute(receipt.ReleaseOperationId, receipt.EffectReceiptHash, receipt.FrontierReceiptHash),
            _ => null,
        };
        return review is not null
            && state is not null
            && completion is not null
            && receipt is not null
            && activeClaim is not null
            && review.ContinuationReservation is not null
            && Equals(action.Request, new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash))
            && Equals(action.Decision, review.AcceptedTerminalDecision is null ? null : Reference(review.AcceptedTerminalDecision))
            && Equals(action.Wake, Reference(state.Wake))
            && Equals(action.Reservation, new HumanReviewContinuationReservationReference(review.ContinuationReservation.ReservationId, review.ContinuationReservation.ReservationHash))
            && action.ExpectedGeneration == state.Wake.ExpectedGeneration
            && HumanReviewContinuationContractValidator.ValidateCompletion(review.Request, state.Wake, review.ContinuationReservation, activeClaim, completion).IsValid
            && string.Equals(receipt.ReleaseOperationId, action.ReleaseReceipt?.ReleaseOperationId, StringComparison.Ordinal)
            && Equals(receipt.Wake, action.Wake)
            && Equals(receipt.Reservation, action.Reservation)
            && receipt.ExpectedGeneration == action.ExpectedGeneration
            && receipt.Kind == action.ReleaseReceipt?.Kind
            && receipt.Disposition == HumanReviewContinuationReleaseDisposition.Released
            && string.Equals(receipt.EffectReceiptHash, action.ReleaseReceipt?.EffectReceiptHash, StringComparison.Ordinal)
            && expectedResult is not null
            && string.Equals(receipt.ResultHash, expectedResult, StringComparison.Ordinal)
            && MatchesRetainedFrontier(run, receipt.FrontierReceiptHash, receipt.ReleaseOperationId)
            && HumanReviewContinuationReleaseOperationId.Matches(receipt.ReleaseOperationId, action.Request, action.Wake, action.Reservation, action.ExpectedGeneration.GetValueOrDefault(), receipt.Kind);
    }

    private static bool TryDecisionReplay(CustomLoopRunRecord run, HumanReviewDecisionActionIntent intent, out HumanReviewDecisionActionCompletion? completion)
    {
        var review = run.HumanReview;
        var action = review?.DecisionActions.SingleOrDefault(item => item is not null && Equals(Reference(item.Reservation), intent.Reservation));
        completion = action?.Completion;
        var terminal = ExactReviewTerminal(run, review?.Request, CustomLoopRunEventKind.NodeAttemptFailed);
        var expectedResult = intent.Decision.Kind switch
        {
            HumanReviewDecisionKind.RequestInformation or HumanReviewDecisionKind.Cancel when completion is not null => GovernedLoopHumanReviewReleaseReceiptHash.Compute(intent.ActionOperationId, completion.FrontierReceiptHash, completion.FrontierReceiptHash),
            HumanReviewDecisionKind.Reject when terminal is not null && completion is not null => GovernedLoopHumanReviewReleaseReceiptHash.Compute(intent.ActionOperationId, CustomLoopSequentialOutcomeArtifactHash.Compute(terminal), completion.FrontierReceiptHash),
            _ => null,
        };
        return review is not null
            && action is not null
            && completion is not null
            && action.Wake is not null
            && HumanReviewDecisionActionContractValidator.ValidateState(review.Request, action).IsValid
            && Equals(intent.Request, new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash))
            && Equals(intent.Decision, action.Reservation.Decision)
            && Equals(intent.Wake, Reference(action.Wake))
            && Equals(intent.Reservation, Reference(action.Reservation))
            && intent.ExpectedGeneration == action.ExpectedGeneration
            && Equals(completion.Reservation, intent.Reservation)
            && Equals(completion.Wake, intent.Wake)
            && completion.ExpectedGeneration == intent.ExpectedGeneration
            && completion.Disposition == ExpectedDisposition(intent.Decision.Kind)
            && expectedResult is not null
            && string.Equals(completion.ResultHash, expectedResult, StringComparison.Ordinal)
            && MatchesRetainedFrontier(run, completion.FrontierReceiptHash, intent.ActionOperationId)
            && string.Equals(intent.ActionOperationId, Id("action-operation", intent.Reservation.ReservationHash), StringComparison.Ordinal);
    }

    private static CustomLoopRunEvent? ExactReviewTerminal(CustomLoopRunRecord run, HumanReviewRequest? request, CustomLoopRunEventKind kind)
    {
        var matches = run.Events.Where(item => item.Kind == kind
                && item.SequentialNodeEvidence is not null
                && string.Equals(item.StepId, request?.Binding.NodeId, StringComparison.Ordinal)
                && item.Attempt == request?.Binding.Attempt
                && CustomLoopSequentialOutcomeArtifactHash.Matches(item))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool MatchesRetainedFrontier(CustomLoopRunRecord run, string frontierReceiptHash, string operationId)
    {
        if (run.Frontier is null)
        {
            return false;
        }

        if (string.Equals(run.Frontier.Payload.ContentHash, frontierReceiptHash, StringComparison.Ordinal)) return true;
        if (IsOrderedHandoffPending(run, operationId)) return false;

        var handoffs = run.Events
            .Select((item, index) => (item, index))
            .Where(item => item.item.Kind == CustomLoopRunEventKind.LifecycleChanged && string.Equals(item.item.EventId, operationId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return handoffs.Length == 1 && handoffs[0].index < run.Events.Length - 1;
    }

    private static HumanReviewDecisionActionDisposition ExpectedDisposition(HumanReviewDecisionKind kind)
        => kind switch
        {
            HumanReviewDecisionKind.Reject => HumanReviewDecisionActionDisposition.Rejected,
            HumanReviewDecisionKind.Cancel => HumanReviewDecisionActionDisposition.Cancelled,
            HumanReviewDecisionKind.RequestInformation => HumanReviewDecisionActionDisposition.InformationParked,
            _ => HumanReviewDecisionActionDisposition.Unknown,
        };

    private static GovernedLoopNodeExecutionEvidence? ExactReviewActivation(CustomLoopRunRecord run, HumanReviewRequest request)
    {
        var matches = run.Frontier?.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked
                && string.Equals(node.NodeId, request.Binding.NodeId, StringComparison.Ordinal)
                && node.Attempt == request.Binding.Attempt
                && (request.Binding.ActivationOrdinal is null || node.ActivationOrdinal == request.Binding.ActivationOrdinal)
                && (request.Binding.VisitOrdinal is null || node.VisitOrdinal == request.Binding.VisitOrdinal))
            .Take(2)
            .ToArray() ?? [];
        return matches.Length == 1 ? matches[0] : null;
    }

    private static (IReadOnlyList<CustomLoopRunEvent> Events, IReadOnlyList<GovernedLoopSequentialSkipEvidenceReference> References)? CreatePruning(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlan plan,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopControlCondition outcome,
        DateTimeOffset now,
        string operationId)
    {
        var binding = run.SequentialAdapterBinding;
        var pruning = GovernedLoopSequentialFrontierMachine.PlanPruning(run.Frontier, binding, plan, activation, outcome);
        if (binding is null || pruning.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied) return null;

        var events = new List<CustomLoopRunEvent>();
        var references = new List<GovernedLoopSequentialSkipEvidenceReference>();
        foreach (var item in pruning.Activations)
        {
            var skipped = new CustomLoopRunEvent(
                run.Events.Length + events.Count + 1,
                Id("human-review-topology-skip", operationId + "\n" + item.Activation.ActivationOrdinal + "\n" + item.GoverningControlEdgeId),
                now,
                CustomLoopRunEventKind.TopologyNodeSkipped,
                item.Activation.CycleIteration,
                item.Activation.NodeId,
                null,
                $"Activation `{item.Activation.ActivationOrdinal}` was pruned by exact Human Review route edge `{item.GoverningControlEdgeId}`.",
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
            var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
                CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
                CustomLoopSequentialNodeEvidenceKind.TopologySkipped,
                binding.WorkspaceId,
                binding.ExecutionBinding.RunId,
                binding.ExecutionBinding.Revision,
                binding.ExecutionBinding.ExecutionGeneration,
                item.Activation.ActivationOrdinal,
                item.Activation.VisitOrdinal,
                item.Activation.NodeId,
                null,
                item.Activation.CycleId,
                item.Activation.CycleIteration,
                null,
                [],
                [],
                item.GoverningActivationOrdinal,
                item.GoverningControlEdgeId,
                CustomLoopSequentialNodeDisposition.Completed,
                CustomLoopSequentialOutcomeArtifactHash.Compute(skipped),
                string.Empty));
            skipped = skipped with { SequentialNodeEvidence = evidence };
            events.Add(skipped);
            references.Add(new GovernedLoopSequentialSkipEvidenceReference(item.Activation.ActivationOrdinal, item.GoverningActivationOrdinal, item.GoverningControlEdgeId, skipped.EventId, evidence.OutcomeArtifactHash));
        }

        return (events, references);
    }

    private static CustomLoopRunEvent TerminalEvent(CustomLoopRunRecord run, int priorEventCount, GovernedLoopNodeExecutionEvidence activation, DateTimeOffset now, CustomLoopRunEventKind kind, string detail)
        => new(
            run.Events.Length + priorEventCount + 1,
            Id("human-review-release", activation.OutcomeEvidenceHash + "\n" + kind),
            now,
            kind,
            activation.CycleIteration ?? 1,
            activation.NodeId,
            activation.Attempt,
            detail,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static CustomLoopRunEvent AttachEvidence(CustomLoopRunEvent item, GovernedLoopSequentialAdapterBinding binding, GovernedLoopNodeExecutionEvidence activation, CustomLoopSequentialNodeEvidenceKind kind, CustomLoopSequentialNodeDisposition disposition, GovernedLoopControlCondition? controlOutcome, IReadOnlyList<string> selectedControlEdgeIds, IReadOnlyList<string> skippedControlEdgeIds)
    {
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt,
            activation.CycleId,
            activation.CycleIteration,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(item),
            string.Empty));
        return item with { SequentialNodeEvidence = evidence };
    }

    private static CustomLoopRunEvent LifecycleEvent(CustomLoopRunRecord run, int priorEventCount, string operationId, DateTimeOffset now, string detail)
        => new(
            run.Events.Length + priorEventCount + 1,
            operationId,
            now,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            detail,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static HumanReviewContinuationReleaseResult Continuation(HumanReviewContinuationReleaseStatus status, HumanReviewContinuationCompletion? completion = null)
        => new(status, completion);

    private static HumanReviewDecisionActionReleaseResult Action(HumanReviewDecisionActionReleaseStatus status, HumanReviewDecisionActionCompletion? completion = null)
        => new(status, completion);

    private static HumanReviewDecisionReference Reference(HumanReviewDecision value)
        => new(value.DecisionId, value.DecisionOperationId, value.Kind, value.DecisionHash);

    private static HumanReviewContinuationWakeReference Reference(HumanReviewContinuationWake value)
        => new(value.WakeId, value.WakeHash);

    private static HumanReviewDecisionActionWakeReference Reference(HumanReviewDecisionActionWake value)
        => new(value.WakeId, value.WakeHash);

    private static HumanReviewDecisionActionClaimReference Reference(HumanReviewDecisionActionClaim value)
        => new(value.ClaimId, value.ClaimHash);

    private static HumanReviewDecisionActionReservationReference Reference(HumanReviewDecisionActionReservation value)
        => new(value.ReservationId, value.ReservationHash);

    private static string Id(string prefix, string value)
        => prefix + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}
