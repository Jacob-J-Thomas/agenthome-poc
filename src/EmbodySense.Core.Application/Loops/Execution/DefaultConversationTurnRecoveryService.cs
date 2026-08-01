using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Deterministically reconciles incomplete ordinary-chat turns without redispatching provider work.
/// </summary>
/// <remarks>
/// Recovery holds workspace conversation ownership for its entire scan. It may repair idempotent transcript publication and
/// terminal run projection. Outcome-unknown provider attempts and transcript conflicts are parked as NeedsReview unless a
/// conclusive terminal provider failure already proves that the attempt must close as Failed without touching the transcript.
/// </remarks>
public sealed class DefaultConversationTurnRecoveryService
{
    private readonly IConversationMemoryStore _conversationMemory;
    private readonly ILoopRunStore _loopRuns;
    private readonly IDefaultConversationTurnStore _turns;
    private readonly IConversationWorkspaceLease? _workspaceLease;
    private readonly IDefaultConversationTurnFailpoint? _failpoint;
    private readonly ICapabilityAdmissionService? _capabilityAdmissionService;

    /// <summary>Initializes one recovery coordinator.</summary>
    public DefaultConversationTurnRecoveryService(
        IDefaultConversationTurnStore turns,
        IConversationMemoryStore conversationMemory,
        ILoopRunStore loopRuns,
        IConversationWorkspaceLease? workspaceLease = null,
        IDefaultConversationTurnFailpoint? failpoint = null,
        ICapabilityAdmissionService? capabilityAdmissionService = null)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(loopRuns);
        _turns = turns;
        _conversationMemory = conversationMemory;
        _loopRuns = loopRuns;
        _workspaceLease = workspaceLease;
        _failpoint = failpoint;
        _capabilityAdmissionService = capabilityAdmissionService;
    }

    /// <summary>
    /// Scans and reconciles every nonterminal turn in deterministic admission order.
    /// </summary>
    /// <param name="cancellationToken">Cancels lease acquisition, reads, and repair writes.</param>
    /// <returns>The per-turn classification and whether startup must preserve the active conversation.</returns>
    public async Task<DefaultConversationTurnRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        using var workspaceLease = _workspaceLease is null ? null : await _workspaceLease.AcquireAsync(cancellationToken);
        var incomplete = await _turns.ListIncompleteAsync(cancellationToken);
        var results = new List<DefaultConversationTurnRecoveryResult>(incomplete.Count);
        foreach (var record in incomplete)
        {
            results.Add(await RecoverOneAsync(record, cancellationToken));
        }

        var current = await _conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
        var activeReview = (await _turns.ListNeedsReviewAsync(cancellationToken)).Any(record => IdentityMatches(record, current));
        return new DefaultConversationTurnRecoveryReport(results, incomplete.Count > 0 || activeReview);
    }

    private async Task<DefaultConversationTurnRecoveryResult> RecoverOneAsync(DefaultConversationTurnRecord record, CancellationToken cancellationToken)
    {
        var originalCheckpoint = record.Checkpoint;
        if (_capabilityAdmissionService is not null)
        {
            var allowed = LoopCapabilityRequirements.GetAssignedCapabilityIds(LoopDefinition.CreateDefaultConversation().CapabilityRequirements);
            var capabilities = await _capabilityAdmissionService.RevalidateAsync(record.CapabilityAdmission, allowed, cancellationToken);
            if (!capabilities.IsValid)
            {
                var detail = $"Restart capability revalidation failed closed: {capabilities.Detail}";
                var status = record.Checkpoint < DefaultConversationTurnCheckpoint.ProviderDispatchStarted ? LoopRunStatus.Failed : LoopRunStatus.NeedsReview;
                record = await FinalizeAsync(record, status, detail, cancellationToken);
                return Result(record, originalCheckpoint, status == LoopRunStatus.Failed ? DefaultConversationTurnRecoveryClassification.PreDispatch : DefaultConversationTurnRecoveryClassification.ProviderOutcomeUnknown, detail);
            }
        }

        switch (record.Checkpoint)
        {
            case DefaultConversationTurnCheckpoint.Admitted:
            case DefaultConversationTurnCheckpoint.RunStarted:
                if (!await CurrentTranscriptEqualsAsync(record, record.BaseTranscript, cancellationToken))
                {
                    return await ParkConflictAsync(record, originalCheckpoint, "The transcript changed before the accepted user message was published; existing content was preserved.", cancellationToken);
                }

                record = await FinalizeAsync(record, LoopRunStatus.Failed, "Process exited before the user message was durably accepted. No provider dispatch occurred.", cancellationToken);
                return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.PreDispatch, "No provider dispatch occurred; the interrupted admission was closed as Failed.");

            case DefaultConversationTurnCheckpoint.UserMessageAccepted:
                record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.UserPublicationPrepared, "Recovery prepared the accepted user-message publication.", DefaultConversationTurnBoundary.UserPublicationPrepared, cancellationToken);
                return await RecoverAcceptedUserAsync(record, originalCheckpoint, cancellationToken);

            case DefaultConversationTurnCheckpoint.UserPublicationPrepared:
                return await RecoverAcceptedUserAsync(record, originalCheckpoint, cancellationToken);

            case DefaultConversationTurnCheckpoint.UserPublished:
            case DefaultConversationTurnCheckpoint.ProviderDispatchPrepared:
                if (!await CurrentTranscriptEqualsAsync(record, CanonicalUserTranscript(record), cancellationToken))
                {
                    return await ParkConflictAsync(record, originalCheckpoint, "The canonical transcript no longer exactly matches the accepted pre-dispatch user publication; existing content was preserved.", cancellationToken);
                }

                record = await FinalizeAsync(record, LoopRunStatus.Failed, "Process exited before provider dispatch began. The accepted user message remains in the transcript and no provider request was repeated.", cancellationToken);
                return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.PreDispatch, "Provider dispatch was definitely not started; the turn was closed without automatic dispatch.");

            case DefaultConversationTurnCheckpoint.ProviderDispatchStarted:
                if (!await CurrentTranscriptEqualsAsync(record, CanonicalUserTranscript(record), cancellationToken))
                {
                    return await ParkConflictAsync(record, originalCheckpoint, "The transcript diverged while the provider outcome was unknown; existing content was preserved and no provider request was repeated.", cancellationToken);
                }

                record = await FinalizeAsync(record, LoopRunStatus.NeedsReview, OutcomeUnknownDetail(record), cancellationToken);
                return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.ProviderOutcomeUnknown, OutcomeUnknownDetail(record));

            case DefaultConversationTurnCheckpoint.ProviderOutcomeObserved:
                if (record.ProviderOutcome == DefaultConversationProviderOutcome.ObservedWithAuditFailure)
                {
                    var transcriptMatches = await CurrentTranscriptEqualsAsync(record, CanonicalUserTranscript(record), cancellationToken);
                    var auditFailure = record.Transitions.Last(transition => transition.Checkpoint == DefaultConversationTurnCheckpoint.ProviderOutcomeObserved).Detail;
                    var reviewDetail = transcriptMatches
                        ? auditFailure
                        : $"{auditFailure} Recovery also detected divergent transcript content and preserved it exactly.";
                    record = await FinalizeAsync(record, LoopRunStatus.NeedsReview, reviewDetail, cancellationToken);
                    return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, reviewDetail);
                }

                if (record.ProviderOutcome == DefaultConversationProviderOutcome.ObservedFailure)
                {
                    var transcriptMatches = await CurrentTranscriptEqualsAsync(record, CanonicalUserTranscript(record), cancellationToken);
                    var providerFailure = record.Transitions.Last(transition => transition.Checkpoint == DefaultConversationTurnCheckpoint.ProviderOutcomeObserved).Detail;
                    var failureDetail = transcriptMatches
                        ? providerFailure
                        : $"{providerFailure} Recovery also detected divergent transcript content and preserved it exactly.";
                    record = await FinalizeAsync(record, LoopRunStatus.Failed, failureDetail, cancellationToken);
                    var detail = transcriptMatches
                        ? "The conclusive terminal provider failure and Failed run status were recovered without quarantine or redispatch."
                        : "The conclusive terminal provider failure was closed as Failed without quarantine or redispatch; divergent transcript content was preserved exactly.";
                    return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, detail);
                }

                record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.AssistantPublicationPrepared, "Recovery prepared publication of the durably observed assistant output.", DefaultConversationTurnBoundary.AssistantPublicationPrepared, cancellationToken);
                return await RecoverObservedAssistantAsync(record, originalCheckpoint, cancellationToken);

            case DefaultConversationTurnCheckpoint.AssistantPublicationPrepared:
                return await RecoverObservedAssistantAsync(record, originalCheckpoint, cancellationToken);

            case DefaultConversationTurnCheckpoint.AssistantPublished:
                if (!await CurrentTranscriptEqualsAsync(record, CanonicalAssistantTranscript(record), cancellationToken))
                {
                    return await ParkConflictAsync(record, originalCheckpoint, "The transcript diverged after assistant publication; existing content was preserved.", cancellationToken);
                }

                record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.TranscriptSynchronized, "Recovery verified the complete canonical transcript projection.", DefaultConversationTurnBoundary.TranscriptSynchronized, cancellationToken);
                record = await FinalizeAsync(record, LoopRunStatus.Completed, "Recovered a completely published provider outcome.", cancellationToken);
                return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, "The observed provider output and terminal run status were recovered without redispatch.");

            case DefaultConversationTurnCheckpoint.TranscriptSynchronized:
                record = await FinalizeAsync(record, LoopRunStatus.Completed, "Recovered a synchronized transcript whose terminal run projection was missing.", cancellationToken);
                return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.TerminalStatusMissing, "Only the terminal run projection was missing; it was reconstructed from the checkpoint.");

            case DefaultConversationTurnCheckpoint.TerminalPrepared:
                record = await FinalizeAsync(record, record.Run.Status, record.Run.FailureDetail ?? "Recovered the prepared terminal run projection.", cancellationToken);
                return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.TerminalStatusMissing, "The prepared terminal run projection was idempotently synchronized.");

            default:
                throw new InvalidOperationException($"Unsupported incomplete default-conversation checkpoint `{record.Checkpoint}`.");
        }
    }

    private async Task<DefaultConversationTurnRecoveryResult> RecoverAcceptedUserAsync(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint originalCheckpoint, CancellationToken cancellationToken)
    {
        var publication = await EnsureAppendAsync(record, record.BaseTranscript, record.UserMessage, DefaultConversationTurnBoundary.UserTranscriptAppended, cancellationToken);
        if (!publication.Safe)
        {
            return await ParkConflictAsync(record, originalCheckpoint, publication.Detail, cancellationToken);
        }

        record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.UserPublished, "Recovery proved the accepted user message is published exactly once.", DefaultConversationTurnBoundary.UserPublished, cancellationToken);
        record = await FinalizeAsync(record, LoopRunStatus.Failed, "Process exited after accepting the user message but before provider dispatch. No provider request was repeated.", cancellationToken);
        var classification = publication.AlreadyPresent ? DefaultConversationTurnRecoveryClassification.TranscriptPartial : DefaultConversationTurnRecoveryClassification.PreDispatch;
        return Result(record, originalCheckpoint, classification, "The accepted user message was reconciled exactly once and the pre-dispatch turn was closed.");
    }

    private async Task<DefaultConversationTurnRecoveryResult> RecoverObservedAssistantAsync(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint originalCheckpoint, CancellationToken cancellationToken)
    {
        if (record.AssistantMessage is null)
        {
            return await ParkConflictAsync(record, originalCheckpoint, "Provider outcome evidence did not retain an assistant message; automatic reconstruction is forbidden.", cancellationToken);
        }

        var publication = await EnsureAppendAsync(record, CanonicalUserTranscript(record), record.AssistantMessage, DefaultConversationTurnBoundary.AssistantTranscriptAppended, cancellationToken);
        if (!publication.Safe)
        {
            return await ParkConflictAsync(record, originalCheckpoint, publication.Detail, cancellationToken);
        }

        record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.AssistantPublished, "Recovery proved the observed assistant message is published exactly once.", DefaultConversationTurnBoundary.AssistantPublished, cancellationToken);
        record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.TranscriptSynchronized, "Recovery verified the complete canonical transcript projection.", DefaultConversationTurnBoundary.TranscriptSynchronized, cancellationToken);
        record = await FinalizeAsync(record, LoopRunStatus.Completed, "Recovered a durably observed provider outcome and its transcript publication.", cancellationToken);
        var classification = publication.AlreadyPresent ? DefaultConversationTurnRecoveryClassification.TranscriptPartial : DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved;
        return Result(record, originalCheckpoint, classification, "The observed provider output was published exactly once and the run completed without redispatch.");
    }

    private async Task<DefaultConversationTurnRecoveryResult> ParkConflictAsync(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint originalCheckpoint, string detail, CancellationToken cancellationToken)
    {
        record = await FinalizeAsync(record, LoopRunStatus.NeedsReview, detail, cancellationToken);
        return Result(record, originalCheckpoint, DefaultConversationTurnRecoveryClassification.Conflict, detail);
    }

    private async Task<DefaultConversationTurnRecord> FinalizeAsync(DefaultConversationTurnRecord record, LoopRunStatus status, string detail, CancellationToken cancellationToken)
    {
        if (record.Checkpoint < DefaultConversationTurnCheckpoint.TerminalPrepared)
        {
            var now = DateTimeOffset.UtcNow;
            var terminalRun = status switch
            {
                LoopRunStatus.Completed => record.Run.Complete(now),
                LoopRunStatus.Cancelled => record.Run.Cancel(now, detail),
                LoopRunStatus.NeedsReview => record.Run.NeedsReview(now, detail),
                LoopRunStatus.Failed => record.Run.Fail(now, detail),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a terminal recovery status.")
            };
            record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.TerminalPrepared, "Recovery persisted the desired terminal run status and checkpoint.", DefaultConversationTurnBoundary.TerminalPrepared, cancellationToken, run: terminalRun, reviewDetail: status == LoopRunStatus.NeedsReview ? detail : null);
        }

        await _loopRuns.SaveAsync(record.Run, cancellationToken);
        await InvokeFailpointAsync(DefaultConversationTurnBoundary.TerminalRunSaved, record, cancellationToken);
        record = await AdvanceAsync(record, DefaultConversationTurnCheckpoint.Terminal, "Recovery synchronized the terminal loop-run projection.", DefaultConversationTurnBoundary.TerminalCommitted, cancellationToken, runProjectionSynchronized: true);
        return record;
    }

    private async Task<DefaultConversationTurnRecord> AdvanceAsync(
        DefaultConversationTurnRecord record,
        DefaultConversationTurnCheckpoint checkpoint,
        string detail,
        DefaultConversationTurnBoundary boundary,
        CancellationToken cancellationToken,
        LoopRunRecord? run = null,
        bool? runProjectionSynchronized = null,
        string? reviewDetail = null)
    {
        var candidate = record.Advance(checkpoint, DateTimeOffset.UtcNow, detail, run: run, runProjectionSynchronized: runProjectionSynchronized, reviewDetail: reviewDetail);
        var result = await _turns.UpdateAsync(candidate, record.LifecycleVersion, cancellationToken);
        if (result.Status is not (DefaultConversationTurnStoreStatus.Updated or DefaultConversationTurnStoreStatus.Replay) || result.Record is null)
        {
            throw new InvalidOperationException($"Default-conversation recovery checkpoint `{checkpoint}` conflicted with durable lifecycle version `{record.LifecycleVersion}`.");
        }

        await InvokeFailpointAsync(boundary, result.Record, cancellationToken);
        return result.Record;
    }

    private async Task<(bool Safe, bool AlreadyPresent, string Detail)> EnsureAppendAsync(
        DefaultConversationTurnRecord record,
        IReadOnlyList<LlmMessage> expectedPrefix,
        DefaultConversationTurnMessage message,
        DefaultConversationTurnBoundary boundary,
        CancellationToken cancellationToken)
    {
        var publicationId = message.Role == LlmMessageRole.User ? record.UserPublicationId : record.AssistantPublicationId;
        var publication = new ConversationMessagePublication(message.MessageId, publicationId, message.ToLlmMessage());
        var result = await _conversationMemory.TryPublishMessageAsync(record.ConversationId, record.ConversationVersion, expectedPrefix, publication, cancellationToken);
        if (result.Status == ConversationPublicationAppendStatus.Appended)
        {
            await InvokeFailpointAsync(boundary, record, cancellationToken);
            return (true, false, "The message append committed.");
        }

        if (result.Status == ConversationPublicationAppendStatus.AlreadyPresent)
        {
            return (true, true, "The exact identity-bearing message publication had already committed.");
        }

        return (false, false, $"Conversation `{record.ConversationId}` version `{record.ConversationVersion}` no longer has the exact expected prefix for publication `{publicationId}` and message `{message.MessageId}`; existing content was preserved.");
    }

    private async Task<bool> CurrentTranscriptEqualsAsync(DefaultConversationTurnRecord record, IReadOnlyList<LlmMessage> expected, CancellationToken cancellationToken)
    {
        var current = await _conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
        return IdentityMatches(record, current) && MessagesEqual(current.Messages, expected);
    }

    private static IReadOnlyList<LlmMessage> CanonicalUserTranscript(DefaultConversationTurnRecord record)
    {
        return [.. record.BaseTranscript, record.UserMessage.ToLlmMessage()];
    }

    private static IReadOnlyList<LlmMessage> CanonicalAssistantTranscript(DefaultConversationTurnRecord record)
    {
        return record.AssistantMessage is null
            ? CanonicalUserTranscript(record)
            : [.. CanonicalUserTranscript(record), record.AssistantMessage.ToLlmMessage()];
    }

    private static bool IdentityMatches(DefaultConversationTurnRecord record, ConversationMemorySnapshot snapshot)
    {
        return string.Equals(record.ConversationId, snapshot.ConversationId, StringComparison.Ordinal)
            && string.Equals(record.ConversationVersion, snapshot.Version, StringComparison.Ordinal);
    }

    private static bool MessagesEqual(IReadOnlyList<LlmMessage> left, IReadOnlyList<LlmMessage> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
    }

    private static string OutcomeUnknownDetail(DefaultConversationTurnRecord record)
    {
        return $"Provider attempt `{record.ProviderAttemptId}` with correlation `{record.ProviderCorrelationId}` reached the irreversible turn/start transport-write boundary, but no terminal outcome was durably observed. Automatic redispatch is forbidden; inspect provider and audit evidence before deciding whether to publish, retry as a new turn, or abandon the attempt.";
    }

    private static DefaultConversationTurnRecoveryResult Result(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint originalCheckpoint, DefaultConversationTurnRecoveryClassification classification, string detail)
    {
        return new DefaultConversationTurnRecoveryResult(record.TurnId, record.Run.RunId, classification, originalCheckpoint, record.Run.Status, detail);
    }

    private Task InvokeFailpointAsync(DefaultConversationTurnBoundary boundary, DefaultConversationTurnRecord record, CancellationToken cancellationToken)
    {
        return _failpoint?.AfterBoundaryAsync(boundary, record, cancellationToken) ?? Task.CompletedTask;
    }
}
