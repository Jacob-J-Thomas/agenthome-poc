using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Atomically commits one predecessor-to-ReviewBlocked frontier transition with its exact Human Review request.</summary>
public sealed class HumanReviewAdmissionService : IHumanReviewAdmissionService
{
    private readonly ICustomLoopRunStore _runs;

    /// <summary>Initializes the admission service.</summary>
    /// <param name="runs">The one canonical custom-loop run store.</param>
    public HumanReviewAdmissionService(ICustomLoopRunStore runs) => _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    /// <inheritdoc />
    public async Task<CustomLoopRunStoreResult> AdmitAsync(HumanReviewAdmissionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        ArgumentNullException.ThrowIfNull(command.BlockedFrontier);
        var current = await _runs.GetAsync(command.RunId, cancellationToken);
        if (current is null)
        {
            return CustomLoopRunStoreResult.NotFound();
        }

        if (!string.Equals(current.Id, command.RunId, StringComparison.Ordinal)
            || !IsCanonical(current))
        {
            return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
        }

        if (!HumanReviewContractSnapshot.TryCaptureRequest(command.Request, out var request, out _) || request is null)
        {
            return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
        }

        GovernedLoopFrontierPosture blockedFrontier;
        try
        {
            blockedFrontier = GovernedLoopFrontierPosture.Create(command.BlockedFrontier.Binding, command.BlockedFrontier.WorkspaceId, command.BlockedFrontier.GraphArtifactHash, command.BlockedFrontier.GraphLayoutHash, command.BlockedFrontier.AdmissionReceiptHash, command.BlockedFrontier.Payload);
        }
        catch (ArgumentException)
        {
            return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
        }

        var rotateCompletedReview = false;
        ImmutableArray<HumanReviewRunState> completedReviews = ImmutableArray<HumanReviewRunState>.Empty;
        if (current.HumanReview is { } existing)
        {
            if (string.Equals(existing.Request.RequestHash, request.RequestHash, StringComparison.Ordinal)
                && current.Frontier is { } retained
                && string.Equals(retained.Payload.ContentHash, blockedFrontier.Payload.ContentHash, StringComparison.Ordinal))
            {
                return CustomLoopRunStoreResult.AlreadyCreated(current);
            }

            if (string.Equals(existing.Request.RequestHash, request.RequestHash, StringComparison.Ordinal))
            {
                return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
            }

            if (!CanRotateCompletedReview(current, existing))
            {
                return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
            }

            rotateCompletedReview = true;
            completedReviews = existing.CompletedReviews.Add(existing with { CompletedReviews = ImmutableArray<HumanReviewRunState>.Empty });
        }

        var atUtc = blockedFrontier.Payload.UpdatedAtUtc;
        if (current.LifecycleVersion != command.ExpectedLifecycleVersion
            || current.LifecycleVersion == int.MaxValue
            || current.IsTerminal
            || current.Frontier?.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            || atUtc < request.Timing.CreatedAtUtc
            || atUtc < current.UpdatedAtUtc
            || atUtc > request.Timing.ExpiresAtUtc
            || current.ExecutionClock.ActiveSinceUtc is { } activeSinceUtc && atUtc < activeSinceUtc
            || !Matches(current, blockedFrontier, request))
        {
            return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
        }

        if (command.ReviewBlockedEvent is not null && !MatchesReviewBlockedEvent(current, blockedFrontier, command.ReviewBlockedEvent))
        {
            return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
        }

        var requestReference = new HumanReviewRequestReference(request.RequestId, request.RequestHash);
        var lifecycle = HumanReviewContractHash.ApplyLifecycle(new HumanReviewLifecycle(1, requestReference, HumanReviewLifecycleStatus.Pending, 1, atUtc, null, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-store", request.RequestOperationId, atUtc, string.Empty)), null, string.Empty));
        var evidence = HumanReviewContractHash.ApplyEvidence(new HumanReviewEvidence(1, Id("evidence", request.RequestId), requestReference, HumanReviewEvidenceKind.RequestAdmitted, null, atUtc, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-store", request.RequestOperationId, atUtc, string.Empty)), ImmutableArray<HumanReviewRedactedPreview>.Empty, null, string.Empty));
        var priorEvents = command.ReviewBlockedEvent is null ? current.Events : [.. current.Events, command.ReviewBlockedEvent];
        var lifecycleEvent = current.Status == CustomLoopRunStatus.Paused
            ? null
            : new CustomLoopRunEvent(
                priorEvents.LongLength + 1,
                Id("event", "paused-" + request.RequestId),
                atUtc,
                CustomLoopRunEventKind.LifecycleChanged,
                Iteration: null,
                StepId: null,
                Attempt: null,
                Detail: "Run paused with its exact Human Review blocked frontier.",
                ContextBlocks: [],
                CanonicalOutput: null,
                OriginalOutputCharacterCount: null,
                CanonicalOutputTruncated: null,
                RetainedForLoopReasoning: null,
                PublishedToInvokingConversation: null,
                ConversationPublicationId: null,
                Provider: null,
                Model: null,
                ProviderResponseId: null,
                ExitDecision: null);
        var runEvent = new CustomLoopRunEvent(
            priorEvents.LongLength + (lifecycleEvent is null ? 1 : 2),
            Id("event", evidence.EvidenceId),
            atUtc,
            CustomLoopRunEventKind.HumanReviewRequestAdmitted,
            Iteration: null,
            StepId: null,
            Attempt: null,
            Detail: "Human Review request and exact parked frontier were atomically admitted.",
            ContextBlocks: [],
            CanonicalOutput: null,
            OriginalOutputCharacterCount: null,
            CanonicalOutputTruncated: null,
            RetainedForLoopReasoning: null,
            PublishedToInvokingConversation: null,
            ConversationPublicationId: null,
            Provider: null,
            Model: null,
            ProviderResponseId: null,
            ExitDecision: null)
        { HumanReviewEvidence = evidence };
        var next = current with
        {
            LifecycleVersion = checked(current.LifecycleVersion + 1),
            UpdatedAtUtc = atUtc,
            Status = CustomLoopRunStatus.Paused,
            Frontier = blockedFrontier,
            ExecutionClock = StopClock(current.ExecutionClock, atUtc),
            HumanReview = new HumanReviewRunState(request, lifecycle, ImmutableArray.Create(evidence))
            {
                CompletedReviews = rotateCompletedReview ? completedReviews : ImmutableArray<HumanReviewRunState>.Empty,
            },
            Events = lifecycleEvent is null
                ? command.ReviewBlockedEvent is null ? [.. current.Events, runEvent] : [.. current.Events, command.ReviewBlockedEvent, runEvent]
                : command.ReviewBlockedEvent is null ? [.. current.Events, lifecycleEvent, runEvent] : [.. current.Events, command.ReviewBlockedEvent, lifecycleEvent, runEvent]
        };
        if (!IsCanonicalSuccessor(current, next))
        {
            return CustomLoopRunStoreResult.VersionConflict(current, command.ExpectedLifecycleVersion);
        }

        return await _runs.UpdateAsync(next, current.LifecycleVersion, cancellationToken);
    }

    private static bool CanRotateCompletedReview(CustomLoopRunRecord run, HumanReviewRunState review)
    {
        if (run.IsTerminal
            || run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            || review.AcceptedTerminalDecision is null
            || review.CompletedReviews.Length >= HumanReviewContractLimits.MaxCompletedReviews)
        {
            return false;
        }

        return review.Continuation is { Completion: not null } or { Retirement: not null }
            || review.DecisionActions.Any(action => action is not null && (action.Completion is not null || action.Retirement is not null));
    }

    private static bool IsCanonical(CustomLoopRunRecord run)
    {
        try
        {
            return CustomLoopRunValidator.Validate(run).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCanonicalSuccessor(CustomLoopRunRecord current, CustomLoopRunRecord candidate)
    {
        try
        {
            return CustomLoopRunValidator.ValidateUpdate(current, candidate).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool Matches(CustomLoopRunRecord run, GovernedLoopFrontierPosture frontier, HumanReviewRequest request)
    {
        var blockedNodes = frontier.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked).Take(2).ToArray();
        var blockedNode = blockedNodes.Length == 1 ? blockedNodes[0] : null;
        return string.Equals(run.Id, request.Binding.RunId, StringComparison.Ordinal)
            && string.Equals(frontier.WorkspaceId, request.Binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.RunId, run.Id, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.GraphId, request.Binding.GraphId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.RevisionId, request.Binding.RevisionId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.ExecutableHash, request.Binding.RevisionHash, StringComparison.Ordinal)
            && frontier.Payload.FrontierVersion == request.Binding.FrontierVersion
            && string.Equals(frontier.Payload.ContentHash, request.Binding.FrontierHash, StringComparison.Ordinal)
            && frontier.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            && blockedNode is not null
            && string.Equals(blockedNode.NodeId, request.Binding.NodeId, StringComparison.Ordinal)
            && blockedNode.Attempt == request.Binding.Attempt
            && (request.Binding.ActivationOrdinal is null || blockedNode.ActivationOrdinal == request.Binding.ActivationOrdinal)
            && (request.Binding.VisitOrdinal is null || blockedNode.VisitOrdinal == request.Binding.VisitOrdinal);
    }

    private static string Id(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static bool MatchesReviewBlockedEvent(CustomLoopRunRecord current, GovernedLoopFrontierPosture blockedFrontier, CustomLoopRunEvent reviewBlockedEvent)
    {
        var blocked = blockedFrontier.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked).Take(2).ToArray();
        var activation = blocked.Length == 1 ? blocked[0] : null;
        var evidence = reviewBlockedEvent.SequentialNodeEvidence;
        return activation is not null
            && reviewBlockedEvent.Sequence == current.Events.LongLength + 1
            && reviewBlockedEvent.TimestampUtc == blockedFrontier.Payload.UpdatedAtUtc
            && reviewBlockedEvent.Kind == CustomLoopRunEventKind.NodeOutcomeObserved
            && string.Equals(reviewBlockedEvent.StepId, activation.NodeId, StringComparison.Ordinal)
            && reviewBlockedEvent.Attempt == activation.Attempt
            && reviewBlockedEvent.HumanReviewEvidence is null
            && reviewBlockedEvent.HumanReviewDecisionOperation is null
            && reviewBlockedEvent.HumanReviewContinuationReservation is null
            && evidence is not null
            && evidence.Kind == CustomLoopSequentialNodeEvidenceKind.ReviewRequested
            && evidence.Disposition == CustomLoopSequentialNodeDisposition.ReviewPending
            && evidence.ActivationOrdinal == activation.ActivationOrdinal
            && evidence.VisitOrdinal == activation.VisitOrdinal
            && string.Equals(evidence.NodeId, activation.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == activation.Attempt
            && string.Equals(evidence.OutcomeArtifactHash, activation.OutcomeEvidenceHash, StringComparison.Ordinal)
            && string.Equals(reviewBlockedEvent.EventId, activation.OutcomeEvidenceId, StringComparison.Ordinal);
    }

    private static CustomLoopExecutionClock StopClock(CustomLoopExecutionClock clock, DateTimeOffset atUtc)
    {
        var accumulated = Math.Clamp(clock.AccumulatedRunningMilliseconds, 0, CustomLoopLimits.MaxRunExecutionMilliseconds);
        if (clock.ActiveSinceUtc is { } activeSinceUtc)
        {
            var elapsed = Math.Max(0, (long)(atUtc - activeSinceUtc).TotalMilliseconds);
            accumulated = elapsed >= CustomLoopLimits.MaxRunExecutionMilliseconds - accumulated
                ? CustomLoopLimits.MaxRunExecutionMilliseconds
                : accumulated + elapsed;
        }

        return new CustomLoopExecutionClock(accumulated, null);
    }
}
