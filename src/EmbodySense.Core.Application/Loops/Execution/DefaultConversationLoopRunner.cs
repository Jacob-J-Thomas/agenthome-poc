using System.Globalization;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Diagnostics;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

public sealed class DefaultConversationLoopRunner : IDefaultConversationLoopRunner
{
    private readonly ILlmInferenceClient _inferenceClient;
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly ConversationRuntimeState _conversationState;
    private readonly LoopDefinition _loopDefinition;
    private readonly ILoopRunStore? _loopRunStore;
    private readonly RuntimeSurfaceId _surface;

    public DefaultConversationLoopRunner(
        ILlmInferenceClient inferenceClient,
        ConversationRuntimeState conversationState,
        IConversationMemoryStore? conversationMemoryStore = null,
        LoopDefinition? loopDefinition = null,
        ILoopRunStore? loopRunStore = null,
        RuntimeSurfaceId? surface = null)
    {
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(conversationState);

        _inferenceClient = inferenceClient;
        _conversationState = conversationState;
        _conversationMemoryStore = conversationMemoryStore;
        _loopDefinition = loopDefinition ?? LoopDefinition.CreateDefaultConversation();
        _loopRunStore = loopRunStore;
        _surface = surface ?? RuntimeSurfaceId.Runtime;
    }

    public async Task<DefaultConversationLoopTurnResult> RunTurnAsync(DefaultConversationLoopTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userMessage = request.ToUserMessage();
        var inferenceContextMessages = _conversationState.ContextMessages
            .Concat([new RuntimeContextMessage(userMessage, RuntimeContextSource.CurrentTurnInput, "Current user input being evaluated by the active loop before provider dispatch.")])
            .ToArray();
        var inferenceMessages = inferenceContextMessages.Select(message => message.Message).ToArray();
        var inferenceRequest = new LlmInferenceRequest(inferenceMessages);
        var runId = CreateRunId();
        var runIdentity = new LoopRunIdentity(_loopDefinition.Id, runId, _loopDefinition.RoleId);
        var run = LoopRunRecord.Started(
            runId,
            _loopDefinition.Id,
            _loopDefinition.RoleId,
            _surface,
            _loopDefinition.Trigger,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["loopDisplayName"] = _loopDefinition.DisplayName,
                ["loopEditMode"] = _loopDefinition.EditMode.ToString(),
                ["graphEntryNodeId"] = _loopDefinition.Graph?.EntryNodeId ?? "",
                ["graphNodeCount"] = (_loopDefinition.Graph?.Nodes?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                ["reviewPolicy"] = _loopDefinition.ReviewPolicy.ToString(),
                ["failurePolicy"] = _loopDefinition.FailurePolicy.ToString()
            });
        var userMessageAccepted = false;

        try
        {
            var startSaveFailure = await TrySaveRunAsync(run, CancellationToken.None);
            if (startSaveFailure is not null)
            {
                return DefaultConversationLoopTurnResult.Failed($"Could not record loop run start: {startSaveFailure}", runIdentity: runIdentity);
            }

            if (_loopDefinition.State != LoopState.Enabled)
            {
                var detail = $"Loop `{_loopDefinition.Id}` is not enabled.";
                var saveFailure = await TrySaveRunAsync(run.Fail(DateTimeOffset.UtcNow, detail), CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(IncludeRunPersistenceFailure(detail, saveFailure), runIdentity: runIdentity);
            }

            var graphExecutionBlocker = DefaultConversationLoopGraphContract.GetExecutionBlocker(_loopDefinition);
            if (graphExecutionBlocker is not null)
            {
                var saveFailure = await TrySaveRunAsync(run.Fail(DateTimeOffset.UtcNow, graphExecutionBlocker), CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(IncludeRunPersistenceFailure(graphExecutionBlocker, saveFailure), runIdentity: runIdentity);
            }

            await EmitVisibleContextAsync(request, runIdentity, inferenceContextMessages);
            _conversationState.AppendMessage(userMessage);
            userMessageAccepted = true;
            if (_conversationMemoryStore is not null)
            {
                await _conversationMemoryStore.AppendMessageAsync(userMessage, request.CancellationToken);
            }

            var response = await _inferenceClient.GenerateAsync(inferenceRequest, request.ResponseChunkHandler, request.CancellationToken);
            var assistantMessage = LlmMessage.Assistant(response.OutputText);
            _conversationState.AppendMessage(assistantMessage);
            if (_conversationMemoryStore is not null)
            {
                await _conversationMemoryStore.AppendMessageAsync(assistantMessage, request.CancellationToken);
            }

            // TODO(loop-run-status-durability): Completion status persistence is best-effort after the assistant response is already
            // accepted into state and memory. Revisit with a transactional outbox or retry model before loop replay/resume relies on it.
            _ = await TrySaveRunAsync(run.Complete(DateTimeOffset.UtcNow), CancellationToken.None);
            return DefaultConversationLoopTurnResult.Completed(
                response.OutputText,
                [
                    new RuntimeTranscriptMessage(userMessage),
                    new RuntimeTranscriptMessage(assistantMessage)
                ],
                runIdentity);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            const string detail = "Turn was cancelled.";
            var saveFailure = await TrySaveRunAsync(run.Cancel(DateTimeOffset.UtcNow, detail), CancellationToken.None);
            return DefaultConversationLoopTurnResult.Cancelled(
                IncludeRunPersistenceFailure(detail, saveFailure),
                userMessageAccepted ? [new RuntimeTranscriptMessage(userMessage)] : [],
                runIdentity,
                userMessageAccepted);
        }
        catch (Exception exception)
        {
            var saveFailure = await TrySaveRunAsync(run.Fail(DateTimeOffset.UtcNow, exception.Message), CancellationToken.None);
            return DefaultConversationLoopTurnResult.Failed(
                IncludeRunPersistenceFailure(exception.Message, saveFailure),
                userMessageAccepted ? [new RuntimeTranscriptMessage(userMessage)] : [],
                runIdentity,
                userMessageAccepted);
        }
    }

    private async Task<string?> TrySaveRunAsync(LoopRunRecord run, CancellationToken cancellationToken)
    {
        if (_loopRunStore is null)
        {
            return null;
        }

        try
        {
            await _loopRunStore.SaveAsync(run, cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private static string IncludeRunPersistenceFailure(string detail, string? saveFailure)
    {
        return saveFailure is null
            ? detail
            : $"{detail} Loop run persistence also failed: {saveFailure}";
    }

    private static string CreateRunId()
    {
        return "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    private async Task EmitVisibleContextAsync(
        DefaultConversationLoopTurnRequest request,
        LoopRunIdentity runIdentity,
        IReadOnlyList<RuntimeContextMessage> messages)
    {
        if (request.DiagnosticHandler is null)
        {
            return;
        }

        var content = RuntimeDiagnosticFormatter.FormatVerboseContext(new RuntimeVerboseContext(
            _loopDefinition,
            runIdentity,
            _surface,
            messages,
            CreateContextOmissions(messages),
            "No compaction engine or compaction artifact is active in the default conversation loop yet."));
        await request.DiagnosticHandler(new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.VerboseContext, content, "Visible inference context"), request.CancellationToken);
    }

    private static IReadOnlyList<RuntimeContextOmission> CreateContextOmissions(IReadOnlyList<RuntimeContextMessage> messages)
    {
        var omissions = new List<RuntimeContextOmission>
        {
            new(
                "provider-adapter",
                "provider-formatting",
                "Codex app-server formatting can wrap restored context as lower-authority material and omit older restored messages when its adapter budget is exceeded; this diagnostic is emitted before that provider adapter formatting."),
            new(
                "higher-order-memory",
                "runtime-context-assembly",
                "No higher-order memory retrieval or consolidation artifact is active in this default loop path yet.")
        };

        omissions.Add(GetLocalMemoryStatus(messages));

        if (messages.Any(message => message.Message.Content.Contains("[truncated after", StringComparison.OrdinalIgnoreCase) || message.Message.Content.Contains("[truncated]", StringComparison.OrdinalIgnoreCase)))
        {
            omissions.Add(new RuntimeContextOmission(
                "startup-or-restored-context",
                "runtime-context-assembly",
                "At least one context message reports in-band truncation from its source reader."));
        }

        return omissions;
    }

    private static RuntimeContextOmission GetLocalMemoryStatus(IReadOnlyList<RuntimeContextMessage> messages)
    {
        var startupContent = string.Join(Environment.NewLine, messages.Where(message => message.Source == RuntimeContextSource.StartupContext).Select(message => message.Message.Content));
        if (startupContent.Contains("## .agent/MEMORY.md", StringComparison.Ordinal))
        {
            return new RuntimeContextOmission(
                "local-memory",
                "runtime-context-assembly",
                ".agent/MEMORY.md is present in the active startup context.");
        }

        return new RuntimeContextOmission(
            "local-memory",
            "runtime-context-assembly",
            ".agent/MEMORY.md is not included in the active startup context; it may be missing, empty, or not loaded in this workspace.");
    }
}
