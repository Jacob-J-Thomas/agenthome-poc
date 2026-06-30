using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;

namespace EmbodySense.Core.Application.Loops.Execution;

public sealed class DefaultConversationLoopRunner : IDefaultConversationLoopRunner
{
    private readonly ILlmInferenceClient _inferenceClient;
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly ConversationRuntimeState _conversationState;
    private readonly InferenceRequestBuilder _requestBuilder;

    // TODO(default-loop-contract): Revisit whether the default loop runner should own request building, diagnostics, and transcript projection directly.
    // Deferred until the runtime host API settles; revisit when loop execution gains real run identity, audit events, or resumable failure handling.
    public DefaultConversationLoopRunner(
        ILlmInferenceClient inferenceClient,
        ConversationRuntimeState conversationState,
        IConversationMemoryStore? conversationMemoryStore = null,
        InferenceRequestBuilder? requestBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(conversationState);

        _inferenceClient = inferenceClient;
        _conversationState = conversationState;
        _conversationMemoryStore = conversationMemoryStore;
        _requestBuilder = requestBuilder ?? new InferenceRequestBuilder();
    }

    public async Task<DefaultConversationLoopTurnResult> RunTurnAsync(DefaultConversationLoopTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userMessage = request.ToUserMessage();
        var inferenceRequest = _requestBuilder.BuildRequest(_conversationState.Messages, userMessage);

        try
        {
            await EmitVisibleContextAsync(request, inferenceRequest.Messages);
            _conversationState.AppendMessage(userMessage);
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

            return DefaultConversationLoopTurnResult.Completed(
                response.OutputText,
                [
                    new RuntimeTranscriptMessage(userMessage),
                    new RuntimeTranscriptMessage(assistantMessage)
                ]);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            return DefaultConversationLoopTurnResult.Cancelled(
                "Turn was cancelled.",
                [new RuntimeTranscriptMessage(userMessage)]);
        }
        catch (Exception exception)
        {
            return DefaultConversationLoopTurnResult.Failed(
                exception.Message,
                [new RuntimeTranscriptMessage(userMessage)]);
        }
    }

    private static async Task EmitVisibleContextAsync(
        DefaultConversationLoopTurnRequest request,
        IReadOnlyList<LlmMessage> messages)
    {
        if (request.DiagnosticHandler is null)
        {
            return;
        }

        // TODO(runtime-diagnostics): Move visible-context formatting out of RuntimeCommandOutput so loop diagnostics are not coupled to slash-command text.
        // Deferred because the current text is already shared by Web and CLI; revisit when diagnostics become typed runtime events instead of formatted strings.
        var content = RuntimeCommandOutput.FormatVerboseContext(messages);
        await request.DiagnosticHandler(new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.VerboseContext, content), request.CancellationToken);
    }
}
