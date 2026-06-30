using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class DefaultConversationLoopRunnerTests
{
    [Fact]
    public async Task RunTurnAsync_builds_request_persists_messages_and_emits_visible_context()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory);
        var chunks = new List<string>();
        var diagnostics = new List<RuntimeDiagnosticMessage>();

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest(
            "hello",
            RuntimeSurface.Web,
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
            (diagnostic, _) =>
            {
                diagnostics.Add(diagnostic);
                return Task.CompletedTask;
            }));

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        Assert.Equal("completed response", result.AssistantOutput);
        Assert.Equal(["completed response"], chunks);
        Assert.Collection(
            Assert.Single(client.Requests),
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content));
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RuntimeDiagnosticKind.VerboseContext, diagnostic.Kind);
        Assert.Contains("startup context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("hello", diagnostic.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurnAsync_records_failed_result_without_completed_assistant_message()
    {
        var client = new RecordingInferenceClient("unused") { Failure = new InvalidOperationException("provider failed") };
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", RuntimeSurface.Web));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Equal("provider failed", result.FailureDetail);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content));
        var message = Assert.Single(memory.Messages);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public async Task RunTurnAsync_records_cancelled_result_without_completed_assistant_message()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = new RecordingInferenceClient("unused") { Failure = new OperationCanceledException(cancellation.Token) };
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", RuntimeSurface.Web, cancellationToken: cancellation.Token));

        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, result.Status);
        Assert.Collection(state.Messages, message => Assert.Equal("hello", message.Content));
        var message = Assert.Single(memory.Messages);
        Assert.Equal("hello", message.Content);
    }

    private sealed class RecordingInferenceClient(string output) : ILlmInferenceClient
    {
        public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToArray());
            if (Failure is not null)
            {
                throw Failure;
            }

            if (responseChunkHandler is not null)
            {
                await responseChunkHandler(output, cancellationToken);
            }

            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex);
        }
    }

    private sealed class RecordingConversationMemoryStore : IConversationMemoryStore
    {
        public List<LlmMessage> Messages { get; } = [];

        public Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlmMessage>>(Messages);
        }

        public Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationTranscriptListItem>>([]);
        }

        public Task StartFreshConversationAsync(CancellationToken cancellationToken = default)
        {
            Messages.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlmMessage>>([]);
        }

        public Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationMemorySearchResult>>([]);
        }
    }
}
