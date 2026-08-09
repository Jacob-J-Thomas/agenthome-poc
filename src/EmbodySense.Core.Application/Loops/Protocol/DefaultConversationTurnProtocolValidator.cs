using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Loops.Protocol;

/// <summary>
/// Validates the closed version-1 default-conversation protocol independently of its persistence adapter.
/// </summary>
public static class DefaultConversationTurnProtocolValidator
{
    private static readonly LoopDefinition _canonicalDefinition = LoopDefinition.CreateDefaultConversation();

    /// <summary>
    /// Rejects artifacts whose stable identities, transition path, evidence, timestamps, or run projection are not canonical.
    /// </summary>
    public static void Validate(DefaultConversationTurnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Require(record.SchemaVersion == DefaultConversationTurnRecord.CurrentSchemaVersion, $"Unsupported default-conversation turn schema version `{record.SchemaVersion}`.");
        Require(!string.IsNullOrWhiteSpace(record.RequestId) && record.RequestId.Length <= 256 && string.Equals(record.RequestId, record.RequestId.Trim(), StringComparison.Ordinal), "Default-conversation request identity was invalid or noncanonical.");
        Require(string.Equals(record.TurnId, DefaultConversationTurnProtocol.CreateTurnId(record.RequestId), StringComparison.Ordinal), "Default-conversation turn identity must be derived from its exact caller request identity.");
        Require(record.Run is not null, "Default-conversation turn run evidence was missing.");
        ValidateRun(record);
        ValidateCapabilityAdmission(record);
        ValidateConversation(record);
        ValidateStableIdentities(record);
        ValidateTransitions(record);
        ValidateProviderEvidence(record);
        ValidateTerminalEvidence(record);
    }

    private static void ValidateCapabilityAdmission(DefaultConversationTurnRecord record)
    {
        var error = CapabilityAdmissionSnapshotValidator.Validate(record.CapabilityAdmission);
        Require(error is null, error ?? "Default-conversation capability admission evidence was invalid.");
        _ = CapabilityDependencyManifestHash.TryCompute(_canonicalDefinition.CapabilityRequirements, out var expected, out _);
        Require(string.Equals(record.CapabilityAdmission.RequirementsHash, expected!.Value, StringComparison.Ordinal), "Default-conversation capability evidence must bind the canonical system requirements.");
    }

    private static void ValidateRun(DefaultConversationTurnRecord record)
    {
        var run = record.Run;
        Require(run.SchemaVersion == LoopRunRecord.CurrentSchemaVersion, $"Unsupported loop run schema version `{run.SchemaVersion}`.");
        Require(string.Equals(run.RunId, DefaultConversationTurnProtocol.CreateRunId(record.RequestId), StringComparison.Ordinal), "Default-conversation run identity must be derived from its exact caller request identity.");
        Require(string.Equals(run.LoopId, _canonicalDefinition.Id, StringComparison.Ordinal), "Default-conversation turn evidence must belong to the built-in default-conversation loop.");
        Require(string.Equals(run.RoleId, _canonicalDefinition.RoleId, StringComparison.Ordinal), "Default-conversation role identity was invalid or noncanonical.");
        Require(Enum.IsDefined(run.Status) && run.Status != LoopRunStatus.Unknown, "Default-conversation run status was invalid.");
        Require(Enum.IsDefined(run.Trigger) && run.Trigger == _canonicalDefinition.Trigger, "Default-conversation turns require the human-message trigger.");
        Require(IsUtc(run.StartedAtUtc), "Default-conversation run start time must be a nondefault UTC timestamp.");
        Require(IsCanonicalRuntimeSurface(run.Surface), "Default-conversation runtime surface must be canonical.");
        Require(run.Metadata is not null && run.Metadata.Keys.All(key => !string.IsNullOrWhiteSpace(key)) && run.Metadata.Values.All(value => value is not null), "Default-conversation run metadata was invalid.");
    }

    private static void ValidateConversation(DefaultConversationTurnRecord record)
    {
        Require(string.Equals(record.ConversationId, "current", StringComparison.Ordinal), "Persisted default-conversation turns must bind the canonical current conversation.");
        Require(IsLowerHex(record.ConversationVersion, 64), "Default-conversation version identity must be a canonical lowercase SHA-256 value.");
        var baseTranscript = record.BaseTranscript ?? throw Invalid("Default-conversation base transcript was missing.");
        foreach (var message in baseTranscript)
        {
            Require(message is not null && Enum.IsDefined(message.Role) && message.Role != LlmMessageRole.Unknown && !string.IsNullOrWhiteSpace(message.Content), "Default-conversation base transcript contained an invalid message.");
        }

        Require(record.UserMessage is not null && record.UserMessage.Role == LlmMessageRole.User && !string.IsNullOrWhiteSpace(record.UserMessage.Content), "Default-conversation user message evidence was invalid.");
    }

    private static void ValidateStableIdentities(DefaultConversationTurnRecord record)
    {
        Require(string.Equals(record.UserMessage.MessageId, DefaultConversationTurnProtocol.CreateUserMessageId(record.TurnId), StringComparison.Ordinal), "Default-conversation user message identity was noncanonical.");
        Require(string.Equals(record.ProviderAttemptId, DefaultConversationTurnProtocol.CreateProviderAttemptId(record.TurnId), StringComparison.Ordinal), "Default-conversation provider attempt identity was noncanonical.");
        Require(string.Equals(record.ProviderCorrelationId, DefaultConversationTurnProtocol.CreateProviderCorrelationId(record.TurnId), StringComparison.Ordinal), "Default-conversation provider correlation identity was noncanonical.");
        Require(string.Equals(record.UserPublicationId, DefaultConversationTurnProtocol.CreateUserPublicationId(record.TurnId), StringComparison.Ordinal), "Default-conversation user publication identity was noncanonical.");
        Require(string.Equals(record.AssistantPublicationId, DefaultConversationTurnProtocol.CreateAssistantPublicationId(record.TurnId), StringComparison.Ordinal), "Default-conversation assistant publication identity was noncanonical.");
        if (record.AssistantMessage is not null)
        {
            Require(string.Equals(record.AssistantMessage.MessageId, DefaultConversationTurnProtocol.CreateAssistantMessageId(record.TurnId), StringComparison.Ordinal), "Default-conversation assistant message identity was noncanonical.");
            Require(record.AssistantMessage.Role == LlmMessageRole.Assistant && !string.IsNullOrWhiteSpace(record.AssistantMessage.Content), "Default-conversation assistant message evidence was invalid.");
        }
    }

    private static void ValidateTransitions(DefaultConversationTurnRecord record)
    {
        Require(Enum.IsDefined(record.Checkpoint) && record.Checkpoint != DefaultConversationTurnCheckpoint.Unknown, "Default-conversation checkpoint was invalid.");
        var transitions = record.Transitions ?? throw Invalid("Default-conversation transition history was missing.");
        Require(record.LifecycleVersion > 0 && transitions.Count == record.LifecycleVersion, "Default-conversation lifecycle version must match its complete transition history.");
        Require(transitions[0].Checkpoint == DefaultConversationTurnCheckpoint.Admitted, "Default-conversation transition history must begin with admission.");

        DefaultConversationTurnTransition? previous = null;
        for (var index = 0; index < transitions.Count; index++)
        {
            var transition = transitions[index] ?? throw Invalid("Default-conversation transition evidence was missing.");
            var sequence = index + 1;
            Require(transition.Sequence == sequence, "Default-conversation transition sequence was incomplete or out of order.");
            Require(Enum.IsDefined(transition.Checkpoint) && transition.Checkpoint != DefaultConversationTurnCheckpoint.Unknown, "Default-conversation transition checkpoint was invalid.");
            Require(string.Equals(transition.TransitionId, DefaultConversationTurnProtocol.CreateTransitionId(record.TurnId, sequence, transition.Checkpoint), StringComparison.Ordinal), "Default-conversation transition identity was noncanonical.");
            Require(IsUtc(transition.OccurredAtUtc) && transition.OccurredAtUtc >= record.Run.StartedAtUtc, "Default-conversation transition time was invalid.");
            Require(!string.IsNullOrWhiteSpace(transition.Detail), "Default-conversation transition detail was missing.");
            if (previous is not null)
            {
                Require(transition.OccurredAtUtc >= previous.OccurredAtUtc, "Default-conversation transition times must be monotonic.");
                Require(IsLegalTransition(previous.Checkpoint, transition.Checkpoint), $"Default-conversation transition `{previous.Checkpoint}` -> `{transition.Checkpoint}` is not legal in schema version 1.");
            }

            previous = transition;
        }

        Require(transitions[^1].Checkpoint == record.Checkpoint, "Default-conversation checkpoint must match its newest transition.");
    }

    private static void ValidateProviderEvidence(DefaultConversationTurnRecord record)
    {
        Require(Enum.IsDefined(record.ProviderOutcome) && record.ProviderOutcome != DefaultConversationProviderOutcome.Unknown, "Default-conversation provider outcome was invalid.");
        var operationalCheckpoint = record.Transitions.Last(transition => transition.Checkpoint < DefaultConversationTurnCheckpoint.TerminalPrepared).Checkpoint;
        var expectedOutcome = operationalCheckpoint switch
        {
            < DefaultConversationTurnCheckpoint.ProviderDispatchStarted => DefaultConversationProviderOutcome.DefinitelyNotStarted,
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted => DefaultConversationProviderOutcome.OutcomeUnknown,
            _ => record.ProviderOutcome
        };
        if (operationalCheckpoint >= DefaultConversationTurnCheckpoint.ProviderOutcomeObserved)
        {
            Require(record.ProviderOutcome is DefaultConversationProviderOutcome.Observed or DefaultConversationProviderOutcome.ObservedWithAuditFailure or DefaultConversationProviderOutcome.ObservedFailure, "Default-conversation observed provider outcome classification was invalid.");
        }

        Require(record.ProviderOutcome == expectedOutcome, "Default-conversation provider outcome did not match the exact durable transition path.");
        var successfulOutcome = record.ProviderOutcome is DefaultConversationProviderOutcome.Observed or DefaultConversationProviderOutcome.ObservedWithAuditFailure;
        Require((record.AssistantMessage is not null) == successfulOutcome, "Default-conversation assistant evidence must exist exactly for a successful observed provider outcome.");
        Require(record.ProviderResponseId is null || (record.ProviderOutcome is DefaultConversationProviderOutcome.Observed or DefaultConversationProviderOutcome.ObservedWithAuditFailure or DefaultConversationProviderOutcome.ObservedFailure && !string.IsNullOrWhiteSpace(record.ProviderResponseId) && string.Equals(record.ProviderResponseId, record.ProviderResponseId.Trim(), StringComparison.Ordinal)), "Default-conversation provider response identity did not match observed outcome evidence.");
        if (record.ProviderOutcome is DefaultConversationProviderOutcome.ObservedWithAuditFailure or DefaultConversationProviderOutcome.ObservedFailure)
        {
            Require(operationalCheckpoint == DefaultConversationTurnCheckpoint.ProviderOutcomeObserved, "A conclusive provider outcome with failed completion bookkeeping cannot advance into assistant publication.");
        }
    }

    private static void ValidateTerminalEvidence(DefaultConversationTurnRecord record)
    {
        var terminal = record.Checkpoint >= DefaultConversationTurnCheckpoint.TerminalPrepared;
        Require(terminal == (record.Run.Status != LoopRunStatus.Started), "Default-conversation terminal checkpoint and loop-run projection must advance together.");
        Require(record.RunProjectionSynchronized == (record.Checkpoint >= DefaultConversationTurnCheckpoint.Terminal), "Default-conversation terminal synchronization evidence did not match its checkpoint.");
        if (!terminal)
        {
            Require(record.Run.CompletedAtUtc is null && record.Run.FailureDetail is null && record.ReviewCause == DefaultConversationTurnReviewCause.None && record.ReviewDetail is null && record.ReviewResolution is null, "A nonterminal default-conversation turn contained terminal evidence.");
            return;
        }

        var prepared = record.Transitions.Single(transition => transition.Checkpoint == DefaultConversationTurnCheckpoint.TerminalPrepared);
        var operationalTransition = record.Transitions.Last(transition => transition.Checkpoint < DefaultConversationTurnCheckpoint.TerminalPrepared);
        Require(record.Run.CompletedAtUtc is { } completedAtUtc && IsUtc(completedAtUtc) && completedAtUtc >= operationalTransition.OccurredAtUtc && completedAtUtc <= prepared.OccurredAtUtc, "Default-conversation terminal timestamp was invalid.");
        ValidateTerminalStatus(record, operationalTransition.Checkpoint);

        if (record.Checkpoint == DefaultConversationTurnCheckpoint.ReviewResolved)
        {
            ValidateReviewResolution(record);
        }
        else
        {
            Require(record.ReviewResolution is null, "Review resolution evidence requires the resolved-review checkpoint.");
        }
    }

    private static void ValidateTerminalStatus(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint operationalCheckpoint)
    {
        switch (record.Run.Status)
        {
            case LoopRunStatus.Completed:
                Require(operationalCheckpoint == DefaultConversationTurnCheckpoint.TranscriptSynchronized && record.ProviderOutcome == DefaultConversationProviderOutcome.Observed && record.Run.FailureDetail is null && record.ReviewCause == DefaultConversationTurnReviewCause.None && record.ReviewDetail is null, "Completed default-conversation evidence requires a synchronized successfully observed transcript and no failure detail.");
                break;
            case LoopRunStatus.Failed:
                var definitelyPreDispatch = operationalCheckpoint < DefaultConversationTurnCheckpoint.ProviderDispatchStarted;
                var terminalProviderFailure = operationalCheckpoint == DefaultConversationTurnCheckpoint.ProviderOutcomeObserved && record.ProviderOutcome == DefaultConversationProviderOutcome.ObservedFailure;
                Require((definitelyPreDispatch || terminalProviderFailure) && !string.IsNullOrWhiteSpace(record.Run.FailureDetail) && record.ReviewCause == DefaultConversationTurnReviewCause.None && record.ReviewDetail is null, "Failed default-conversation evidence must be definitively pre-dispatch or retain a conclusive terminal provider failure.");
                break;
            case LoopRunStatus.Cancelled:
                Require(operationalCheckpoint < DefaultConversationTurnCheckpoint.ProviderDispatchStarted && !string.IsNullOrWhiteSpace(record.Run.FailureDetail) && record.ReviewCause == DefaultConversationTurnReviewCause.None && record.ReviewDetail is null, "Cancelled default-conversation evidence must be definitively pre-dispatch and retain failure detail.");
                break;
            case LoopRunStatus.NeedsReview:
                Require(operationalCheckpoint != DefaultConversationTurnCheckpoint.TranscriptSynchronized && !string.IsNullOrWhiteSpace(record.Run.FailureDetail) && string.Equals(record.ReviewDetail, record.Run.FailureDetail, StringComparison.Ordinal) && IsValidReviewCause(record), "Needs-review evidence must retain one exact actionable detail, a matching durable cause, and cannot claim a synchronized transcript.");
                break;
            default:
                throw Invalid("Default-conversation terminal run status was invalid.");
        }
    }

    private static void ValidateReviewResolution(DefaultConversationTurnRecord record)
    {
        Require(record.Run.Status == LoopRunStatus.NeedsReview, "A resolved review requires an unresolved needs-review terminal predecessor.");
        var resolution = record.ReviewResolution ?? throw Invalid("A resolved review requires explicit resolution evidence.");
        var completedAtUtc = record.Run.CompletedAtUtc ?? throw Invalid("A resolved review requires terminal completion evidence.");
        Require(record.ProviderOutcome == DefaultConversationProviderOutcome.OutcomeUnknown && record.ReviewCause == DefaultConversationTurnReviewCause.OutcomeUnknown, "Only an outcome-unknown provider attempt can be abandoned after review.");
        Require(resolution.Disposition == DefaultConversationTurnReviewDisposition.Abandoned, "Default-conversation review disposition was invalid.");
        Require(string.Equals(resolution.ResolutionId, DefaultConversationTurnProtocol.CreateReviewResolutionId(record.TurnId), StringComparison.Ordinal), "Default-conversation review resolution identity was noncanonical.");
        Require(string.Equals(resolution.Detail, DefaultConversationTurnProtocol.ReviewAbandonmentDetail, StringComparison.Ordinal), "Default-conversation review resolution detail was noncanonical.");
        Require(IsUtc(resolution.ResolvedAtUtc) && resolution.ResolvedAtUtc >= completedAtUtc && resolution.ResolvedAtUtc == record.Transitions[^1].OccurredAtUtc, "Default-conversation review resolution timestamp was invalid.");
        Require(string.Equals(record.Transitions[^1].Detail, resolution.Detail, StringComparison.Ordinal), "Default-conversation resolved-review transition did not retain the canonical disposition detail.");
    }

    private static bool IsValidReviewCause(DefaultConversationTurnRecord record)
    {
        return record.ReviewCause switch
        {
            DefaultConversationTurnReviewCause.OutcomeUnknown => record.ProviderOutcome == DefaultConversationProviderOutcome.OutcomeUnknown,
            DefaultConversationTurnReviewCause.ObservedWithAuditFailure => record.ProviderOutcome == DefaultConversationProviderOutcome.ObservedWithAuditFailure,
            DefaultConversationTurnReviewCause.TranscriptConflict => true,
            _ => false
        };
    }

    internal static bool IsLegalTransition(DefaultConversationTurnCheckpoint from, DefaultConversationTurnCheckpoint to)
    {
        return from switch
        {
            DefaultConversationTurnCheckpoint.Admitted => to is DefaultConversationTurnCheckpoint.RunStarted or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.RunStarted => to is DefaultConversationTurnCheckpoint.UserMessageAccepted or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.UserMessageAccepted => to == DefaultConversationTurnCheckpoint.UserPublicationPrepared,
            DefaultConversationTurnCheckpoint.UserPublicationPrepared => to is DefaultConversationTurnCheckpoint.UserPublished or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.UserPublished => to is DefaultConversationTurnCheckpoint.ProviderDispatchPrepared or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.ProviderDispatchPrepared => to is DefaultConversationTurnCheckpoint.ProviderDispatchStarted or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted => to is DefaultConversationTurnCheckpoint.ProviderOutcomeObserved or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.ProviderOutcomeObserved => to is DefaultConversationTurnCheckpoint.AssistantPublicationPrepared or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.AssistantPublicationPrepared => to is DefaultConversationTurnCheckpoint.AssistantPublished or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.AssistantPublished => to is DefaultConversationTurnCheckpoint.TranscriptSynchronized or DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.TranscriptSynchronized => to == DefaultConversationTurnCheckpoint.TerminalPrepared,
            DefaultConversationTurnCheckpoint.TerminalPrepared => to == DefaultConversationTurnCheckpoint.Terminal,
            DefaultConversationTurnCheckpoint.Terminal => to == DefaultConversationTurnCheckpoint.ReviewResolved,
            _ => false
        };
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsCanonicalRuntimeSurface(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim().ToLowerInvariant(), StringComparison.Ordinal)
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsLowerHex(string? value, int length)
    {
        return value is not null && value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition)
        {
            throw Invalid(detail);
        }
    }

    private static FormatException Invalid(string detail) => new(detail);
}
