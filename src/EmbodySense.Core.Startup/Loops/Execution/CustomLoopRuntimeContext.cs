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

internal sealed class CustomLoopRuntimeContext
{
    private const int MaxDirectoryRoleSourceCharacters = 12_000;
    private readonly WorkspacePaths _paths;
    private readonly ConversationRuntimeState _conversationState;
    private readonly IConversationMemoryStore _conversationMemory;
    private readonly WorkspaceContextStore _workspaceContextStore;
    private readonly TimeProvider _timeProvider;

    public CustomLoopRuntimeContext(WorkspacePaths paths, ConversationRuntimeState conversationState, IConversationMemoryStore conversationMemory, WorkspaceContextStore? workspaceContextStore = null, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));
        _conversationMemory = conversationMemory ?? throw new ArgumentNullException(nameof(conversationMemory));
        _workspaceContextStore = workspaceContextStore ?? new WorkspaceContextStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CustomLoopRuntimeContextCapture> CaptureAsync(bool includeInvokingConversation, CancellationToken cancellationToken)
    {
        var capturedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var documents = await _workspaceContextStore.LoadDocumentsAsync(_paths, cancellationToken);
        ConversationMemorySnapshot persistedConversation;
        LlmMessage[] logicalMessages;
        using (await _conversationState.AcquireExclusiveAccessAsync(cancellationToken))
        {
            persistedConversation = await _conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
            if (!_conversationState.TrySynchronizeConversationTranscript(persistedConversation.Messages))
            {
                throw new InvalidOperationException("The active logical conversation diverged from durable workspace state; custom-loop context was not captured.");
            }

            logicalMessages = GetLogicalConversationMessages(_conversationState);
        }
        var conversationVersion = CustomLoopConversationVersion.Compute(logicalMessages);
        var manifest = documents
            .Select((document, index) => CreateWorkspaceSource(document, capturedAtUtc, index + 1))
            .Concat(CreateConversationSources(logicalMessages, includeInvokingConversation, capturedAtUtc, documents.Count + 1))
            .ToArray();
        var snapshot = new CustomLoopContextSnapshot(CustomLoopContextSnapshot.CurrentSchemaVersion, capturedAtUtc, manifest, string.Empty);

        return new CustomLoopRuntimeContextCapture(
            CustomLoopContextSnapshotHash.Apply(snapshot),
            new CustomLoopConversationReference(persistedConversation.Version, conversationVersion, capturedAtUtc));
    }

    internal static LlmMessage[] GetLogicalConversationMessages(ConversationRuntimeState conversationState)
    {
        ArgumentNullException.ThrowIfNull(conversationState);
        return conversationState.ContextMessages
            .Where(message => message.Source is RuntimeContextSource.RestoredConversationHistory or RuntimeContextSource.SessionTranscript)
            .Select(message => message.Message)
            .ToArray();
    }

    private static CustomLoopContextManifestSource CreateWorkspaceSource(WorkspaceContextDocument document, DateTimeOffset capturedAtUtc, int order)
    {
        var sourceType = document.Kind switch
        {
            WorkspaceContextDocumentKind.RoleInstruction => CustomLoopContextSource.RoleInstruction,
            WorkspaceContextDocumentKind.IdentityInstruction => CustomLoopContextSource.RoleInstruction,
            WorkspaceContextDocumentKind.ContextualState => CustomLoopContextSource.ContextualState,
            _ => throw new InvalidOperationException($"Workspace context source `{document.SourceId}` has no supported trust classification.")
        };
        var provenance = document.Kind switch
        {
            WorkspaceContextDocumentKind.RoleInstruction => CustomLoopContextProvenance.WorkspaceRoleFile,
            WorkspaceContextDocumentKind.IdentityInstruction => CustomLoopContextProvenance.AgentIdentityFile,
            _ => CustomLoopContextProvenance.WorkspaceContextFile
        };
        var trustClass = document.Kind is WorkspaceContextDocumentKind.RoleInstruction or WorkspaceContextDocumentKind.IdentityInstruction
            ? CustomLoopContextTrustClass.TrustedInstruction
            : CustomLoopContextTrustClass.UntrustedData;
        if (document.OmissionReason is not null)
        {
            return CreateManifestSource(order, sourceType, document.SourceId, document.ExactPath, provenance, trustClass, string.Empty, document.OriginalCharacterCount, false, null, document.OmissionReason, capturedAtUtc);
        }

        var label = document.Kind switch
        {
            WorkspaceContextDocumentKind.RoleInstruction => $"[EmbodySense role instruction source: {document.DisplayPath}]{Environment.NewLine}",
            WorkspaceContextDocumentKind.IdentityInstruction => $"[EmbodySense durable identity instruction source: {document.DisplayPath}]{Environment.NewLine}",
            _ => $"[EmbodySense untrusted contextual state source: {document.DisplayPath}]{Environment.NewLine}"
        };
        var fullContent = label + document.Content;
        if (fullContent.Length <= MaxDirectoryRoleSourceCharacters)
        {
            return CreateManifestSource(order, sourceType, document.SourceId, document.ExactPath, provenance, trustClass, fullContent, fullContent.Length, false, null, null, capturedAtUtc);
        }

        var marker = $"{Environment.NewLine}[source truncated to fit the {MaxDirectoryRoleSourceCharacters}-character admitted source limit]";
        var availableSourceCharacters = MaxDirectoryRoleSourceCharacters - label.Length - marker.Length;
        var safeSourceCharacters = SafePrefixLength(document.Content, availableSourceCharacters);
        var content = label + document.Content[..safeSourceCharacters] + marker;
        return CreateManifestSource(order, sourceType, document.SourceId, document.ExactPath, provenance, trustClass, content, fullContent.Length, true, $"Source exceeded the {MaxDirectoryRoleSourceCharacters}-character per-source admission limit.", null, capturedAtUtc);
    }

    private static CustomLoopContextManifestSource[] CreateConversationSources(IReadOnlyList<LlmMessage> logicalMessages, bool admitted, DateTimeOffset capturedAtUtc, int firstOrder)
    {
        if (logicalMessages.Count == 0)
        {
            return
            [
                CreateManifestSource(firstOrder, CustomLoopContextSource.InvokingConversation, "invoking-conversation", "conversation/current", CustomLoopContextProvenance.LogicalConversation, CustomLoopContextTrustClass.UntrustedData, string.Empty, 0, false, null, "No logical invoking-conversation message existed at admission.", capturedAtUtc)
            ];
        }

        var selected = new Dictionary<int, (string Content, int OriginalCharacters, bool Truncated, string? TruncationReason)>();
        var usedCharacters = 0;
        if (admitted)
        {
            SelectConversationContent(logicalMessages, selected, ref usedCharacters);
        }

        var sources = new List<CustomLoopContextManifestSource>(Math.Min(logicalMessages.Count, CustomLoopLimits.MaxInvokingConversationEntries) + 1);
        var order = firstOrder;
        for (var index = 0; index < logicalMessages.Count; index++)
        {
            if (!selected.TryGetValue(index, out var capture))
            {
                continue;
            }

            sources.Add(CreateManifestSource(order++, CustomLoopContextSource.InvokingConversation, $"invoking-conversation-{index + 1}", $"conversation/current/messages/{index + 1}", CustomLoopContextProvenance.LogicalConversation, CustomLoopContextTrustClass.UntrustedData, capture.Content, capture.OriginalCharacters, capture.Truncated, capture.TruncationReason, null, capturedAtUtc));
        }

        var omittedCount = logicalMessages.Count - selected.Count;
        if (omittedCount > 0)
        {
            var omittedCharacters = logicalMessages.Select(FormatConversationMessage).Where((_, index) => !selected.ContainsKey(index)).Sum(content => (long)content.Length);
            var boundedOriginalCharacters = (int)Math.Min(int.MaxValue, omittedCharacters);
            var omissionReason = admitted
                ? $"{omittedCount} older logical conversation message(s) were omitted by the {CustomLoopLimits.MaxInvokingConversationEntries}-entry and {CustomLoopLimits.MaxInvokingConversationCharacters}-character snapshot limits."
                : $"Trigger policy did not admit {omittedCount} logical invoking-conversation message(s).";
            sources.Add(CreateManifestSource(order, CustomLoopContextSource.InvokingConversation, "invoking-conversation-omitted", "conversation/current/messages/omitted", CustomLoopContextProvenance.LogicalConversation, CustomLoopContextTrustClass.UntrustedData, string.Empty, boundedOriginalCharacters, false, null, omissionReason, capturedAtUtc));
        }

        return sources.ToArray();
    }

    private static void SelectConversationContent(
        IReadOnlyList<LlmMessage> logicalMessages,
        Dictionary<int, (string Content, int OriginalCharacters, bool Truncated, string? TruncationReason)> selected,
        ref int usedCharacters)
    {
        for (var index = logicalMessages.Count - 1; index >= 0; index--)
        {
            if (selected.Count >= CustomLoopLimits.MaxInvokingConversationEntries)
            {
                break;
            }

            var message = logicalMessages[index];
            var formatted = FormatConversationMessage(message, index);
            if (usedCharacters + formatted.Length <= CustomLoopLimits.MaxInvokingConversationCharacters)
            {
                selected[index] = (formatted, formatted.Length, false, null);
                usedCharacters += formatted.Length;
                continue;
            }

            if (usedCharacters == 0)
            {
                var truncated = TruncateConversationMessage(formatted);
                selected[index] = (truncated, formatted.Length, true, $"Message exceeded the {CustomLoopLimits.MaxInvokingConversationCharacters}-character invoking-conversation snapshot limit.");
                usedCharacters = truncated.Length;
            }

            break;
        }
    }

    private static string FormatConversationMessage(LlmMessage message, int index)
    {
        return $"[EmbodySense untrusted logical conversation {message.Role.ToString().ToLowerInvariant()} source: message {index + 1}]{Environment.NewLine}{message.Content}";
    }

    private static string TruncateConversationMessage(string content)
    {
        var marker = $"{Environment.NewLine}[truncated to {CustomLoopLimits.MaxInvokingConversationCharacters} characters for invoking-conversation admission]{Environment.NewLine}";
        var available = CustomLoopLimits.MaxInvokingConversationCharacters - marker.Length;
        var headCharacters = available / 2;
        var tailCharacters = available - headCharacters;
        var safeHeadCharacters = SafePrefixLength(content, headCharacters);
        var safeTailStart = SafeSuffixStart(content, content.Length - tailCharacters);
        return content[..safeHeadCharacters] + marker + content[safeTailStart..];
    }

    private static int SafePrefixLength(string value, int maximumCharacters)
    {
        var length = Math.Min(value.Length, maximumCharacters);
        return length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]) ? length - 1 : length;
    }

    private static int SafeSuffixStart(string value, int minimumStart)
    {
        var start = Math.Clamp(minimumStart, 0, value.Length);
        return start > 0 && start < value.Length && char.IsHighSurrogate(value[start - 1]) && char.IsLowSurrogate(value[start]) ? start + 1 : start;
    }

    private static CustomLoopContextManifestSource CreateManifestSource(
        int order,
        CustomLoopContextSource sourceType,
        string sourceId,
        string sourcePath,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        string content,
        int originalCharacterCount,
        bool truncated,
        string? truncationReason,
        string? omissionReason,
        DateTimeOffset capturedAtUtc)
    {
        return new CustomLoopContextManifestSource(
            order,
            sourceType,
            sourceId,
            sourcePath,
            provenance,
            trustClass,
            sourceType == CustomLoopContextSource.RoleInstruction ? LlmMessageRole.System : LlmMessageRole.User,
            content,
            CustomLoopTraceContentHash.Compute(content),
            originalCharacterCount,
            content.Length,
            truncated,
            truncationReason,
            omissionReason,
            capturedAtUtc);
    }
}
