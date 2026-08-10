using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Compatibility;
using EmbodySense.Core.Application.Loops.Compatibility.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Compatibility;

public sealed class GovernedLoopCompatibilityProjectorTests
{
    private const string RequestId = "request-compatibility";
    private static readonly DateTimeOffset _startedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProjectDefaultConversation_returns_sorted_partial_unbound_payload()
    {
        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(CreateDefaultAdmitted()));

        Assert.Equal(GovernedLoopCompatibilityProjectionStatus.Partial, result.Status);
        Assert.Equal(GovernedLoopCompatibilitySource.DefaultConversation, result.Source);
        Assert.Equal(GovernedLoopRunStatus.Admitted, result.Payload.Lifecycle.Status);
        Assert.Null(result.Payload.Frontier);
        Assert.Empty(result.Payload.Effects);
        Assert.Equal(result.Gaps.OrderBy(gap => gap.Code), result.Gaps);
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ExactRevisionUnavailable);
        Assert.All(result.Gaps, gap => Assert.InRange(gap.Detail.Length, 1, GovernedLoopCompatibilityGap.MaxDetailCharacters));
    }

    [Fact]
    public void ProjectDefaultConversation_maps_typed_provider_unknown_without_fabricating_intent()
    {
        var record = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(record));
        var effect = Assert.Single(result.Payload.Effects, item => item.Origin == GovernedLoopEffectOrigin.Provider);

        Assert.Equal(GovernedLoopEffectPhase.DispatchBoundaryReached, effect.Phase);
        Assert.Equal(GovernedLoopEffectOutcome.OutcomeUnknown, effect.Outcome);
        Assert.Equal(GovernedLoopEffectEvidenceStatus.Pending, effect.EvidenceStatus);
        Assert.Equal(record.ProviderAttemptId, effect.EffectId);
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.CanonicalEffectIntentUnavailable);
    }

    [Fact]
    public void ProjectCustomLoop_returns_partial_lifecycle_without_inventing_frontier_or_projection()
    {
        var run = CreateCustomAdmitted();

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(run));

        Assert.Equal(GovernedLoopCompatibilitySource.CustomLoop, result.Source);
        Assert.Equal(GovernedLoopRunStatus.Admitted, result.Payload.Lifecycle.Status);
        Assert.Null(result.Payload.Frontier);
        Assert.Empty(result.Payload.Effects);
        Assert.Empty(result.Payload.Projections);
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.DurableFrontierUnavailable);
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ProjectionEvidenceUnavailable);
    }

    [Fact]
    public void ProjectCustomLoop_accepts_the_valid_single_admission_event_crash_window()
    {
        var admitted = CreateCustomAdmitted();
        var crashWindow = admitted with
        {
            LifecycleVersion = 1,
            Events = [admitted.Events[0]]
        };
        Assert.True(CustomLoopRunValidator.Validate(crashWindow).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(crashWindow).Errors));

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(crashWindow));

        Assert.Equal(GovernedLoopRunStatus.Admitted, result.Payload.Lifecycle.Status);
        Assert.Equal(1, result.Payload.Lifecycle.LifecycleVersion);
    }

    [Fact]
    public void ProjectDefaultConversation_maps_success_failure_audit_and_publication_from_typed_fields()
    {
        var success = SynchronizeDefaultTerminal(PrepareDefaultTerminal(AdvanceDefaultTo(DefaultConversationTurnCheckpoint.TranscriptSynchronized), LoopRunStatus.Completed));
        var failure = SynchronizeDefaultTerminal(PrepareDefaultTerminal(CreateDefaultObservedFailure(), LoopRunStatus.Failed));
        var observed = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved);
        var auditFailure = SynchronizeDefaultTerminal(PrepareDefaultTerminal(observed with { ProviderOutcome = DefaultConversationProviderOutcome.ObservedWithAuditFailure }, LoopRunStatus.NeedsReview));

        var successResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(success));
        var successProvider = Assert.Single(successResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Provider);
        Assert.Equal(GovernedLoopRunStatus.Completed, successResult.Payload.Lifecycle.Status);
        Assert.Equal((GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete), (successProvider.Outcome, successProvider.EvidenceStatus));
        Assert.Contains(successResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Publication && effect.Outcome == GovernedLoopEffectOutcome.Succeeded);
        Assert.All(successResult.Payload.Projections, projection => Assert.Equal(GovernedLoopProjectionStatus.Committed, projection.Status));

        var failureResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(failure));
        Assert.Equal(GovernedLoopRunStatus.Failed, failureResult.Payload.Lifecycle.Status);
        Assert.Equal(GovernedLoopEffectOutcome.Failed, Assert.Single(failureResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Provider).Outcome);
        Assert.Contains(failureResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.CanonicalFailureUnavailable);

        var auditResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(auditFailure));
        var auditProvider = Assert.Single(auditResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Provider);
        Assert.Equal((GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete), (auditProvider.Outcome, auditProvider.EvidenceStatus));
        Assert.Contains(auditResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
        Assert.Contains(auditResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ReviewDispositionUnavailable);
    }

    [Fact]
    public void ProjectDefaultConversation_retains_authoritative_run_completion_time_before_terminal_preparation()
    {
        var operational = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.TranscriptSynchronized);
        var completedAtUtc = operational.Transitions[^1].OccurredAtUtc.AddMilliseconds(250);
        var terminalPreparedAtUtc = completedAtUtc.AddMilliseconds(250);
        var run = operational.Run.Complete(completedAtUtc);
        var terminalPrepared = operational.Advance(
            DefaultConversationTurnCheckpoint.TerminalPrepared,
            terminalPreparedAtUtc,
            "Terminal prepared after the authoritative run completion time.",
            run: run);
        DefaultConversationTurnProtocolValidator.Validate(terminalPrepared);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(terminalPrepared));

        Assert.Equal(completedAtUtc, result.Payload.Lifecycle.UpdatedAtUtc);
        Assert.Equal(completedAtUtc, result.Payload.Lifecycle.TerminalAtUtc);
        Assert.NotEqual(terminalPreparedAtUtc, result.Payload.Lifecycle.TerminalAtUtc);
    }

    [Fact]
    public void ProjectDefaultConversation_keeps_prepared_publication_conflict_cancel_and_restart_distinct()
    {
        var userPublicationPrepared = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.UserPublicationPrepared);
        var publicationPrepared = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.AssistantPublicationPrepared);
        var providerPrepared = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.ProviderDispatchPrepared);
        var conflict = SynchronizeDefaultTerminal(PrepareDefaultTerminal(publicationPrepared, LoopRunStatus.NeedsReview));
        var cancelled = SynchronizeDefaultTerminal(PrepareDefaultTerminal(AdvanceDefaultTo(DefaultConversationTurnCheckpoint.RunStarted), LoopRunStatus.Cancelled));
        var restart = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted);

        var userPreparedResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(userPublicationPrepared));
        var userPreparedEffect = Assert.Single(userPreparedResult.Payload.Effects, effect => effect.EffectId == userPublicationPrepared.UserPublicationId);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (userPreparedEffect.Phase, userPreparedEffect.Outcome, userPreparedEffect.EvidenceStatus));

        var preparedResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(publicationPrepared));
        var preparedEffect = Assert.Single(preparedResult.Payload.Effects, effect => effect.EffectId == publicationPrepared.AssistantPublicationId);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (preparedEffect.Phase, preparedEffect.Outcome, preparedEffect.EvidenceStatus));
        Assert.Contains(preparedResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.PublicationDispatchBoundaryUnavailable);

        var providerPreparedResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(providerPrepared));
        var providerPreparedEffect = Assert.Single(providerPreparedResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Provider);
        Assert.Equal((GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete), (providerPreparedEffect.Phase, providerPreparedEffect.Outcome, providerPreparedEffect.EvidenceStatus));

        var conflictResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(conflict));
        Assert.Equal(GovernedLoopRunStatus.NeedsReview, conflictResult.Payload.Lifecycle.Status);
        Assert.Contains(conflictResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ReviewDispositionUnavailable);
        Assert.Contains(conflictResult.Payload.Projections, projection => projection.Status == GovernedLoopProjectionStatus.ReconciliationRequired && projection.EffectId == conflict.AssistantPublicationId);

        var cancelledResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(cancelled));
        Assert.Equal(GovernedLoopRunStatus.Cancelled, cancelledResult.Payload.Lifecycle.Status);
        Assert.Empty(cancelledResult.Payload.Effects);

        var restartResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(restart));
        Assert.Equal(GovernedLoopRunStatus.Running, restartResult.Payload.Lifecycle.Status);
        Assert.Equal(GovernedLoopEffectPhase.DispatchBoundaryReached, Assert.Single(restartResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Provider).Phase);
    }

    [Theory]
    [InlineData(CustomLoopRunStatus.Failed, GovernedLoopRunStatus.Failed)]
    [InlineData(CustomLoopRunStatus.Cancelled, GovernedLoopRunStatus.Cancelled)]
    public void ProjectCustomLoop_maps_terminal_lifecycle_without_parsing_failure_detail(CustomLoopRunStatus sourceStatus, GovernedLoopRunStatus expected)
    {
        var run = WithCustomTerminal(CreateCustomAdmitted(), sourceStatus);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(run));

        Assert.Equal(expected, result.Payload.Lifecycle.Status);
        Assert.Equal(run.CompletedAtUtc, result.Payload.Lifecycle.TerminalAtUtc);
        Assert.Equal(sourceStatus == CustomLoopRunStatus.Failed, result.Gaps.Any(gap => gap.Code == GovernedLoopCompatibilityGapCode.CanonicalFailureUnavailable));
    }

    [Fact]
    public void ProjectCustomLoop_maps_provider_success_and_open_restart_without_claiming_dispatch_or_audit_parity()
    {
        var open = WithCustomProviderTrace(CreateCustomAdmitted(), observed: false);
        var openExit = WithCustomExitTrace(CreateCustomAdmitted());
        var succeeded = WithCustomProviderTrace(CreateCustomAdmitted(), observed: true);

        var openResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(open));
        var openEffect = Assert.Single(openResult.Payload.Effects);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (openEffect.Phase, openEffect.Outcome, openEffect.EvidenceStatus));
        Assert.Contains(openResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ProviderDispatchBoundaryUnavailable);

        var openExitResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(openExit));
        var openExitEffect = Assert.Single(openExitResult.Payload.Effects);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (openExitEffect.Phase, openExitEffect.Outcome, openExitEffect.EvidenceStatus));
        Assert.Contains(openExitResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ProviderDispatchBoundaryUnavailable);

        var successResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(succeeded));
        var successEffect = Assert.Single(successResult.Payload.Effects);
        Assert.Equal((GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded), (successEffect.Phase, successEffect.Outcome));
        Assert.Equal(GovernedLoopEffectEvidenceStatus.Incomplete, successEffect.EvidenceStatus);
        Assert.Contains(successResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
        Assert.Contains(successResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.CanonicalEffectIntentUnavailable);
        succeeded.Events[^1] = succeeded.Events[^1] with { EventId = "mutated-after-projection" };
        Assert.Equal("event-attempt-completed", successEffect.SourceEvidenceId);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopCompatibilityEffectObservation>)successResult.Payload.Effects).Add(successEffect));
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopCompatibilityGap>)successResult.Gaps).Add(successResult.Gaps[0]));
    }

    [Theory]
    [InlineData(PermissionDecision.Deny, ToolApprovalDecision.NotEvaluated)]
    [InlineData(PermissionDecision.RequiresApproval, ToolApprovalDecision.Requested)]
    public void ProjectCustomLoop_maps_denied_or_pending_approval_as_definitely_not_dispatched(PermissionDecision permission, ToolApprovalDecision approval)
    {
        var run = WithNonDispatchingToolGovernance(WithCustomProviderTrace(CreateCustomAdmitted(), observed: false), permission, approval);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(run));
        var actuator = Assert.Single(result.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Actuator);

        Assert.Equal((GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete), (actuator.Phase, actuator.Outcome, actuator.EvidenceStatus));
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
    }

    [Fact]
    public void ProjectCustomLoop_keeps_allowed_tool_governance_ambiguous_until_outcome_evidence_exists()
    {
        var run = WithNonDispatchingToolGovernance(WithCustomProviderTrace(CreateCustomAdmitted(), observed: false), PermissionDecision.Allow, ToolApprovalDecision.NotRequired);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(run));
        var actuator = Assert.Single(result.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Actuator);

        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (actuator.Phase, actuator.Outcome, actuator.EvidenceStatus));
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectCustomLoop_requires_reconciliation_for_failed_tool_outcomes(bool integrityFailed)
    {
        var run = WithToolOutcome(WithCustomProviderTrace(CreateCustomAdmitted(), observed: false), ToolExecutionOutcome.Failed, integrityFailed);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(run));
        var actuator = Assert.Single(result.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Actuator);

        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (actuator.Phase, actuator.Outcome, actuator.EvidenceStatus));
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
        Assert.Equal(integrityFailed, result.Gaps.Any(gap => gap.Code == GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable));
    }

    [Fact]
    public void ProjectCustomLoop_retains_conclusive_tool_success_when_later_integrity_evidence_fails()
    {
        var run = WithToolOutcome(WithCustomProviderTrace(CreateCustomAdmitted(), observed: false), ToolExecutionOutcome.Succeeded, integrityFailed: true);

        var result = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(run));
        var actuator = Assert.Single(result.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Actuator);

        Assert.Equal((GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Incomplete), (actuator.Phase, actuator.Outcome, actuator.EvidenceStatus));
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable);
        Assert.Contains(result.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable);
    }

    [Fact]
    public void ProjectCustomLoop_keeps_failed_attempt_and_false_publication_ambiguous_without_parsing_prose()
    {
        var failed = WithCustomProviderFailure(WithCustomProviderTrace(CreateCustomAdmitted(), observed: false));
        var publicationStarted = WithCustomPublicationStarted(WithCustomProviderTrace(CreateCustomAdmitted(), observed: true));
        var publication = WithCustomPublication(WithCustomProviderTrace(CreateCustomAdmitted(), observed: true), published: false);
        var published = WithCustomPublication(WithCustomProviderTrace(CreateCustomAdmitted(), observed: true), published: true);

        var failedResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(failed));
        var failedProvider = Assert.Single(failedResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Provider);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown), (failedProvider.Phase, failedProvider.Outcome));
        Assert.Contains(failedResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.ProviderDispatchBoundaryUnavailable);

        var publicationStartedResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(publicationStarted));
        var publicationStartedEffect = Assert.Single(publicationStartedResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Publication);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete), (publicationStartedEffect.Phase, publicationStartedEffect.Outcome, publicationStartedEffect.EvidenceStatus));
        Assert.Contains(publicationStartedResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.PublicationDispatchBoundaryUnavailable);

        var publicationResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(publication));
        var publicationEffect = Assert.Single(publicationResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Publication);
        Assert.Equal((GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown), (publicationEffect.Phase, publicationEffect.Outcome));
        Assert.Contains(publicationResult.Gaps, gap => gap.Code == GovernedLoopCompatibilityGapCode.PublicationOutcomeConflated);
        Assert.Equal(publicationResult.Payload.Effects.OrderBy(effect => effect.EffectId, StringComparer.Ordinal).ThenBy(effect => effect.SourceGeneration), publicationResult.Payload.Effects);

        var publishedResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(published));
        var publishedEffect = Assert.Single(publishedResult.Payload.Effects, effect => effect.Origin == GovernedLoopEffectOrigin.Publication);
        Assert.Equal((GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete), (publishedEffect.Phase, publishedEffect.Outcome, publishedEffect.EvidenceStatus));
    }

    [Fact]
    public void Projection_never_uses_legacy_detail_or_failure_code_as_contract_evidence()
    {
        const string Hostile = "HOSTILE_DETAIL_MUST_NOT_PROJECT";
        var source = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted);
        source = source with { Transitions = source.Transitions.Select(transition => transition with { Detail = Hostile }).ToArray() };
        var defaultResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(source));
        var customResult = Assert.IsType<GovernedLoopCompatibilityPartialResult>(GovernedLoopCompatibilityProjector.ProjectCustomLoop(WithCustomTerminal(CreateCustomAdmitted(), CustomLoopRunStatus.Failed) with { FailureCode = Hostile, FailureDetail = Hostile }));

        Assert.DoesNotContain(defaultResult.Gaps, gap => gap.Detail.Contains(Hostile, StringComparison.Ordinal));
        Assert.DoesNotContain(defaultResult.Payload.Effects, effect => EffectStrings(effect).Any(value => value.Contains(Hostile, StringComparison.Ordinal)));
        Assert.DoesNotContain(customResult.Gaps, gap => gap.Detail.Contains(Hostile, StringComparison.Ordinal));
        Assert.DoesNotContain(customResult.Payload.Effects, effect => EffectStrings(effect).Any(value => value.Contains(Hostile, StringComparison.Ordinal)));
    }

    [Fact]
    public void ProjectCustomLoop_fails_closed_at_effect_observation_limit_plus_one()
    {
        var run = CreateCustomAdmitted();
        var events = new List<CustomLoopRunEvent>(run.Events);
        var timestamp = run.UpdatedAtUtc.AddSeconds(1);
        for (var index = 0; index <= GovernedLoopCompatibilityLimits.MaxEffectObservations; index++)
        {
            var id = $"publication-{index:D4}";
            events.Add(new CustomLoopRunEvent(events.Count + 1, $"event-{id}", timestamp, CustomLoopRunEventKind.ConversationPublished, 1, null, null, "Typed false publication outcome.", [], null, null, null, null, false, id, null, null, null, null));
        }

        var oversized = run with { LifecycleVersion = run.LifecycleVersion + 1, UpdatedAtUtc = timestamp, Events = events.ToArray() };
        Assert.True(CustomLoopRunValidator.Validate(oversized).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(oversized).Errors));

        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversized), GovernedLoopCompatibilitySource.CustomLoop);
    }

    [Fact]
    public void Projectors_reject_source_history_arrays_at_authoritative_limit_plus_one()
    {
        var defaultRecord = CreateDefaultAdmitted();
        var maximumDefaultTransitions = Enum.GetValues<DefaultConversationTurnCheckpoint>()
            .Count(checkpoint => checkpoint != DefaultConversationTurnCheckpoint.Unknown);
        var oversizedDefault = defaultRecord with
        {
            Transitions = Enumerable.Repeat(defaultRecord.Transitions[0], maximumDefaultTransitions + 1).ToArray()
        };
        var customRun = CreateCustomAdmitted();
        var oversizedCustom = customRun with
        {
            Events = Enumerable.Repeat(customRun.Events[0], CustomLoopLimits.MaxTraceEventsPerRun + 1).ToArray()
        };

        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(oversizedDefault), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedCustom), GovernedLoopCompatibilitySource.CustomLoop);
    }

    [Fact]
    public void ProjectDefaultConversation_does_not_traverse_a_large_oversized_history()
    {
        var source = CreateDefaultAdmitted() with { Transitions = new NonTraversableReadOnlyList<DefaultConversationTurnTransition>(int.MaxValue) };

        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(source), GovernedLoopCompatibilitySource.DefaultConversation);
    }

    [Fact]
    public void ProjectDefaultConversation_preflights_transcript_and_metadata_adapter_safety_bounds()
    {
        var source = CreateDefaultAdmitted();
        var oversizedTranscript = source with
        {
            BaseTranscript = Enumerable.Repeat(LlmMessage.System("bounded"), GovernedLoopCompatibilityLimits.MaxDefaultTranscriptMessages + 1).ToArray()
        };
        var nonTraversableTranscript = source with
        {
            BaseTranscript = new NonTraversableReadOnlyList<LlmMessage>(int.MaxValue)
        };
        var throwingCountTranscript = source with
        {
            BaseTranscript = new ThrowingCountReadOnlyList<LlmMessage>()
        };
        var oversizedMetadata = source with
        {
            Run = source.Run with
            {
                Metadata = Enumerable.Range(0, GovernedLoopCompatibilityLimits.MaxDefaultRunMetadataEntries + 1)
                    .ToDictionary(index => $"metadata-{index:D3}", _ => "value", StringComparer.Ordinal)
            }
        };

        DefaultConversationTurnProtocolValidator.Validate(oversizedTranscript);
        DefaultConversationTurnProtocolValidator.Validate(oversizedMetadata);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(oversizedTranscript), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(nonTraversableTranscript), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(throwingCountTranscript), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(oversizedMetadata), GovernedLoopCompatibilitySource.DefaultConversation);
    }

    [Fact]
    public void ProjectCustomLoop_preflights_nested_source_context_retained_output_and_authority_arrays()
    {
        Assert.Equal(
            GovernedLoopCompatibilityLimits.MaxCustomToolAssignments,
            Enum.GetValues<CustomLoopToolAssignment>().Count(assignment => assignment != CustomLoopToolAssignment.Unknown));

        var run = CreateCustomAdmitted();
        var oversizedManifest = run with
        {
            ContextSnapshot = run.ContextSnapshot with
            {
                SourceManifest = Enumerable.Repeat(run.ContextSnapshot.SourceManifest[0], GovernedLoopCompatibilityLimits.MaxCustomContextManifestSources + 1).ToArray()
            }
        };
        var eventsWithOversizedBlocks = run.Events.ToArray();
        eventsWithOversizedBlocks[0] = eventsWithOversizedBlocks[0] with
        {
            ContextBlocks = new CustomLoopContextBlock[GovernedLoopCompatibilityLimits.MaxCustomContextBlocks + 1]
        };
        var oversizedBlocks = run with { Events = eventsWithOversizedBlocks };
        var oversizedRetainedOutputs = run with
        {
            Checkpoint = run.Checkpoint with
            {
                EarlierRetainedOutputs = new CustomLoopRetainedOutput[GovernedLoopCompatibilityLimits.MaxCustomRetainedOutputs + 1]
            }
        };
        var traced = WithCustomProviderTrace(run, observed: false);
        var eventsWithOversizedAuthority = traced.Events.ToArray();
        var attemptIndex = Array.FindIndex(eventsWithOversizedAuthority, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
        var attempt = eventsWithOversizedAuthority[attemptIndex];
        var authority = attempt.ToolAuthority!;
        eventsWithOversizedAuthority[attemptIndex] = attempt with
        {
            ToolAuthority = authority with
            {
                AdmittedMaximum = Enumerable.Repeat(CustomLoopToolAssignment.Read, GovernedLoopCompatibilityLimits.MaxCustomToolAssignments + 1).ToArray()
            }
        };
        var oversizedAuthority = traced with { Events = eventsWithOversizedAuthority };

        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedManifest), GovernedLoopCompatibilitySource.CustomLoop);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedBlocks), GovernedLoopCompatibilitySource.CustomLoop);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedRetainedOutputs), GovernedLoopCompatibilitySource.CustomLoop);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedAuthority), GovernedLoopCompatibilitySource.CustomLoop);
    }

    [Fact]
    public void Projectors_preflight_definition_and_capability_collections_before_authoritative_hashing_and_validation()
    {
        var defaultRecord = CreateDefaultAdmitted();
        var pin = defaultRecord.CapabilityAdmission.Pins[0];
        var oversizedPins = defaultRecord with
        {
            CapabilityAdmission = defaultRecord.CapabilityAdmission with
            {
                Pins = Enumerable.Repeat(pin, CapabilityContractLimits.MaxCapabilityAdmissionPins + 1).ToArray()
            }
        };
        var evidence = defaultRecord.CapabilityAdmission.Evidence[0];
        var lyingEvidence = defaultRecord with
        {
            CapabilityAdmission = defaultRecord.CapabilityAdmission with
            {
                Evidence = new LyingCountReadOnlyList<CapabilityAdmissionEvidence>(1, Enumerable.Repeat(evidence, CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries + 1))
            }
        };
        var dependency = defaultRecord.CapabilityAdmission.Requirements.Required[0];
        var oversizedDefaultRequirements = defaultRecord with
        {
            CapabilityAdmission = defaultRecord.CapabilityAdmission with
            {
                Requirements = defaultRecord.CapabilityAdmission.Requirements with
                {
                    Required = Enumerable.Repeat(dependency, CapabilityContractLimits.MaxDependencyManifestDependencies + 1).ToArray()
                }
            }
        };

        var customRun = CreateCustomAdmitted();
        var oversizedSteps = customRun with
        {
            AdmittedDefinition = customRun.AdmittedDefinition with
            {
                InferenceSteps = Enumerable.Repeat(customRun.AdmittedDefinition.InferenceSteps[0], CustomLoopLimits.MaxInferenceSteps + 1).ToArray()
            }
        };
        var customDependency = customRun.AdmittedDefinition.CapabilityRequirements.Required[0];
        var oversizedCustomRequirements = customRun with
        {
            AdmittedDefinition = customRun.AdmittedDefinition with
            {
                CapabilityRequirements = customRun.AdmittedDefinition.CapabilityRequirements with
                {
                    Required = Enumerable.Repeat(customDependency, CapabilityContractLimits.MaxDependencyManifestDependencies + 1).ToArray()
                }
            }
        };

        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(oversizedPins), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(lyingEvidence), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(oversizedDefaultRequirements), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedSteps), GovernedLoopCompatibilitySource.CustomLoop);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(oversizedCustomRequirements), GovernedLoopCompatibilitySource.CustomLoop);
    }

    [Fact]
    public void Projectors_reject_absent_or_malformed_sources_with_one_static_gap()
    {
        var malformedDefault = CreateDefaultAdmitted() with { RequestId = " forged " };
        var malformedCustom = CreateCustomAdmitted() with { LifecycleVersion = 0 };

        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(null), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(malformedDefault), GovernedLoopCompatibilitySource.DefaultConversation, GovernedLoopCompatibilityGapCode.SourceValidationFailed);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(CreateDefaultAdmitted() with { Transitions = [] }), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectDefaultConversation(CreateDefaultAdmitted() with { Transitions = null! }), GovernedLoopCompatibilitySource.DefaultConversation);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(null), GovernedLoopCompatibilitySource.CustomLoop);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(malformedCustom), GovernedLoopCompatibilitySource.CustomLoop, GovernedLoopCompatibilityGapCode.SourceValidationFailed);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(CreateCustomAdmitted() with { Events = [] }), GovernedLoopCompatibilitySource.CustomLoop);
        AssertUnsupported(GovernedLoopCompatibilityProjector.ProjectCustomLoop(CreateCustomAdmitted() with { Events = null! }), GovernedLoopCompatibilitySource.CustomLoop);
    }

    private static void AssertUnsupported(
        GovernedLoopCompatibilityProjectionResult result,
        GovernedLoopCompatibilitySource source,
        GovernedLoopCompatibilityGapCode expectedCause = GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded)
    {
        var unsupported = Assert.IsType<GovernedLoopCompatibilityUnsupportedResult>(result);
        Assert.Equal(GovernedLoopCompatibilityProjectionStatus.Unsupported, unsupported.Status);
        Assert.Equal(source, unsupported.Source);
        var gap = Assert.Single(unsupported.Gaps);
        Assert.Equal(expectedCause, gap.Code);
        Assert.DoesNotContain("forged", gap.Detail, StringComparison.OrdinalIgnoreCase);
        if (expectedCause == GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded)
        {
            Assert.DoesNotContain("failed its authoritative public validator", gap.Detail, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("failed its authoritative public validator", gap.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DefaultConversationTurnRecord CreateDefaultAdmitted()
    {
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, _startedAtUtc);
        var conversation = new ConversationMemorySnapshot("current", new string('0', 64), [LlmMessage.System("system context")]);
        var admittedAtUtc = _startedAtUtc.AddSeconds(1);
        return DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), admittedAtUtc, RequestId, TestCapabilityAdmissionFactory.Create(LoopDefinition.CreateDefaultConversation().CapabilityRequirements, admittedAtUtc));
    }

    private static DefaultConversationTurnRecord AdvanceDefaultTo(DefaultConversationTurnCheckpoint target)
    {
        var record = CreateDefaultAdmitted();
        foreach (var checkpoint in new[]
        {
            DefaultConversationTurnCheckpoint.RunStarted,
            DefaultConversationTurnCheckpoint.UserMessageAccepted,
            DefaultConversationTurnCheckpoint.UserPublicationPrepared,
            DefaultConversationTurnCheckpoint.UserPublished,
            DefaultConversationTurnCheckpoint.ProviderDispatchPrepared,
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted,
            DefaultConversationTurnCheckpoint.ProviderOutcomeObserved,
            DefaultConversationTurnCheckpoint.AssistantPublicationPrepared,
            DefaultConversationTurnCheckpoint.AssistantPublished,
            DefaultConversationTurnCheckpoint.TranscriptSynchronized
        }.TakeWhile(checkpoint => checkpoint <= target))
        {
            record = checkpoint switch
            {
                DefaultConversationTurnCheckpoint.ProviderDispatchStarted => record.Advance(checkpoint, NextTime(record), checkpoint.ToString(), providerOutcome: DefaultConversationProviderOutcome.OutcomeUnknown),
                DefaultConversationTurnCheckpoint.ProviderOutcomeObserved => record.Advance(checkpoint, NextTime(record), checkpoint.ToString(), providerOutcome: DefaultConversationProviderOutcome.Observed, assistantMessage: new DefaultConversationTurnMessage(record.TurnId + ":message:assistant", LlmMessageRole.Assistant, "answer"), providerResponseId: "response-1"),
                _ => record.Advance(checkpoint, NextTime(record), checkpoint.ToString())
            };
        }

        return record;
    }

    private static DefaultConversationTurnRecord CreateDefaultObservedFailure()
    {
        var record = AdvanceDefaultTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted);
        return record.Advance(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved, NextTime(record), "Provider failure observed.", providerOutcome: DefaultConversationProviderOutcome.ObservedFailure, providerResponseId: "response-failed");
    }

    private static DefaultConversationTurnRecord PrepareDefaultTerminal(DefaultConversationTurnRecord record, LoopRunStatus status)
    {
        var occurredAtUtc = NextTime(record);
        const string Detail = "terminal detail";
        var run = status switch
        {
            LoopRunStatus.Completed => record.Run.Complete(occurredAtUtc),
            LoopRunStatus.Failed => record.Run.Fail(occurredAtUtc, Detail),
            LoopRunStatus.Cancelled => record.Run.Cancel(occurredAtUtc, Detail),
            LoopRunStatus.NeedsReview => record.Run.NeedsReview(occurredAtUtc, Detail),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
        return record.Advance(DefaultConversationTurnCheckpoint.TerminalPrepared, occurredAtUtc, "Terminal prepared.", run: run, reviewDetail: status == LoopRunStatus.NeedsReview ? Detail : null);
    }

    private static DefaultConversationTurnRecord SynchronizeDefaultTerminal(DefaultConversationTurnRecord record)
    {
        return record.Advance(DefaultConversationTurnCheckpoint.Terminal, NextTime(record), "Terminal synchronized.", runProjectionSynchronized: true);
    }

    private static CustomLoopRunRecord CreateCustomAdmitted()
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-compatibility", "role-workspace", "step-only", "create-loop", _startedAtUtc);
        var definition = CustomLoopDefinitionContentHash.Apply(seed with { ContentHash = string.Empty });
        var admitted = new CustomLoopRunEvent(1, "event-admitted", _startedAtUtc, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var audit = new CustomLoopRunEvent(2, "event-admission-audit", _startedAtUtc, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-compatibility",
            definition.Id,
            2,
            CustomLoopRunStatus.Admitted,
            _startedAtUtc,
            _startedAtUtc,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "invoke-compatibility",
            "test-user",
            string.Empty,
            definition,
            "Initial prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(_startedAtUtc),
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted, audit],
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _startedAtUtc)
        };
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        return run;
    }

    private static CustomLoopRunRecord WithCustomTerminal(CustomLoopRunRecord run, CustomLoopRunStatus status)
    {
        var now = run.UpdatedAtUtc.AddSeconds(1);
        var lifecycle = new CustomLoopRunEvent(run.Events.Length + 1, "event-terminal", now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Typed lifecycle terminal evidence.", [], null, null, null, null, null, null, null, null, null, null);
        var terminal = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = now,
            CompletedAtUtc = now,
            Events = [.. run.Events, lifecycle],
            FailureCode = status == CustomLoopRunStatus.Failed ? "opaque_failure" : null,
            FailureDetail = status == CustomLoopRunStatus.Failed ? "Detail intentionally ignored by the mapper." : null
        };
        Assert.True(CustomLoopRunValidator.Validate(terminal).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(terminal).Errors));
        return terminal;
    }

    private static CustomLoopRunRecord WithCustomProviderTrace(CustomLoopRunRecord run, bool observed)
    {
        var runningAt = run.UpdatedAtUtc.AddSeconds(1);
        var attemptAt = runningAt.AddSeconds(1);
        var observedAt = attemptAt.AddSeconds(1);
        var authority = ToolAuthority(run.AdmittedDefinition.RoleId, attemptAt);
        var events = new List<CustomLoopRunEvent>(run.Events)
        {
            new(run.Events.Length + 1, "event-running", runningAt, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered running.", [], null, null, null, null, null, null, null, null, null, null),
            new(run.Events.Length + 2, "event-iteration", runningAt, CustomLoopRunEventKind.IterationStarted, 1, null, null, "Iteration started.", [], null, null, null, null, null, null, null, null, null, null),
            new(run.Events.Length + 3, "event-attempt-started", attemptAt, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-only", 1, "Attempt started.", [], null, null, null, null, null, null, "provider", "model", "attempt-correlation", null, authority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes)
        };
        if (observed)
        {
            events.Add(new CustomLoopRunEvent(events.Count + 1, "event-outcome", observedAt, CustomLoopRunEventKind.NodeOutcomeObserved, 1, "step-only", 1, "Outcome observed.", [], "answer", 6, false, false, false, null, "provider", "model", "response-1", null));
            events.Add(new CustomLoopRunEvent(events.Count + 1, "event-attempt-completed", observedAt, CustomLoopRunEventKind.NodeAttemptCompleted, 1, "step-only", 1, "Attempt completed.", [], "answer", 6, false, false, false, null, "provider", "model", "response-1", null));
        }

        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + (observed ? 3 : 2),
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = observed ? observedAt : attemptAt,
            ExecutionClock = new CustomLoopExecutionClock(0, runningAt),
            Events = events.ToArray()
        };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopRunRecord WithCustomExitTrace(CustomLoopRunRecord run)
    {
        var runningAt = run.UpdatedAtUtc.AddSeconds(1);
        var attemptAt = runningAt.AddSeconds(1);
        var authority = ToolAuthority(run.AdmittedDefinition.RoleId, attemptAt);
        CustomLoopRunEvent[] events =
        [
            .. run.Events,
            new(run.Events.Length + 1, "event-running", runningAt, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered running.", [], null, null, null, null, null, null, null, null, null, null),
            new(run.Events.Length + 2, "event-iteration", runningAt, CustomLoopRunEventKind.IterationStarted, 1, null, null, "Iteration started.", [], null, null, null, null, null, null, null, null, null, null),
            new(run.Events.Length + 3, "event-exit-started", attemptAt, CustomLoopRunEventKind.ExitDecisionStarted, 1, null, 1, "Exit decision started.", [], null, null, null, null, null, null, "provider", "model", "exit-correlation", null, authority, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes)
        ];
        var candidate = run with
        {
            LifecycleVersion = run.LifecycleVersion + 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = attemptAt,
            ExecutionClock = new CustomLoopExecutionClock(0, runningAt),
            Events = events
        };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopRunRecord WithNonDispatchingToolGovernance(CustomLoopRunRecord run, PermissionDecision permission, ToolApprovalDecision approval)
    {
        var start = run.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
        var time = run.UpdatedAtUtc.AddSeconds(1);
        var authority = ToolAuthority(run.AdmittedDefinition.RoleId, start.TimestampUtc);
        var reservation = new CustomLoopToolTraceEvidence(CustomLoopToolEvidencePhase.RequestReserved, 1, "tool-request-1", null, ToolCommand.Read, "shared/file.txt", null, null, null, authority, null, null, null, null, null, false, CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
        var governance = new ToolGovernanceEvidence(ToolAuthorityDecision.Allowed, "Authority allowed the request.", permission, "shared/file.txt", "Permission did not admit dispatch.", null, approval, null, null);
        var decided = reservation with { Phase = CustomLoopToolEvidencePhase.GovernanceDecided, BrokerRequestId = "broker-request-1", Governance = governance };
        var reservedEvent = new CustomLoopRunEvent(run.Events.Length + 1, "event-tool-reserved", time, CustomLoopRunEventKind.ToolRequestReserved, 1, "step-only", 1, "Tool request reserved.", [], null, null, null, null, null, null, null, null, null, null, authority, reservation);
        var decidedEvent = new CustomLoopRunEvent(run.Events.Length + 2, "event-tool-governed", time, CustomLoopRunEventKind.ToolGovernanceDecided, 1, "step-only", 1, "Tool governance decided.", [], null, null, null, null, null, null, null, null, null, null, authority, decided);
        var candidate = run with { LifecycleVersion = run.LifecycleVersion + 2, UpdatedAtUtc = time, Events = [.. run.Events, reservedEvent, decidedEvent] };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopRunRecord WithToolOutcome(CustomLoopRunRecord run, ToolExecutionOutcome outcome, bool integrityFailed)
    {
        var start = run.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
        var time = run.UpdatedAtUtc.AddSeconds(1);
        var authority = ToolAuthority(run.AdmittedDefinition.RoleId, start.TimestampUtc);
        var reservation = new CustomLoopToolTraceEvidence(CustomLoopToolEvidencePhase.RequestReserved, 1, "tool-request-1", null, ToolCommand.Read, "shared/file.txt", null, null, "workspace/shared/file.txt", authority, null, null, null, null, null, false, CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
        var governance = new ToolGovernanceEvidence(ToolAuthorityDecision.Allowed, "Authority allowed the request.", PermissionDecision.Allow, "shared/file.txt", "Permission admitted dispatch.", null, ToolApprovalDecision.NotRequired, null, null);
        var decided = reservation with { Phase = CustomLoopToolEvidencePhase.GovernanceDecided, BrokerRequestId = "broker-request-1", Governance = governance };
        var canonicalResult = outcome == ToolExecutionOutcome.Succeeded ? "tool succeeded" : "tool failed";
        var observed = decided with
        {
            Phase = CustomLoopToolEvidencePhase.OutcomeObserved,
            Outcome = outcome,
            CanonicalResultReturnedToModel = canonicalResult,
            CanonicalResultHash = CustomLoopTraceContentHash.Compute(canonicalResult),
            CanonicalResultCharacterCount = canonicalResult.Length
        };
        var events = new List<CustomLoopRunEvent>(run.Events)
        {
            ToolEvent(run.Events.Length + 1, "event-tool-reserved", time, CustomLoopRunEventKind.ToolRequestReserved, authority, reservation),
            ToolEvent(run.Events.Length + 2, "event-tool-governed", time, CustomLoopRunEventKind.ToolGovernanceDecided, authority, decided),
            ToolEvent(run.Events.Length + 3, "event-tool-outcome", time, CustomLoopRunEventKind.ToolOutcomeObserved, authority, observed)
        };
        if (integrityFailed)
        {
            events.Add(ToolEvent(run.Events.Length + 4, "event-tool-integrity", time, CustomLoopRunEventKind.ToolIntegrityFailed, authority, observed with { Phase = CustomLoopToolEvidencePhase.IntegrityFailed }));
        }

        var candidate = run with { LifecycleVersion = run.LifecycleVersion + events.Count - run.Events.Length, UpdatedAtUtc = time, Events = events.ToArray() };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopRunEvent ToolEvent(long sequence, string eventId, DateTimeOffset timestampUtc, CustomLoopRunEventKind kind, CustomLoopToolAuthoritySnapshot authority, CustomLoopToolTraceEvidence evidence)
    {
        return new CustomLoopRunEvent(sequence, eventId, timestampUtc, kind, 1, "step-only", 1, "Typed tool protocol evidence.", [], null, null, null, null, null, null, null, null, null, null, authority, evidence);
    }

    private static CustomLoopRunRecord WithCustomProviderFailure(CustomLoopRunRecord run)
    {
        var time = run.UpdatedAtUtc.AddSeconds(1);
        var failure = new CustomLoopRunEvent(run.Events.Length + 1, "event-attempt-failed", time, CustomLoopRunEventKind.NodeAttemptFailed, 1, "step-only", 1, "Opaque failure prose that the mapper must ignore.", [], null, null, null, null, null, null, "provider", "model", "attempt-correlation", null);
        var candidate = run with { LifecycleVersion = run.LifecycleVersion + 1, UpdatedAtUtc = time, Events = [.. run.Events, failure] };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopRunRecord WithCustomPublication(CustomLoopRunRecord run, bool published)
    {
        var time = run.UpdatedAtUtc.AddSeconds(1);
        const string PublicationId = "publication-step-only";
        var started = new CustomLoopRunEvent(run.Events.Length + 1, "event-publication-started", time, CustomLoopRunEventKind.ConversationPublicationStarted, 1, "step-only", null, "Publication intent retained.", [], null, null, null, null, null, PublicationId, null, null, null, null);
        var completed = new CustomLoopRunEvent(run.Events.Length + 2, "event-publication-completed", time, CustomLoopRunEventKind.ConversationPublished, 1, "step-only", null, "Typed publication outcome retained.", [], published ? "answer" : null, published ? 6 : null, published ? false : null, null, published, PublicationId, null, null, null, null);
        var candidate = run with { LifecycleVersion = run.LifecycleVersion + 1, UpdatedAtUtc = time, Events = [.. run.Events, started, completed] };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopRunRecord WithCustomPublicationStarted(CustomLoopRunRecord run)
    {
        var time = run.UpdatedAtUtc.AddSeconds(1);
        const string PublicationId = "publication-step-only";
        var started = new CustomLoopRunEvent(run.Events.Length + 1, "event-publication-started", time, CustomLoopRunEventKind.ConversationPublicationStarted, 1, "step-only", null, "Publication intent retained.", [], null, null, null, null, null, PublicationId, null, null, null, null);
        var candidate = run with { LifecycleVersion = run.LifecycleVersion + 1, UpdatedAtUtc = time, Events = [.. run.Events, started] };
        Assert.True(CustomLoopRunValidator.Validate(candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(candidate).Errors));
        return candidate;
    }

    private static CustomLoopToolAuthoritySnapshot ToolAuthority(string roleId, DateTimeOffset evaluatedAtUtc)
    {
        var assignments = new[] { CustomLoopToolAssignment.Read };
        var hash = CustomLoopTraceContentHash.Compute("read");
        return new CustomLoopToolAuthoritySnapshot(roleId, assignments, assignments, assignments, assignments, hash, hash, evaluatedAtUtc, true, "Read-only test authority.");
    }

    private static IEnumerable<string> EffectStrings(GovernedLoopCompatibilityEffectObservation effect)
    {
        return new[] { effect.EffectId, effect.OperationId, effect.SourceEvidenceId, effect.SourceReconciliationEvidenceId }.OfType<string>();
    }

    private sealed class NonTraversableReadOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count { get; } = count;

        public T this[int index] => throw new InvalidOperationException("Oversized collection must be rejected from its count without indexing.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Oversized collection must be rejected from its count without enumeration.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LyingCountReadOnlyList<T>(int count, IEnumerable<T> values) : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _values = values.ToArray();

        public int Count { get; } = count;

        public T this[int index] => _values[index];

        public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingCountReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("A hostile count getter must fail closed.");

        public T this[int index] => throw new InvalidOperationException("A hostile collection must not be indexed.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("A hostile collection must not be enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static DateTimeOffset NextTime(DefaultConversationTurnRecord record) => _startedAtUtc.AddSeconds(record.LifecycleVersion + 1);
}
