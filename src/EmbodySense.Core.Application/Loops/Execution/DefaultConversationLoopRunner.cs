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
    // TODO(default-loop-execution-graph): This is still a linear system default loop runner, not the generic graph executor
    // needed for editable/user-authored loops. Revisit when loop definitions gain nodes, edges, locked system steps,
    // and agent-authored loop artifacts.
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
        var inferenceMessages = _conversationState.Messages.Concat([userMessage]).ToArray();
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

            await EmitVisibleContextAsync(request, inferenceMessages);
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

    private static async Task EmitVisibleContextAsync(
        DefaultConversationLoopTurnRequest request,
        IReadOnlyList<LlmMessage> messages)
    {
        if (request.DiagnosticHandler is null)
        {
            return;
        }

        var content = RuntimeDiagnosticFormatter.FormatVerboseContext(messages);
        await request.DiagnosticHandler(new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.VerboseContext, content), request.CancellationToken);
    }
}
