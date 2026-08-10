using EmbodySense.Core.Application.Loops.Compatibility.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

internal static class CustomLoopCompatibilityMapper
{
    internal static GovernedLoopCompatibilityProjectionResult Project(CustomLoopRunRecord? run)
    {
        if (run is null || !CustomLoopRunValidator.Validate(run).IsValid)
        {
            return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.CustomLoop);
        }

        try
        {
            var gaps = BaselineGaps();
            var effects = ProviderObservations(run, gaps)
                .Concat(ToolObservations(run, gaps))
                .Concat(PublicationObservations(run, gaps))
                .Take(GovernedLoopCompatibilityLimits.MaxEffectObservations + 1)
                .ToArray();
            if (effects.Length > GovernedLoopCompatibilityLimits.MaxEffectObservations)
            {
                return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.CustomLoop, GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded);
            }

            if (effects.Length > 0)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.CanonicalEffectIntentUnavailable);
            }

            if (run.Status == CustomLoopRunStatus.Failed)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.CanonicalFailureUnavailable);
            }

            if (run.Status == CustomLoopRunStatus.NeedsReview)
            {
                gaps.Add(GovernedLoopCompatibilityGapCode.ReviewDispositionUnavailable);
            }

            var payload = new GovernedLoopCompatibilityPayload(CreateLifecycle(run), null, effects, []);
            return new GovernedLoopCompatibilityPartialResult(GovernedLoopCompatibilitySource.CustomLoop, payload, gaps.Order().Select(GovernedLoopCompatibilityGap.Create));
        }
        catch (ArgumentException)
        {
            return new GovernedLoopCompatibilityUnsupportedResult(GovernedLoopCompatibilitySource.CustomLoop);
        }
    }

    private static GovernedLoopRunLifecyclePayload CreateLifecycle(CustomLoopRunRecord run)
    {
        var status = run.Status switch
        {
            CustomLoopRunStatus.Admitted => GovernedLoopRunStatus.Admitted,
            CustomLoopRunStatus.Running => GovernedLoopRunStatus.Running,
            CustomLoopRunStatus.PauseRequested => GovernedLoopRunStatus.PauseRequested,
            CustomLoopRunStatus.Paused => GovernedLoopRunStatus.Paused,
            CustomLoopRunStatus.CancelRequested => GovernedLoopRunStatus.CancelRequested,
            CustomLoopRunStatus.Completed => GovernedLoopRunStatus.Completed,
            CustomLoopRunStatus.Failed => GovernedLoopRunStatus.Failed,
            CustomLoopRunStatus.Cancelled => GovernedLoopRunStatus.Cancelled,
            CustomLoopRunStatus.NeedsReview => GovernedLoopRunStatus.NeedsReview,
            _ => throw new ArgumentOutOfRangeException(nameof(run), "The validated source had no supported lifecycle mapping.")
        };
        var terminal = GovernedLoopExecutionStateMatrix.IsTerminal(status);
        var trailingIntegrityWarning = terminal && run.Events.LastOrDefault()?.Kind == CustomLoopRunEventKind.IntegrityWarning;
        var lifecycleVersion = trailingIntegrityWarning ? run.LifecycleVersion - 1L : run.LifecycleVersion;
        var updatedAtUtc = terminal ? run.CompletedAtUtc!.Value : run.UpdatedAtUtc;
        return GovernedLoopRunLifecyclePayload.Create(GovernedLoopExecutionLimits.CurrentSchemaVersion, lifecycleVersion, status, run.CreatedAtUtc, updatedAtUtc, terminal ? updatedAtUtc : null);
    }

    private static IEnumerable<GovernedLoopCompatibilityEffectObservation> ProviderObservations(CustomLoopRunRecord run, ISet<GovernedLoopCompatibilityGapCode> gaps)
    {
        foreach (var start in run.Events.Where(item => item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted))
        {
            var related = run.Events.Where(item => item.Sequence > start.Sequence && SameAttempt(start, item)).ToArray();
            var succeeded = related.LastOrDefault(item => item.Kind is CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeOutcomeObserved);
            var failed = related.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);
            GovernedLoopEffectPhase phase;
            GovernedLoopEffectOutcome outcome;
            GovernedLoopEffectEvidenceStatus evidenceStatus;
            CustomLoopRunEvent evidence;
            if (succeeded is not null)
            {
                phase = GovernedLoopEffectPhase.OutcomeObserved;
                outcome = GovernedLoopEffectOutcome.Succeeded;
                evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                evidence = succeeded;
                gaps.Add(GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
            }
            else if (failed is not null)
            {
                phase = GovernedLoopEffectPhase.ReconciliationRequired;
                outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                evidence = failed;
                gaps.Add(GovernedLoopCompatibilityGapCode.ProviderDispatchBoundaryUnavailable);
                gaps.Add(GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
            }
            else
            {
                phase = GovernedLoopEffectPhase.ReconciliationRequired;
                outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                evidence = start;
                gaps.Add(GovernedLoopCompatibilityGapCode.ProviderDispatchBoundaryUnavailable);
            }

            yield return new GovernedLoopCompatibilityEffectObservation(start.EventId, start.ProviderResponseId ?? start.EventId, start.Attempt ?? 1, GovernedLoopEffectOrigin.Provider, phase, outcome, evidenceStatus, evidence.EventId, null, evidence.TimestampUtc);
        }
    }

    private static IEnumerable<GovernedLoopCompatibilityEffectObservation> PublicationObservations(CustomLoopRunRecord run, ISet<GovernedLoopCompatibilityGapCode> gaps)
    {
        var publications = run.Events
            .Where(item => item.Kind is CustomLoopRunEventKind.ConversationPublicationStarted or CustomLoopRunEventKind.ConversationPublished)
            .GroupBy(item => item.ConversationPublicationId!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in publications)
        {
            var final = group.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
            var evidence = final ?? group.Last();
            var phase = GovernedLoopEffectPhase.IntentPrepared;
            var outcome = GovernedLoopEffectOutcome.None;
            var evidenceStatus = GovernedLoopEffectEvidenceStatus.Pending;
            if (final?.PublishedToInvokingConversation == true)
            {
                phase = GovernedLoopEffectPhase.OutcomeObserved;
                outcome = GovernedLoopEffectOutcome.Succeeded;
                evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
            }
            else if (final is not null)
            {
                phase = GovernedLoopEffectPhase.ReconciliationRequired;
                outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                gaps.Add(GovernedLoopCompatibilityGapCode.PublicationOutcomeConflated);
            }
            else
            {
                phase = GovernedLoopEffectPhase.ReconciliationRequired;
                outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                gaps.Add(GovernedLoopCompatibilityGapCode.PublicationDispatchBoundaryUnavailable);
            }

            yield return new GovernedLoopCompatibilityEffectObservation(group.Key, group.Key, evidence.Iteration ?? 1, GovernedLoopEffectOrigin.Publication, phase, outcome, evidenceStatus, evidence.EventId, null, evidence.TimestampUtc);
        }
    }

    private static IEnumerable<GovernedLoopCompatibilityEffectObservation> ToolObservations(CustomLoopRunRecord run, ISet<GovernedLoopCompatibilityGapCode> gaps)
    {
        var groups = run.Events
            .Where(item => item.ToolEvidence is not null)
            .GroupBy(item => item.ToolEvidence!.RequestCorrelationId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var evidence = group.OrderBy(item => item.Sequence).Last();
            var tool = evidence.ToolEvidence!;
            var phase = GovernedLoopEffectPhase.IntentPrepared;
            var outcome = GovernedLoopEffectOutcome.None;
            var evidenceStatus = GovernedLoopEffectEvidenceStatus.Pending;
            if (evidence.Kind == CustomLoopRunEventKind.ToolGovernanceDecided)
            {
                var definitelyDenied = tool.Governance?.AuthorityDecision == ToolAuthorityDecision.Denied
                    || tool.Governance?.PermissionDecision == PermissionDecision.Deny
                    || tool.Governance?.ApprovalDecision is ToolApprovalDecision.Rejected or ToolApprovalDecision.Requested;
                if (definitelyDenied)
                {
                    phase = GovernedLoopEffectPhase.DispatchNotStarted;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
                }
                else
                {
                    phase = GovernedLoopEffectPhase.ReconciliationRequired;
                    outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                    gaps.Add(GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
                }
            }
            else if (evidence.Kind == CustomLoopRunEventKind.ToolOutcomeObserved && tool.Outcome is { } toolOutcome)
            {
                if (toolOutcome is ToolExecutionOutcome.Denied or ToolExecutionOutcome.ApprovalRejected)
                {
                    phase = GovernedLoopEffectPhase.DispatchNotStarted;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
                }
                else if (toolOutcome == ToolExecutionOutcome.Succeeded)
                {
                    phase = GovernedLoopEffectPhase.OutcomeObserved;
                    outcome = GovernedLoopEffectOutcome.Succeeded;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
                }
                else
                {
                    phase = GovernedLoopEffectPhase.ReconciliationRequired;
                    outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                    gaps.Add(GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
                }
            }
            else if (evidence.Kind == CustomLoopRunEventKind.ToolIntegrityFailed)
            {
                if (tool.Outcome == ToolExecutionOutcome.Succeeded)
                {
                    phase = GovernedLoopEffectPhase.OutcomeObserved;
                    outcome = GovernedLoopEffectOutcome.Succeeded;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                }
                else if (tool.Outcome is ToolExecutionOutcome.Denied or ToolExecutionOutcome.ApprovalRejected)
                {
                    phase = GovernedLoopEffectPhase.DispatchNotStarted;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Complete;
                }
                else
                {
                    phase = GovernedLoopEffectPhase.ReconciliationRequired;
                    outcome = GovernedLoopEffectOutcome.OutcomeUnknown;
                    evidenceStatus = GovernedLoopEffectEvidenceStatus.Incomplete;
                    gaps.Add(GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
                }

                gaps.Add(GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
            }

            yield return new GovernedLoopCompatibilityEffectObservation(tool.RequestCorrelationId, tool.BrokerRequestId ?? tool.RequestCorrelationId, tool.RequestOrdinal, GovernedLoopEffectOrigin.Actuator, phase, outcome, evidenceStatus, evidence.EventId, null, evidence.TimestampUtc);
        }
    }

    private static bool SameAttempt(CustomLoopRunEvent start, CustomLoopRunEvent candidate)
    {
        var expectedCompletion = start.Kind == CustomLoopRunEventKind.ExitDecisionStarted
            ? candidate.Kind is CustomLoopRunEventKind.ExitDecisionCompleted or CustomLoopRunEventKind.NodeAttemptFailed
            : candidate.Kind is CustomLoopRunEventKind.NodeOutcomeObserved or CustomLoopRunEventKind.NodeAttemptCompleted or CustomLoopRunEventKind.NodeAttemptFailed;
        return expectedCompletion
            && candidate.Iteration == start.Iteration
            && candidate.Attempt == start.Attempt
            && string.Equals(candidate.StepId, start.StepId, StringComparison.Ordinal);
    }

    private static HashSet<GovernedLoopCompatibilityGapCode> BaselineGaps()
    {
        return
        [
            GovernedLoopCompatibilityGapCode.ExactRevisionUnavailable,
            GovernedLoopCompatibilityGapCode.ExecutionBindingUnavailable,
            GovernedLoopCompatibilityGapCode.DurableFrontierUnavailable,
            GovernedLoopCompatibilityGapCode.ProjectionEvidenceUnavailable,
            GovernedLoopCompatibilityGapCode.CanonicalLifecycleHistoryUnavailable
        ];
    }
}
