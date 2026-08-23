using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal static class DefaultConversationTurnProcessLossHost
{
    private const int ProcessLossExitCode = 173;

    internal static async Task<int> RunAsync(string workspaceRoot, string boundaryText)
    {
        if (!Enum.TryParse<DefaultConversationTurnBoundary>(boundaryText, out var boundary)
            || boundary == DefaultConversationTurnBoundary.Unknown)
        {
            return 2;
        }

        var paths = new WorkspacePaths(workspaceRoot);
        var memory = new ConversationMemoryStore(paths);
        var turns = new DefaultConversationTurnStore(paths);
        var runs = new LoopRunStore(paths);
        var state = new ConversationRuntimeState(workspaceLease: new FileConversationWorkspaceLease(paths));
        var runner = new DefaultConversationLoopRunner(
            new FixedInferenceClient("answer"),
            state,
            memory,
            LoopDefinition.CreateDefaultConversation(),
            runs,
            RuntimeSurfaceId.Web,
            turns,
            new ExitingFailpoint(boundary),
            new TestCapabilityAdmissionService());

        _ = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: "process-loss-request"));
        return 4;
    }

    private sealed class FixedInferenceClient(string output) : ILlmInferenceClient
    {
        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (responseChunkHandler is not null)
            {
                await responseChunkHandler(output, cancellationToken);
            }

            return new LlmInferenceResponse(
                output,
                LlmInferenceSurface.OpenAiCodex,
                EmbodySense.Core.Common.Inference.Profiles.Models.LlmInferenceUsageEvidence.Unavailable("test", "v1"),
                "test-model",
                "provider-response-1");
        }

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler,
            CancellationToken cancellationToken,
            InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        {
            ArgumentNullException.ThrowIfNull(providerTransportCommitBoundary);
            var writes = 0;
            await providerTransportCommitBoundary(
                _ =>
                {
                    if (Interlocked.Increment(ref writes) != 1)
                    {
                        throw new InvalidOperationException("Provider transport write committed more than once.");
                    }

                    return Task.CompletedTask;
                },
                cancellationToken);
            if (writes != 1)
            {
                throw new InvalidOperationException("Provider transport write was not committed.");
            }

            return await GenerateAsync(request, responseChunkHandler, cancellationToken);
        }
    }

    private sealed class ExitingFailpoint(DefaultConversationTurnBoundary boundary) : IDefaultConversationTurnFailpoint
    {
        public Task AfterBoundaryAsync(
            DefaultConversationTurnBoundary currentBoundary,
            DefaultConversationTurnRecord record,
            CancellationToken cancellationToken = default)
        {
            _ = record;
            cancellationToken.ThrowIfCancellationRequested();
            if (currentBoundary == boundary)
            {
                Console.Error.WriteLine($"The test host process crashed after `{currentBoundary}`.");
                Console.Error.Flush();
                Environment.Exit(ProcessLossExitCode);
            }

            return Task.CompletedTask;
        }
    }
}
