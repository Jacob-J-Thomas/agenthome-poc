using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Memory.Models;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CurrentConversationLoopPublisher : ICustomLoopConversationPublisher
{
    private static readonly TimeSpan _reconciliationTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRememberedNotificationOperations = 1_024;
    private readonly ConversationRuntimeState _conversationState;
    private readonly IConversationMemoryStore _conversationMemory;
    private readonly IAgentRuntimeConversationPublicationObserver? _observer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LinkedListNode<string>> _notifiedOperations = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _notificationOrder = [];

    /// <summary>
    /// Creates the serialized publication boundary for one active runtime conversation.
    /// </summary>
    /// <param name="conversationState">The conversation state.</param>
    /// <param name="conversationMemory">The conversation memory.</param>
    /// <param name="observer">The observer.</param>
    public CurrentConversationLoopPublisher(
        ConversationRuntimeState conversationState,
        IConversationMemoryStore conversationMemory,
        IAgentRuntimeConversationPublicationObserver? observer = null)
    {
        _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));
        _conversationMemory = conversationMemory ?? throw new ArgumentNullException(nameof(conversationMemory));
        _observer = observer;
    }

    /// <summary>
    /// Atomically appends one verified canonical loop output to the admitted invoking conversation
    /// and reconciles ambiguous append outcomes.
    /// </summary>
    /// <param name="request">The operation identity, admitted conversation identity and prefix, prior publications, and exact hashed output.</param>
    /// <param name="cancellationToken">The token used to cancel serialized verification and append work.</param>
    /// <returns>
    /// A task whose result distinguishes verified publication, definite non-publication, and an
    /// uncertain outcome that callers must not retry as though nothing committed.
    /// </returns>
    /// <remarks>
    /// Publication requires the active and durable conversations to match the immutable admission
    /// prefix plus exact prior publications. Append uses the persistence compare-and-append boundary.
    /// Post-append notification is de-duplicated per operation and does not replace durable verification.
    /// </remarks>
    public async Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var conversationLease = await _conversationState.AcquireExclusiveAccessAsync(cancellationToken);

            if (!MatchesHash(request.CanonicalOutput, request.CanonicalOutputHash))
            {
                return DefinitelyFailed(request, "The canonical output hash did not match the exact publication content.");
            }

            if (!ValidatePriorPublications(request, out var priorValidationDetail))
            {
                return DefinitelyFailed(request, priorValidationDetail);
            }

            ConversationMemorySnapshot persistedConversation;
            try
            {
                persistedConversation = await _conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Uncertain(request, $"The persisted conversation could not be verified before publication: {exception.GetType().Name}.");
            }

            if (!string.Equals(persistedConversation.Version, request.ConversationId, StringComparison.Ordinal))
            {
                return DefinitelyFailed(request, "The durable current conversation identity no longer matches the conversation admitted for this run.");
            }

            var stateMessages = CustomLoopRuntimeContext.GetLogicalConversationMessages(_conversationState).ToArray();
            var outputAlreadyProjected = IsExpectedPrefixPlusOutput(stateMessages, request);
            if (!outputAlreadyProjected && !MatchesExpectedPublicationPrefix(stateMessages, request))
            {
                return DefinitelyFailed(request, "The invoking conversation did not equal the immutable admission prefix plus this run's exact prior publications; publication was not attempted.");
            }

            if (!MessagesEqual(persistedConversation.Messages, stateMessages))
            {
                return DefinitelyFailed(request, "The persisted conversation and active logical conversation differed before publication.");
            }

            var expectedPrefix = outputAlreadyProjected ? stateMessages.Take(stateMessages.Length - 1).ToArray() : stateMessages;
            var commitBoundary = request.AppendCommitBoundary ?? CommitAppendDirectlyAsync;
            var commit = await ConversationPublicationCommitProtocol.ExecuteAsync(
                commitBoundary,
                async token =>
                {
                    request.AppendStarted?.Invoke();
                    return await _conversationMemory.TryPublishMessageAsync(
                        persistedConversation.ConversationId,
                        request.ConversationId,
                        expectedPrefix,
                        new ConversationMessagePublication(MessageId(request.OperationId), request.OperationId, LlmMessage.Assistant(request.CanonicalOutput)),
                        token);
                },
                cancellationToken);

            if (commit.Status != ConversationPublicationCommitProtocolStatus.Completed)
            {
                if (commit.Status is ConversationPublicationCommitProtocolStatus.CallbackInvokedMultipleTimes or ConversationPublicationCommitProtocolStatus.CallbackIncomplete)
                {
                    return Uncertain(request, $"The conversation publication commit boundary violated its callback protocol ({commit.Status}); replay is required before projection.");
                }

                if (commit.Status == ConversationPublicationCommitProtocolStatus.BoundaryFailed
                    && commit.Value is not null
                    && commit.Value.Status is ConversationPublicationAppendStatus.Appended or ConversationPublicationAppendStatus.AlreadyPresent
                    && IsExactAppendSnapshot(commit.Value.Snapshot, persistedConversation.ConversationId, request.ConversationId, expectedPrefix, request.CanonicalOutput))
                {
                    return Uncertain(request, $"The exact identity-bearing append completed, but its caller-owned boundary failed with {commit.Failure?.GetType().Name ?? "UnknownFailure"}; replay is required before projection.");
                }

                return await ReconcileAppendExceptionAsync(
                    request,
                    expectedPrefix,
                    commit.Failure ?? new InvalidOperationException($"Conversation publication commit protocol stopped with {commit.Status}."));
            }

            var appendResult = commit.Value!;
            if (appendResult.Status == ConversationPublicationAppendStatus.Conflict)
            {
                return DefinitelyFailed(request, "The persisted invoking conversation changed at the atomic publication boundary; no custom-loop output was appended.");
            }

            if (appendResult.Status is not (ConversationPublicationAppendStatus.Appended or ConversationPublicationAppendStatus.AlreadyPresent))
            {
                return Uncertain(request, "The identity-bearing publication boundary returned an unsupported append status.");
            }

            var alreadyPublished = appendResult.Status == ConversationPublicationAppendStatus.AlreadyPresent;
            if (!IsExactAppendSnapshot(appendResult.Snapshot, persistedConversation.ConversationId, request.ConversationId, expectedPrefix, request.CanonicalOutput))
            {
                return Uncertain(request, "The identity-bearing publication boundary returned a snapshot that did not prove the exact append outcome.");
            }

            if (!outputAlreadyProjected)
            {
                _conversationState.AppendMessage(LlmMessage.Assistant(request.CanonicalOutput));
            }

            return await VerifyPublishedAsync(request, expectedPrefix, cancellationToken, alreadyPublished);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task CommitAppendDirectlyAsync(
        Func<CancellationToken, Task> commitAppend,
        CancellationToken cancellationToken)
    {
        return commitAppend(cancellationToken);
    }

    private static string MessageId(string operationId) => $"custom-loop-output-{operationId}";

    private async Task<CustomLoopConversationPublicationResult> ReconcileAppendExceptionAsync(CustomLoopConversationPublicationRequest request, IReadOnlyList<LlmMessage> expectedPrefix, Exception exception)
    {
        using var reconciliation = new CancellationTokenSource(_reconciliationTimeout);
        try
        {
            var persistedConversation = await _conversationMemory.LoadCurrentConversationSnapshotAsync(reconciliation.Token);
            if (!string.Equals(persistedConversation.Version, request.ConversationId, StringComparison.Ordinal))
            {
                return Uncertain(request, $"Conversation append failed with {exception.GetType().Name}, and the durable conversation identity changed before reconciliation.");
            }

            if (MessagesEqual(persistedConversation.Messages, expectedPrefix))
            {
                return DefinitelyFailed(request, $"Conversation append failed with {exception.GetType().Name}, and no append was observed.");
            }

            if (!IsExactOutputShape(persistedConversation.Messages, expectedPrefix, request.CanonicalOutput))
            {
                return Uncertain(request, $"Conversation append failed with {exception.GetType().Name}, and durable state no longer has a provable expected shape.");
            }

            return Uncertain(request, $"Conversation append failed with {exception.GetType().Name}; the expected content exists, but its exact publication identity could not be proven.");
        }
        catch (Exception reconciliationException)
        {
            return Uncertain(request, $"Conversation append failed with {exception.GetType().Name}, and its outcome could not be reconciled: {reconciliationException.GetType().Name}.");
        }
    }

    private async Task<CustomLoopConversationPublicationResult> VerifyPublishedAsync(CustomLoopConversationPublicationRequest request, IReadOnlyList<LlmMessage> expectedPrefix, CancellationToken cancellationToken, bool alreadyPublished)
    {
        try
        {
            var stateMessages = CustomLoopRuntimeContext.GetLogicalConversationMessages(_conversationState);
            var persistedConversation = await _conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
            if (!string.Equals(persistedConversation.Version, request.ConversationId, StringComparison.Ordinal) || !IsExpectedPrefixPlusOutput(stateMessages, request) || !MessagesEqual(stateMessages, persistedConversation.Messages) || !PrefixMatches(stateMessages, expectedPrefix))
            {
                return Uncertain(request, "The append returned, but the exact expected-prefix-plus-one-output state could not be proven.");
            }

            return await CompleteVerifiedPublicationAsync(request, stateMessages.Count(), alreadyPublished, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Uncertain(request, $"The append returned, but post-append state could not be verified: {exception.GetType().Name}.");
        }
    }

    private async Task<CustomLoopConversationPublicationResult> CompleteVerifiedPublicationAsync(
        CustomLoopConversationPublicationRequest request,
        int messageCount,
        bool alreadyPublished,
        CancellationToken cancellationToken)
    {
        var notificationFailed = false;
        if (_observer is not null && TryRememberNotification(request.OperationId))
        {
            try
            {
                await _observer.PublicationCommittedAsync(
                    new AgentRuntimeConversationPublication(
                        request.OperationId,
                        request.RunId,
                        request.LoopId,
                        request.ConversationId,
                        messageCount,
                        alreadyPublished),
                    cancellationToken);
            }
            catch (Exception)
            {
                ForgetNotification(request.OperationId);
                notificationFailed = true;
            }
        }

        var outcome = alreadyPublished ? CustomLoopConversationPublicationOutcome.AlreadyPublished : CustomLoopConversationPublicationOutcome.Published;
        var detail = alreadyPublished ? "The canonical output was already published." : "The canonical output was appended exactly once and verified.";
        if (notificationFailed)
        {
            detail += " The surface notification failed, but the durable conversation publication remains verified and a later replay may retry projection.";
        }

        return new CustomLoopConversationPublicationResult(outcome, request.OperationId, detail);
    }

    private bool TryRememberNotification(string operationId)
    {
        if (_notifiedOperations.ContainsKey(operationId))
        {
            return false;
        }

        var node = _notificationOrder.AddLast(operationId);
        _notifiedOperations.Add(operationId, node);
        while (_notifiedOperations.Count > MaxRememberedNotificationOperations)
        {
            var oldest = _notificationOrder.First!;
            _notificationOrder.RemoveFirst();
            _notifiedOperations.Remove(oldest.Value);
        }

        return true;
    }

    private void ForgetNotification(string operationId)
    {
        if (_notifiedOperations.Remove(operationId, out var node))
        {
            _notificationOrder.Remove(node);
        }
    }

    private static bool IsExpectedPrefixPlusOutput(IReadOnlyList<LlmMessage> messages, CustomLoopConversationPublicationRequest request)
    {
        var expectedSuffix = ExpectedPublicationSuffix(request, includeCurrent: true);
        return MatchesAdmissionPrefixAndSuffix(messages, request.ExpectedConversationVersion, expectedSuffix);
    }

    private static bool MatchesExpectedPublicationPrefix(IReadOnlyList<LlmMessage> messages, CustomLoopConversationPublicationRequest request)
    {
        var expectedSuffix = ExpectedPublicationSuffix(request, includeCurrent: false);
        return MatchesAdmissionPrefixAndSuffix(messages, request.ExpectedConversationVersion, expectedSuffix);
    }

    private static IReadOnlyList<string> ExpectedPublicationSuffix(CustomLoopConversationPublicationRequest request, bool includeCurrent)
    {
        var prior = request.PriorPublications ?? [];
        return includeCurrent
            ? [.. prior.Select(item => item.CanonicalOutput), request.CanonicalOutput]
            : prior.Select(item => item.CanonicalOutput).ToArray();
    }

    private static bool MatchesAdmissionPrefixAndSuffix(IReadOnlyList<LlmMessage> messages, string admissionVersion, IReadOnlyList<string> expectedSuffix)
    {
        if (messages.Count < expectedSuffix.Count)
        {
            return false;
        }

        var prefixCount = messages.Count - expectedSuffix.Count;
        for (var index = 0; index < expectedSuffix.Count; index++)
        {
            var message = messages[prefixCount + index];
            if (message.Role != LlmMessageRole.Assistant || !string.Equals(message.Content, expectedSuffix[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return string.Equals(CustomLoopConversationVersion.Compute(messages.Take(prefixCount).ToArray()), admissionVersion, StringComparison.Ordinal);
    }

    private static bool ValidatePriorPublications(CustomLoopConversationPublicationRequest request, out string detail)
    {
        var prior = request.PriorPublications ?? [];
        if (prior.Count > CustomLoopLimits.MaxConversationPublicationEffectsPerRun)
        {
            detail = "The expected prior-publication suffix exceeded the bounded model-attempt count.";
            return false;
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publication in prior)
        {
            if (publication is null || !CustomLoopArtifactIdentifier.IsValid(publication.OperationId) || !operationIds.Add(publication.OperationId))
            {
                detail = "The expected prior-publication suffix contained an invalid or duplicate operation id.";
                return false;
            }

            if (!MatchesHash(publication.CanonicalOutput, publication.CanonicalOutputHash))
            {
                detail = "The expected prior-publication suffix contained canonical content whose hash did not match.";
                return false;
            }
        }

        if (!CustomLoopArtifactIdentifier.IsValid(request.OperationId) || operationIds.Contains(request.OperationId))
        {
            detail = "The current publication operation id was invalid or already present in the prior-publication suffix.";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    private static bool PrefixMatches(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmMessage> expectedPrefix)
    {
        return messages.Count == expectedPrefix.Count + 1 && MessagesEqual(messages.Take(expectedPrefix.Count).ToArray(), expectedPrefix);
    }

    private static bool IsExactAppendSnapshot(
        ConversationMemorySnapshot snapshot,
        string expectedConversationId,
        string expectedConversationVersion,
        IReadOnlyList<LlmMessage> expectedPrefix,
        string output)
    {
        return string.Equals(snapshot.ConversationId, expectedConversationId, StringComparison.Ordinal)
            && string.Equals(snapshot.Version, expectedConversationVersion, StringComparison.Ordinal)
            && IsExactOutputShape(snapshot.Messages, expectedPrefix, output);
    }

    private static bool IsExactOutputShape(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmMessage> expectedPrefix, string output)
    {
        return PrefixMatches(messages, expectedPrefix)
            && messages[^1].Role == LlmMessageRole.Assistant
            && string.Equals(messages[^1].Content, output, StringComparison.Ordinal);
    }

    private static bool MessagesEqual(IReadOnlyList<LlmMessage> left, IReadOnlyList<LlmMessage> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
    }

    private static bool MatchesHash(string content, string expectedHash)
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static CustomLoopConversationPublicationResult DefinitelyFailed(CustomLoopConversationPublicationRequest request, string detail)
    {
        return new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.DefinitelyFailed, request.OperationId, detail);
    }

    private static CustomLoopConversationPublicationResult Uncertain(CustomLoopConversationPublicationRequest request, string detail)
    {
        return new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Uncertain, request.OperationId, detail);
    }
}
