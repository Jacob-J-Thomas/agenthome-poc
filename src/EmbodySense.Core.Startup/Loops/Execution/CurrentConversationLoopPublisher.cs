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

namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CurrentConversationLoopPublisher : ICustomLoopConversationPublisher
{
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);
    private readonly ConversationRuntimeState _conversationState;
    private readonly IConversationMemoryStore _conversationMemory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CurrentConversationLoopPublisher(ConversationRuntimeState conversationState, IConversationMemoryStore conversationMemory)
    {
        _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));
        _conversationMemory = conversationMemory ?? throw new ArgumentNullException(nameof(conversationMemory));
    }

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

            var stateMessages = CustomLoopRuntimeContext.GetLogicalConversationMessages(_conversationState);
            if (IsExpectedPrefixPlusOutput(stateMessages, request))
            {
                return await ReconcileAlreadyPublishedAsync(request, stateMessages, cancellationToken);
            }

            if (!MatchesExpectedPublicationPrefix(stateMessages, request))
            {
                return DefinitelyFailed(request, "The invoking conversation did not equal the immutable admission prefix plus this run's exact prior publications; publication was not attempted.");
            }

            if (!MessagesEqual(persistedConversation.Messages, stateMessages))
            {
                return DefinitelyFailed(request, "The persisted conversation and active logical conversation differed before publication.");
            }

            try
            {
                request.AppendStarted?.Invoke();
                var appended = await _conversationMemory.TryAppendMessageAsync(persistedConversation.ConversationId, request.ConversationId, stateMessages, LlmMessage.Assistant(request.CanonicalOutput), cancellationToken);
                if (!appended)
                {
                    return DefinitelyFailed(request, "The persisted invoking conversation changed at the atomic publication boundary; no custom-loop output was appended.");
                }
            }
            catch (Exception exception)
            {
                return await ReconcileAppendExceptionAsync(request, stateMessages, exception);
            }

            try
            {
                _conversationState.AppendMessage(LlmMessage.Assistant(request.CanonicalOutput));
            }
            catch (Exception exception)
            {
                return Uncertain(request, $"The durable append may have succeeded, but active conversation projection failed: {exception.GetType().Name}.");
            }

            return await VerifyPublishedAsync(request, stateMessages, cancellationToken, alreadyPublished: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CustomLoopConversationPublicationResult> ReconcileAlreadyPublishedAsync(CustomLoopConversationPublicationRequest request, IReadOnlyList<LlmMessage> stateMessages, CancellationToken cancellationToken)
    {
        try
        {
            var persistedConversation = await _conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
            return string.Equals(persistedConversation.Version, request.ConversationId, StringComparison.Ordinal) && MessagesEqual(persistedConversation.Messages, stateMessages)
                ? new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.AlreadyPublished, request.OperationId, "The exact expected conversation prefix plus one canonical assistant output was already committed.")
                : Uncertain(request, "The active conversation contains the canonical output, but durable conversation identity or state does not match it.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Uncertain(request, $"The active conversation contains the canonical output, but durable state could not be verified: {exception.GetType().Name}.");
        }
    }

    private async Task<CustomLoopConversationPublicationResult> ReconcileAppendExceptionAsync(CustomLoopConversationPublicationRequest request, IReadOnlyList<LlmMessage> expectedPrefix, Exception exception)
    {
        using var reconciliation = new CancellationTokenSource(ReconciliationTimeout);
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

            if (!IsExpectedPrefixPlusOutput(persistedConversation.Messages, request))
            {
                return Uncertain(request, $"Conversation append failed with {exception.GetType().Name}, and durable state no longer has a provable expected shape.");
            }

            _conversationState.AppendMessage(LlmMessage.Assistant(request.CanonicalOutput));
            return await VerifyPublishedAsync(request, expectedPrefix, reconciliation.Token, alreadyPublished: false);
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

            var outcome = alreadyPublished ? CustomLoopConversationPublicationOutcome.AlreadyPublished : CustomLoopConversationPublicationOutcome.Published;
            return new CustomLoopConversationPublicationResult(outcome, request.OperationId, alreadyPublished ? "The canonical output was already published." : "The canonical output was appended exactly once and verified.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Uncertain(request, $"The append returned, but post-append state could not be verified: {exception.GetType().Name}.");
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
