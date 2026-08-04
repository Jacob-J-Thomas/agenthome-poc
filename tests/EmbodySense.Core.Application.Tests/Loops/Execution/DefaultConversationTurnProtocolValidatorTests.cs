using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime;

namespace EmbodySense.Core.Application.Tests.Loops.Execution;

public sealed class DefaultConversationTurnProtocolValidatorTests
{
    private const string RequestId = "request-protocol-validator";
    private static readonly DateTimeOffset _startedAtUtc = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validate_accepts_canonical_operational_and_terminal_paths()
    {
        var record = CreateAdmitted();
        DefaultConversationTurnProtocolValidator.Validate(record);

        foreach (var checkpoint in OperationalCheckpoints())
        {
            record = AdvanceOperational(record, checkpoint);
            DefaultConversationTurnProtocolValidator.Validate(record);
        }

        record = PrepareTerminal(record, LoopRunStatus.Completed);
        DefaultConversationTurnProtocolValidator.Validate(record);
        record = SynchronizeTerminal(record);
        DefaultConversationTurnProtocolValidator.Validate(record);
    }

    [Fact]
    public void Validate_accepts_conclusive_provider_failure_without_assistant_publication()
    {
        var record = CreateObservedFailure();
        DefaultConversationTurnProtocolValidator.Validate(record);

        record = PrepareTerminal(record, LoopRunStatus.Failed);
        DefaultConversationTurnProtocolValidator.Validate(record);
        DefaultConversationTurnProtocolValidator.Validate(SynchronizeTerminal(record));
    }

    [Fact]
    public void Validate_accepts_observed_success_with_failed_audit_only_as_needs_review_before_publication()
    {
        var observed = AdvanceTo(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved);
        var auditFailure = observed with { ProviderOutcome = DefaultConversationProviderOutcome.ObservedWithAuditFailure };
        DefaultConversationTurnProtocolValidator.Validate(auditFailure);

        var needsReview = PrepareTerminal(auditFailure, LoopRunStatus.NeedsReview);
        DefaultConversationTurnProtocolValidator.Validate(needsReview);
        DefaultConversationTurnProtocolValidator.Validate(SynchronizeTerminal(needsReview));

        var publication = auditFailure.Advance(DefaultConversationTurnCheckpoint.AssistantPublicationPrepared, NextTime(auditFailure), "Forged publication.");
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(publication));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(PrepareTerminal(auditFailure, LoopRunStatus.Completed)));
    }

    [Fact]
    public void Validate_rejects_provider_outcome_evidence_that_disagrees_with_terminal_status()
    {
        var observedFailure = CreateObservedFailure();
        var assistantMessage = new DefaultConversationTurnMessage(observedFailure.TurnId + ":message:assistant", LlmMessageRole.Assistant, "forged answer");
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(observedFailure with { AssistantMessage = assistantMessage }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(observedFailure with { ProviderOutcome = DefaultConversationProviderOutcome.Observed }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(observedFailure with { ProviderResponseId = " " }));

        var observedSuccess = AdvanceTo(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved);
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(observedSuccess with { ProviderOutcome = DefaultConversationProviderOutcome.ObservedFailure }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(PrepareTerminal(observedSuccess, LoopRunStatus.Failed)));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(PrepareTerminal(observedFailure, LoopRunStatus.Completed)));
    }

    [Fact]
    public void Validate_accepts_every_recovery_terminal_shortcut_and_review_resolution()
    {
        foreach (var checkpoint in new[]
        {
            DefaultConversationTurnCheckpoint.Admitted,
            DefaultConversationTurnCheckpoint.RunStarted,
            DefaultConversationTurnCheckpoint.UserPublicationPrepared,
            DefaultConversationTurnCheckpoint.UserPublished,
            DefaultConversationTurnCheckpoint.ProviderDispatchPrepared
        })
        {
            var record = AdvanceTo(checkpoint);
            record = PrepareTerminal(record, LoopRunStatus.Failed);
            DefaultConversationTurnProtocolValidator.Validate(record);
            DefaultConversationTurnProtocolValidator.Validate(SynchronizeTerminal(record));
        }

        var cancelled = PrepareTerminal(AdvanceTo(DefaultConversationTurnCheckpoint.RunStarted), LoopRunStatus.Cancelled);
        DefaultConversationTurnProtocolValidator.Validate(SynchronizeTerminal(cancelled));

        foreach (var checkpoint in new[]
        {
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted,
            DefaultConversationTurnCheckpoint.AssistantPublicationPrepared,
            DefaultConversationTurnCheckpoint.AssistantPublished
        })
        {
            var record = PrepareTerminal(AdvanceTo(checkpoint), LoopRunStatus.NeedsReview);
            record = SynchronizeTerminal(record);
            DefaultConversationTurnProtocolValidator.Validate(record);
            DefaultConversationTurnProtocolValidator.Validate(record.ResolveReview(NextTime(record)));
        }
    }

    [Fact]
    public void Validate_rejects_each_forged_stable_identity()
    {
        var admitted = CreateAdmitted();
        var mutations = new (string Name, Func<DefaultConversationTurnRecord, DefaultConversationTurnRecord> Apply)[]
        {
            ("turn", record => record with { TurnId = "turn-forged" }),
            ("run", record => record with { Run = record.Run with { RunId = "run-forged" } }),
            ("run-role", record => record with { Run = record.Run with { RoleId = "forged-role" } }),
            ("user-message", record => record with { UserMessage = record.UserMessage with { MessageId = "message-forged" } }),
            ("provider-attempt", record => record with { ProviderAttemptId = "attempt-forged" }),
            ("provider-correlation", record => record with { ProviderCorrelationId = "correlation-forged" }),
            ("user-publication", record => record with { UserPublicationId = "publication-user-forged" }),
            ("assistant-publication", record => record with { AssistantPublicationId = "publication-assistant-forged" })
        };

        foreach (var mutation in mutations)
        {
            var exception = Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(mutation.Apply(admitted)));
            Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        var observed = AdvanceTo(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved);
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(observed with { AssistantMessage = observed.AssistantMessage! with { MessageId = "assistant-forged" } }));

        var resolved = SynchronizeTerminal(PrepareTerminal(AdvanceTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted), LoopRunStatus.NeedsReview)).ResolveReview(_startedAtUtc.AddMinutes(1));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(resolved with { ReviewResolution = resolved.ReviewResolution! with { ResolutionId = "review-forged" } }));
    }

    [Fact]
    public void Validate_rejects_forged_transition_structure_and_cross_linked_evidence()
    {
        var admitted = CreateAdmitted();
        var transition = admitted.Transitions[0];
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(admitted with { Transitions = [transition with { TransitionId = "transition-forged" }] }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(admitted with { Transitions = [transition with { Sequence = 2 }] }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(admitted with { ProviderOutcome = DefaultConversationProviderOutcome.OutcomeUnknown }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(admitted with { Run = admitted.Run.Fail(NextTime(admitted), "forged failure") }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(admitted with { ConversationVersion = new string('A', 64) }));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(admitted with { Transitions = [transition with { OccurredAtUtc = _startedAtUtc.AddSeconds(-1) }] }));

        var skipped = admitted with
        {
            LifecycleVersion = 2,
            Checkpoint = DefaultConversationTurnCheckpoint.ProviderDispatchStarted,
            ProviderOutcome = DefaultConversationProviderOutcome.OutcomeUnknown,
            Transitions =
            [
                transition,
                new DefaultConversationTurnTransition(2, admitted.TurnId + ":2:providerdispatchstarted", DefaultConversationTurnCheckpoint.ProviderDispatchStarted, NextTime(admitted), "Forged skipped dispatch.")
            ]
        };
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(skipped));

        var completed = SynchronizeTerminal(PrepareTerminal(AdvanceTo(DefaultConversationTurnCheckpoint.TranscriptSynchronized), LoopRunStatus.Completed));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(completed with { Run = completed.Run with { CompletedAtUtc = completed.Transitions[0].OccurredAtUtc.AddSeconds(-1) } }));

        var resolved = SynchronizeTerminal(PrepareTerminal(AdvanceTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted), LoopRunStatus.NeedsReview)).ResolveReview(_startedAtUtc.AddMinutes(1));
        Assert.Throws<FormatException>(() => DefaultConversationTurnProtocolValidator.Validate(resolved with { ReviewResolution = resolved.ReviewResolution! with { Detail = "forged resolution" } }));
    }

    [Fact]
    public void Advance_rejects_skipped_or_backward_state_machine_edges()
    {
        var admitted = CreateAdmitted();

        Assert.Throws<ArgumentOutOfRangeException>(() => admitted.Advance(DefaultConversationTurnCheckpoint.ProviderDispatchStarted, NextTime(admitted), "Skipped checkpoints."));
        var started = AdvanceOperational(admitted, DefaultConversationTurnCheckpoint.RunStarted);
        Assert.Throws<ArgumentOutOfRangeException>(() => started.Advance(DefaultConversationTurnCheckpoint.Admitted, NextTime(started), "Backward checkpoint."));
    }

    private static DefaultConversationTurnRecord CreateAdmitted()
    {
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, _startedAtUtc);
        var conversation = new ConversationMemorySnapshot("current", new string('0', 64), [LlmMessage.System("system context")]);
        return DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), _startedAtUtc.AddSeconds(1), RequestId);
    }

    private static DefaultConversationTurnRecord AdvanceTo(DefaultConversationTurnCheckpoint target)
    {
        var record = CreateAdmitted();
        foreach (var checkpoint in OperationalCheckpoints().TakeWhile(checkpoint => checkpoint <= target))
        {
            record = AdvanceOperational(record, checkpoint);
        }

        return record;
    }

    private static DefaultConversationTurnRecord AdvanceOperational(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint checkpoint)
    {
        return checkpoint switch
        {
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted => record.Advance(checkpoint, NextTime(record), checkpoint.ToString(), providerOutcome: DefaultConversationProviderOutcome.OutcomeUnknown),
            DefaultConversationTurnCheckpoint.ProviderOutcomeObserved => record.Advance(checkpoint, NextTime(record), checkpoint.ToString(), providerOutcome: DefaultConversationProviderOutcome.Observed, assistantMessage: new DefaultConversationTurnMessage(record.TurnId + ":message:assistant", LlmMessageRole.Assistant, "answer"), providerResponseId: "response-1"),
            _ => record.Advance(checkpoint, NextTime(record), checkpoint.ToString())
        };
    }

    private static DefaultConversationTurnRecord CreateObservedFailure()
    {
        var record = AdvanceTo(DefaultConversationTurnCheckpoint.ProviderDispatchStarted);
        return record.Advance(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved, NextTime(record), "Provider failure observed.", providerOutcome: DefaultConversationProviderOutcome.ObservedFailure, providerResponseId: "response-failed");
    }

    private static DefaultConversationTurnRecord PrepareTerminal(DefaultConversationTurnRecord record, LoopRunStatus status)
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

    private static DefaultConversationTurnRecord SynchronizeTerminal(DefaultConversationTurnRecord record)
    {
        return record.Advance(DefaultConversationTurnCheckpoint.Terminal, NextTime(record), "Terminal synchronized.", runProjectionSynchronized: true);
    }

    private static DateTimeOffset NextTime(DefaultConversationTurnRecord record) => _startedAtUtc.AddSeconds(record.LifecycleVersion + 1);

    private static IReadOnlyList<DefaultConversationTurnCheckpoint> OperationalCheckpoints()
    {
        return
        [
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
        ];
    }
}
