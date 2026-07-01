using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Common.Runtime.Models;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class DefaultConversationLoopRunnerTests
{
    [Fact]
    public async Task RunTurnAsync_builds_request_persists_messages_and_emits_visible_context()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);
        var chunks = new List<string>();
        var diagnostics = new List<RuntimeDiagnosticMessage>();

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest(
            "hello",
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
        Assert.True(result.UserMessageAccepted);
        Assert.NotNull(result.RunIdentity);
        Assert.Equal("default-conversation", result.RunIdentity.LoopId);
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
        Assert.Equal("Visible inference context", diagnostic.Title);
        Assert.Contains("loop_id: default-conversation", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("role_id: default-assistant", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("surface: web", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("edit_mode: SystemLocked", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("graph_entry_node: accept-user-message", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("graph_nodes:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("capability_ids:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("workspace_commands_allowed_by_loop:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("compaction:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("provider-adapter", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains(".agent/MEMORY.md is not included", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("source=startup-context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("source=current-turn-input", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("startup context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("hello", diagnostic.Content, StringComparison.Ordinal);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Completed, run.Status));
        Assert.All(runs.Saved, run =>
        {
            Assert.Equal("default-conversation", run.LoopId);
            Assert.Equal("default-assistant", run.RoleId);
            Assert.Equal("web", run.Surface);
            Assert.Equal("SystemLocked", run.Metadata["loopEditMode"]);
            Assert.Equal(DefaultConversationLoopGraphIds.AcceptUserMessage, run.Metadata["graphEntryNodeId"]);
            Assert.Equal("5", run.Metadata["graphNodeCount"]);
        });
    }

    [Fact]
    public async Task RunTurnAsync_verbose_context_reports_memory_loaded_and_in_band_truncation()
    {
        var client = new RecordingInferenceClient("completed response");
        var state = new ConversationRuntimeState([LlmMessage.System("## .agent/MEMORY.md" + Environment.NewLine + "memory note" + Environment.NewLine + "[truncated]")]);
        var runner = new DefaultConversationLoopRunner(client, state, loopDefinition: LoopDefinition.CreateDefaultConversation(), surface: RuntimeSurfaceId.Web);
        var diagnostics = new List<RuntimeDiagnosticMessage>();

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest(
            "hello",
            diagnosticHandler: (diagnostic, _) =>
            {
                diagnostics.Add(diagnostic);
                return Task.CompletedTask;
            }));

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains(".agent/MEMORY.md is present in the active startup context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("in-band truncation", diagnostic.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurnAsync_records_failed_result_without_completed_assistant_message()
    {
        var client = new RecordingInferenceClient("unused") { Failure = new InvalidOperationException("provider failed") };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Equal("provider failed", result.FailureDetail);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content));
        var message = Assert.Single(memory.Messages);
        Assert.Equal("hello", message.Content);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_records_cancelled_result_without_completed_assistant_message()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = new RecordingInferenceClient("unused") { Failure = new OperationCanceledException(cancellation.Token) };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", cancellationToken: cancellation.Token));

        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, result.Status);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(state.Messages, message => Assert.Equal("hello", message.Content));
        var message = Assert.Single(memory.Messages);
        Assert.Equal("hello", message.Content);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Cancelled, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_rejects_disabled_loop_without_accepting_user_message()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var disabledLoop = LoopDefinition.CreateDefaultConversation() with { State = LoopState.Disabled };
        var runner = new DefaultConversationLoopRunner(client, state, memory, disabledLoop, runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Equal("Loop `default-conversation` is not enabled.", result.FailureDetail);
        Assert.Empty(client.Requests);
        Assert.Empty(state.Messages);
        Assert.Empty(memory.Messages);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_rejects_graph_shapes_the_default_runner_does_not_execute()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var graph = LoopGraphDefinition.CreateDefaultConversation();
        var extendedLoop = LoopDefinition.CreateDefaultConversation() with
        {
            Graph = graph with
            {
                Nodes = graph.Nodes.Concat(
                [
                    new LoopGraphNodeDefinition(
                        "future-hook",
                        "Future hook",
                        "A future hook node that the current default conversation runner must not silently ignore.",
                        LoopGraphNodeKind.ToolActuation,
                        LoopGraphNodeEditMode.UserEditable,
                        [])
                ]).ToArray()
            }
        };
        var runner = new DefaultConversationLoopRunner(client, state, memory, extendedLoop, runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Contains("does not execute yet", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Empty(state.Messages);
        Assert.Empty(memory.Messages);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_returns_failed_result_when_started_run_cannot_be_recorded()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore { FailureAtSaveNumber = 1 };
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Contains("Could not record loop run start", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Collection(state.Messages, message => Assert.Equal("startup context", message.Content));
        Assert.Empty(memory.Messages);
    }

    [Fact]
    public async Task RunTurnAsync_returns_failed_result_when_terminal_run_status_cannot_be_recorded()
    {
        var client = new RecordingInferenceClient("unused") { Failure = new InvalidOperationException("provider failed") };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore { FailureAtSaveNumber = 2 };
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.True(result.UserMessageAccepted);
        Assert.Contains("provider failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("Loop run persistence also failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_still_returns_completed_when_completed_run_status_cannot_be_recorded()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore { FailureAtSaveNumber = 2 };
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        Assert.Equal("completed response", result.AssistantOutput);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status));
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

    private sealed class RecordingLoopRunStore : ILoopRunStore
    {
        public List<LoopRunRecord> Saved { get; } = [];

        public int? FailureAtSaveNumber { get; init; }

        public Task SaveAsync(LoopRunRecord run, CancellationToken cancellationToken = default)
        {
            if (FailureAtSaveNumber == Saved.Count + 1)
            {
                throw new IOException("run store failed");
            }

            Saved.Add(run);
            return Task.CompletedTask;
        }

        public Task<LoopRunRecord?> LoadAsync(string loopId, string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LoopRunRecord?>(null);
        }

        public Task<IReadOnlyList<LoopRunRecord>> ListAsync(string loopId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LoopRunRecord>>([]);
        }
    }
}
