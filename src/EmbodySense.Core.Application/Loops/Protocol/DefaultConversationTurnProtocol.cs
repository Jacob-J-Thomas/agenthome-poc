using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.Loops.Protocol;

/// <summary>
/// Creates and advances canonical default-conversation turn protocol values.
/// </summary>
public static class DefaultConversationTurnProtocol
{
    internal const string ReviewAbandonmentDetail = "Human review explicitly abandoned the outcome-unknown provider attempt without publication or redispatch.";

    /// <summary>
    /// Creates the first durable admission checkpoint and every stable identity used by the turn.
    /// </summary>
    /// <param name="run">The admitted Started run.</param>
    /// <param name="conversation">The exact current conversation identity, version, and prefix.</param>
    /// <param name="userMessage">The exact accepted input candidate.</param>
    /// <param name="admittedAtUtc">The admission time.</param>
    /// <param name="requestId">The caller-owned idempotency identity.</param>
    /// <param name="capabilityAdmission">The server-resolved immutable capability pins and evidence.</param>
    /// <returns>A lifecycle-version-one admitted record.</returns>
    public static DefaultConversationTurnRecord Admit(LoopRunRecord run, ConversationMemorySnapshot conversation, LlmMessage userMessage, DateTimeOffset admittedAtUtc, string requestId, CapabilityAdmissionSnapshot capabilityAdmission)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(userMessage);
        ArgumentNullException.ThrowIfNull(capabilityAdmission);

        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        requestId = requestId.Trim();
        var turnId = CreateTurnId(requestId);
        var transition = new DefaultConversationTurnTransition(1, CreateTransitionId(turnId, 1, DefaultConversationTurnCheckpoint.Admitted), DefaultConversationTurnCheckpoint.Admitted, admittedAtUtc, "Turn identity and canonical base transcript admitted.");
        // TODO(#259): Bound active/review scans without deleting or rewriting this immutable transcript evidence. https://github.com/Jacob-J-Thomas/agenthome-poc/issues/259
        return new DefaultConversationTurnRecord(
            DefaultConversationTurnRecord.CurrentSchemaVersion,
            1,
            turnId,
            requestId,
            run,
            conversation.ConversationId,
            conversation.Version,
            conversation.Messages.ToArray(),
            new DefaultConversationTurnMessage(CreateUserMessageId(turnId), userMessage.Role, userMessage.Content),
            null,
            CreateProviderAttemptId(turnId),
            CreateProviderCorrelationId(turnId),
            CreateUserPublicationId(turnId),
            CreateAssistantPublicationId(turnId),
            DefaultConversationProviderOutcome.DefinitelyNotStarted,
            null,
            DefaultConversationTurnCheckpoint.Admitted,
            false,
            DefaultConversationTurnReviewCause.None,
            null,
            null,
            [transition])
        {
            CapabilityAdmission = capabilityAdmission
        };
    }

    /// <summary>
    /// Appends one later checkpoint without changing any stable identity or prior transition.
    /// </summary>
    /// <param name="record">The current canonical record.</param>
    /// <param name="checkpoint">The strictly later checkpoint.</param>
    /// <param name="occurredAtUtc">The transition time.</param>
    /// <param name="detail">The non-empty evidence summary.</param>
    /// <param name="providerOutcome">An optional provider-outcome replacement.</param>
    /// <param name="assistantMessage">An optional exact assistant message.</param>
    /// <param name="providerResponseId">An optional provider response identity.</param>
    /// <param name="run">An optional desired run projection.</param>
    /// <param name="runProjectionSynchronized">An optional run-projection synchronization value.</param>
    /// <param name="reviewDetail">Optional actionable conflict or ambiguity evidence.</param>
    /// <param name="reviewResolution">Optional explicit human review-resolution evidence.</param>
    /// <param name="reviewCause">Optional durable cause for a needs-review terminal state.</param>
    /// <returns>The next lifecycle version.</returns>
    public static DefaultConversationTurnRecord Advance(
        this DefaultConversationTurnRecord record,
        DefaultConversationTurnCheckpoint checkpoint,
        DateTimeOffset occurredAtUtc,
        string detail,
        DefaultConversationProviderOutcome? providerOutcome = null,
        DefaultConversationTurnMessage? assistantMessage = null,
        string? providerResponseId = null,
        LoopRunRecord? run = null,
        bool? runProjectionSynchronized = null,
        string? reviewDetail = null,
        DefaultConversationTurnReviewResolution? reviewResolution = null,
        DefaultConversationTurnReviewCause? reviewCause = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(checkpoint) || !DefaultConversationTurnProtocolValidator.IsLegalTransition(record.Checkpoint, checkpoint))
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, $"The default-conversation transition `{record.Checkpoint}` -> `{checkpoint}` is not legal in schema version 1.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var nextVersion = record.LifecycleVersion + 1;
        var transitionId = CreateTransitionId(record.TurnId, nextVersion, checkpoint);
        var transition = new DefaultConversationTurnTransition(nextVersion, transitionId, checkpoint, occurredAtUtc, detail);
        return record with
        {
            LifecycleVersion = nextVersion,
            Run = run ?? record.Run,
            AssistantMessage = assistantMessage ?? record.AssistantMessage,
            ProviderOutcome = providerOutcome ?? record.ProviderOutcome,
            ProviderResponseId = providerResponseId ?? record.ProviderResponseId,
            Checkpoint = checkpoint,
            RunProjectionSynchronized = runProjectionSynchronized ?? record.RunProjectionSynchronized,
            ReviewCause = reviewCause ?? InferReviewCause(record, run, providerOutcome),
            ReviewDetail = reviewDetail ?? record.ReviewDetail,
            ReviewResolution = reviewResolution ?? record.ReviewResolution,
            Transitions = [.. record.Transitions, transition]
        };
    }

    /// <summary>
    /// Appends the one explicit abandonment decision permitted for an outcome-unknown needs-review turn.
    /// </summary>
    public static DefaultConversationTurnRecord ResolveReview(this DefaultConversationTurnRecord record, DateTimeOffset resolvedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Checkpoint != DefaultConversationTurnCheckpoint.Terminal || record.Run.Status != LoopRunStatus.NeedsReview || record.ReviewResolution is not null)
        {
            throw new InvalidOperationException("Only an unresolved terminal NeedsReview turn can be resolved.");
        }

        if (!CanAbandonReview(record))
        {
            throw new InvalidOperationException($"Default-conversation turn `{record.TurnId}` is classified as {GetReviewClassification(record)} and cannot be abandoned. {GetReviewAction(record)}");
        }

        var resolution = new DefaultConversationTurnReviewResolution(
            CreateReviewResolutionId(record.TurnId),
            DefaultConversationTurnReviewDisposition.Abandoned,
            resolvedAtUtc,
            ReviewAbandonmentDetail);
        return record.Advance(DefaultConversationTurnCheckpoint.ReviewResolved, resolvedAtUtc, resolution.Detail, reviewResolution: resolution);
    }

    /// <summary>
    /// Classifies the durable review evidence for a turn without inferring a human disposition.
    /// </summary>
    /// <param name="record">The durable default-conversation turn.</param>
    /// <returns>The review classification implied by retained provider and transcript evidence.</returns>
    public static DefaultConversationTurnReviewClassification GetReviewClassification(DefaultConversationTurnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.ReviewCause switch
        {
            DefaultConversationTurnReviewCause.OutcomeUnknown => DefaultConversationTurnReviewClassification.OutcomeUnknown,
            DefaultConversationTurnReviewCause.ObservedWithAuditFailure => DefaultConversationTurnReviewClassification.ObservedWithAuditFailure,
            DefaultConversationTurnReviewCause.TranscriptConflict => DefaultConversationTurnReviewClassification.TranscriptConflict,
            _ => DefaultConversationTurnReviewClassification.Unknown
        };
    }

    private static DefaultConversationTurnReviewCause InferReviewCause(DefaultConversationTurnRecord record, LoopRunRecord? run, DefaultConversationProviderOutcome? providerOutcome)
    {
        if (record.ReviewCause != DefaultConversationTurnReviewCause.None || run?.Status != LoopRunStatus.NeedsReview)
        {
            return record.ReviewCause;
        }

        return (providerOutcome ?? record.ProviderOutcome) switch
        {
            DefaultConversationProviderOutcome.OutcomeUnknown => DefaultConversationTurnReviewCause.OutcomeUnknown,
            DefaultConversationProviderOutcome.ObservedWithAuditFailure => DefaultConversationTurnReviewCause.ObservedWithAuditFailure,
            _ => DefaultConversationTurnReviewCause.TranscriptConflict
        };
    }

    /// <summary>
    /// Determines whether the retained review evidence permits the explicit abandonment disposition.
    /// </summary>
    /// <param name="record">The durable default-conversation turn.</param>
    /// <returns><see langword="true"/> only for an unresolved outcome-unknown review.</returns>
    public static bool CanAbandonReview(DefaultConversationTurnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Checkpoint == DefaultConversationTurnCheckpoint.Terminal
            && record.Run.Status == LoopRunStatus.NeedsReview
            && record.ReviewResolution is null
            && GetReviewClassification(record) == DefaultConversationTurnReviewClassification.OutcomeUnknown;
    }

    /// <summary>
    /// Describes the only currently supported human action for the retained review classification.
    /// </summary>
    /// <param name="record">The durable default-conversation turn.</param>
    /// <returns>Operator-facing guidance that does not imply unavailable automation.</returns>
    public static string GetReviewAction(DefaultConversationTurnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return GetReviewClassification(record) switch
        {
            DefaultConversationTurnReviewClassification.OutcomeUnknown => $"After inspecting provider and audit evidence, `/review resolve {record.TurnId}` may explicitly abandon this outcome-unknown attempt without redispatch or transcript publication.",
            DefaultConversationTurnReviewClassification.TranscriptConflict => "Transcript-conflict review remains blocked; no state-appropriate human disposition is implemented.",
            DefaultConversationTurnReviewClassification.ObservedWithAuditFailure => "Observed-with-audit-failure review remains blocked; no state-appropriate human disposition is implemented.",
            _ => "This review remains blocked because its retained evidence has no supported disposition."
        };
    }

    /// <summary>
    /// Creates the deterministic durable turn identity for a caller-owned request identity.
    /// </summary>
    public static string CreateTurnId(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId.Trim()))).ToLowerInvariant();
        return "turn-" + hash;
    }

    /// <summary>
    /// Creates the deterministic loop-run identity paired with one caller request.
    /// </summary>
    public static string CreateRunId(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId.Trim()))).ToLowerInvariant();
        return "run-" + hash;
    }

    internal static string CreateUserMessageId(string turnId) => turnId + ":message:user";

    internal static string CreateAssistantMessageId(string turnId) => turnId + ":message:assistant";

    internal static string CreateProviderAttemptId(string turnId) => turnId + ":provider-attempt:1";

    internal static string CreateProviderCorrelationId(string turnId) => turnId + ":provider-correlation:1";

    internal static string CreateUserPublicationId(string turnId) => turnId + ":publication:user";

    internal static string CreateAssistantPublicationId(string turnId) => turnId + ":publication:assistant";

    internal static string CreateReviewResolutionId(string turnId) => turnId + ":review-resolution:abandoned";

    internal static string CreateTransitionId(string turnId, int sequence, DefaultConversationTurnCheckpoint checkpoint)
    {
        return $"{turnId}:{sequence}:{checkpoint.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// Converts a retained durable message to the provider-neutral message contract.
    /// </summary>
    /// <param name="message">The retained message.</param>
    /// <returns>The exact role and content.</returns>
    public static LlmMessage ToLlmMessage(this DefaultConversationTurnMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new LlmMessage(message.Role, message.Content);
    }
}
