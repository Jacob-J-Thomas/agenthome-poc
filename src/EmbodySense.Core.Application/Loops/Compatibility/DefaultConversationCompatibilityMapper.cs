using EmbodySense.Core.Application.Loops.Compatibility.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

internal static class DefaultConversationCompatibilityMapper
{
    internal static GovernedLoopCompatibilityProjectionResult Project(DefaultConversationTurnRecord? record)
    {
        if (!IsValid(record))
        {
            return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.DefaultConversation);
        }

        try
        {
            var source = record!;
            var effects = new List<GovernedLoopCompatibilityEffectObservation>();
            var projections = new List<GovernedLoopCompatibilityProjectionObservation>();
            var gaps = BaselineGaps();
            AddProvider(source, effects, gaps);
            AddPublication(source, source.UserPublicationId, DefaultConversationTurnCheckpoint.UserPublicationPrepared, DefaultConversationTurnCheckpoint.UserPublished, effects, gaps);
            AddPublication(source, source.AssistantPublicationId, DefaultConversationTurnCheckpoint.AssistantPublicationPrepared, DefaultConversationTurnCheckpoint.AssistantPublished, effects, gaps);
            AddProjections(source, projections, gaps);
            if (effects.Count > 0)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.CanonicalEffectIntentUnavailable);
            }

            if (source.Run.Status == LoopRunStatus.Failed)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.CanonicalFailureUnavailable);
            }

            if (source.Run.Status == LoopRunStatus.NeedsReview || source.ReviewResolution is not null)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.ReviewDispositionUnavailable);
            }

            var payload = new GovernedLoopCompatibilityPayload(CreateLifecycle(source), null, effects, projections);
            return new GovernedLoopCompatibilityPartialResult(GovernedLoopCompatibilitySource.DefaultConversation, payload, CreateGaps(gaps));
        }
        catch (ArgumentException)
        {
            return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.DefaultConversation);
        }
    }

    private static bool IsValid(DefaultConversationTurnRecord? record)
    {
        if (record is null)
        {
            return false;
        }

        try
        {
            DefaultConversationTurnProtocolValidator.Validate(record);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static GovernedLoopRunLifecyclePayload CreateLifecycle(DefaultConversationTurnRecord record)
    {
        var status = record.Run.Status switch
        {
            LoopRunStatus.Started when record.Checkpoint == DefaultConversationTurnCheckpoint.Admitted => GovernedLoopRunStatus.Admitted,
            LoopRunStatus.Started => GovernedLoopRunStatus.Running,
            LoopRunStatus.Completed => GovernedLoopRunStatus.Completed,
            LoopRunStatus.Failed => GovernedLoopRunStatus.Failed,
            LoopRunStatus.Cancelled => GovernedLoopRunStatus.Cancelled,
            LoopRunStatus.NeedsReview => GovernedLoopRunStatus.NeedsReview,
            _ => throw new ArgumentOutOfRangeException(nameof(record), "The validated source had no supported lifecycle mapping.")
        };
        var transition = status switch
        {
            GovernedLoopRunStatus.Admitted => record.Transitions[0],
            GovernedLoopRunStatus.Running => Find(record, DefaultConversationTurnCheckpoint.RunStarted),
            _ => Find(record, DefaultConversationTurnCheckpoint.TerminalPrepared)
        };
        DateTimeOffset? terminalAtUtc = GovernedLoopExecutionStateMatrix.IsTerminal(status) ? record.Run.CompletedAtUtc!.Value : null;
        var updatedAtUtc = terminalAtUtc ?? transition.OccurredAtUtc;
        return GovernedLoopRunLifecyclePayload.Create(GovernedLoopExecutionLimits.CurrentSchemaVersion, transition.Sequence, status, record.Run.StartedAtUtc, updatedAtUtc, terminalAtUtc);
    }

    private static void AddProvider(DefaultConversationTurnRecord record, ICollection<GovernedLoopCompatibilityEffectObservation> effects, ISet<GovernedLoopCompatibilityGapCode> gaps)
    {
        var prepared = FindOptional(record, DefaultConversationTurnCheckpoint.ProviderDispatchPrepared);
        if (prepared is null)
        {
            return;
        }

        var phase = GovernedLoopEffectPhase.DispatchNotStarted;
        var outcome = GovernedLoopEffectOutcome.None;
        var evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
        var evidence = prepared;
        string? reconciliationEvidenceId = null;
        if (record.ReviewResolution is not null)
        {
            phase = GovernedLoopEffectPhase.Reconciled;
            outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
            evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
            evidence = record.Transitions[^1];
            reconciliationEvidenceId = record.ReviewResolution.ResolutionId;
        }
        else if (record.Run.Status == LoopRunStatus.NeedsReview && record.ProviderOutcome == DefaultConversationProviderOutcome.OutcomeUnknown)
        {
            phase = GovernedLoopEffectPhase.ReconciliationRequired;
            outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
            evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
            evidence = Find(record, DefaultConversationTurnCheckpoint.ProviderDispatchStarted);
        }
        else
        {
            (phase, outcome, evidenceStatus, evidence) = record.ProviderOutcome switch
            {
                DefaultConversationProviderOutcome.DefinitelyNotStarted => (GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete, prepared),
                DefaultConversationProviderOutcome.OutcomeUnknown => (GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, Find(record, DefaultConversationTurnCheckpoint.ProviderDispatchStarted)),
                DefaultConversationProviderOutcome.Observed => (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, Find(record, DefaultConversationTurnCheckpoint.ProviderOutcomeObserved)),
                DefaultConversationProviderOutcome.ObservedFailure => (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Failed, GovernedLoopEffectEvidenceStatus.Complete, Find(record, DefaultConversationTurnCheckpoint.ProviderOutcomeObserved)),
                DefaultConversationProviderOutcome.ObservedWithAuditFailure => (GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete, Find(record, DefaultConversationTurnCheckpoint.ProviderOutcomeObserved)),
                _ => throw new ArgumentOutOfRangeException(nameof(record), "The validated provider outcome had no supported mapping.")
            };
        }

        if (record.ProviderOutcome == DefaultConversationProviderOutcome.ObservedWithAuditFailure)
        {
            gaps.Add(GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
        }

        effects.Add(new GovernedLoopCompatibilityEffectObservation(record.ProviderAttemptId, record.ProviderCorrelationId, 1, GovernedLoopEffectOrigin.Provider, phase, outcome, evidenceStatus, evidence.TransitionId, reconciliationEvidenceId, evidence.OccurredAtUtc));
    }

    private static void AddPublication(
        DefaultConversationTurnRecord record,
        string publicationId,
        DefaultConversationTurnCheckpoint preparedCheckpoint,
        DefaultConversationTurnCheckpoint publishedCheckpoint,
        ICollection<GovernedLoopCompatibilityEffectObservation> effects,
        ISet<GovernedLoopCompatibilityGapCode> gaps)
    {
        var prepared = FindOptional(record, preparedCheckpoint);
        if (prepared is null)
        {
            return;
        }

        var published = FindOptional(record, publishedCheckpoint);
        if (published is null)
        {
            effects.Add(new GovernedLoopCompatibilityEffectObservation(publicationId, publicationId, 1, GovernedLoopEffectOrigin.Publication, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, prepared.TransitionId, null, prepared.OccurredAtUtc));
            gaps.Add(GovernedLoopCompatibilityGapCode.PublicationDispatchBoundaryUnavailable);
            return;
        }

        effects.Add(new GovernedLoopCompatibilityEffectObservation(publicationId, publicationId, 1, GovernedLoopEffectOrigin.Publication, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, published.TransitionId, null, published.OccurredAtUtc));
    }

    private static void AddProjections(DefaultConversationTurnRecord record, ICollection<GovernedLoopCompatibilityProjectionObservation> projections, ISet<GovernedLoopCompatibilityGapCode> gaps)
    {
        var transcript = FindOptional(record, DefaultConversationTurnCheckpoint.TranscriptSynchronized);
        if (transcript is not null)
        {
            projections.Add(new GovernedLoopCompatibilityProjectionObservation(record.ConversationId, record.AssistantPublicationId, GovernedLoopProjectionClass.LocalRuntime, GovernedLoopProjectionStatus.Committed, transcript.TransitionId, record.AssistantPublicationId, transcript.OccurredAtUtc));
        }

        var terminalPrepared = FindOptional(record, DefaultConversationTurnCheckpoint.TerminalPrepared);
        if (terminalPrepared is null)
        {
            if (projections.Count > 0)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.ProjectionEvidenceUnavailable);
            }

            return;
        }

        var terminal = FindOptional(record, DefaultConversationTurnCheckpoint.Terminal);
        var evidence = terminal ?? terminalPrepared;
        var status = terminal is null ? GovernedLoopProjectionStatus.Pending : GovernedLoopProjectionStatus.Committed;
        projections.Add(new GovernedLoopCompatibilityProjectionObservation(record.Run.RunId, record.Run.RunId, GovernedLoopProjectionClass.LocalRuntime, status, evidence.TransitionId, null, evidence.OccurredAtUtc));
        if (record.ReviewCause == DefaultConversationTurnReviewCause.TranscriptConflict)
        {
            var relatedPublication = FindOptional(record, DefaultConversationTurnCheckpoint.AssistantPublicationPrepared) is null ? null : record.AssistantPublicationId;
            projections.Add(new GovernedLoopCompatibilityProjectionObservation(record.ConversationId, record.AssistantPublicationId, GovernedLoopProjectionClass.DurableReadModel, GovernedLoopProjectionStatus.ReconciliationRequired, terminalPrepared.TransitionId, relatedPublication, terminalPrepared.OccurredAtUtc));
        }

        gaps.Add(GovernedLoopCompatibilityGapCode.ProjectionEvidenceUnavailable);
    }

    private static HashSet<GovernedLoopCompatibilityGapCode> BaselineGaps()
    {
        return
        [
            GovernedLoopCompatibilityGapCode.ExactRevisionUnavailable,
            GovernedLoopCompatibilityGapCode.ExecutionBindingUnavailable,
            GovernedLoopCompatibilityGapCode.DurableFrontierUnavailable,
            GovernedLoopCompatibilityGapCode.CanonicalLifecycleHistoryUnavailable
        ];
    }

    private static IReadOnlyList<GovernedLoopCompatibilityGap> CreateGaps(IEnumerable<GovernedLoopCompatibilityGapCode> codes)
    {
        return codes.Order().Select(GovernedLoopCompatibilityGap.Create).ToArray();
    }

    private static DefaultConversationTurnTransition Find(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint checkpoint)
    {
        return FindOptional(record, checkpoint) ?? throw new ArgumentException($"Validated default-conversation evidence was missing `{checkpoint}`.", nameof(record));
    }

    private static DefaultConversationTurnTransition? FindOptional(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint checkpoint)
    {
        return record.Transitions.LastOrDefault(transition => transition.Checkpoint == checkpoint);
    }
}
