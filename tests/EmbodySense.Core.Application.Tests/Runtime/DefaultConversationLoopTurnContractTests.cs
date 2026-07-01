using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class DefaultConversationLoopTurnContractTests
{
    [Fact]
    public void Loop_turn_request_requires_input_without_defaulting_context()
    {
        var request = new DefaultConversationLoopTurnRequest("hello");

        Assert.Equal("hello", request.Input);
        Assert.Equal(LlmMessageRole.User, request.ToUserMessage().Role);
        Assert.Throws<ArgumentException>(() => new DefaultConversationLoopTurnRequest(" "));
    }

    [Fact]
    public async Task Loop_turn_request_carries_streaming_diagnostics_and_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<string>();
        var diagnostics = new List<RuntimeDiagnosticMessage>();
        var request = new DefaultConversationLoopTurnRequest(
            "hello",
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
            (message, _) =>
            {
                diagnostics.Add(message);
                return Task.CompletedTask;
            },
            cancellation.Token);

        await request.ResponseChunkHandler!("chunk", CancellationToken.None);
        await request.DiagnosticHandler!(new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.VerboseContext, "visible context"), CancellationToken.None);

        Assert.Equal(cancellation.Token, request.CancellationToken);
        Assert.Equal(["chunk"], chunks);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RuntimeDiagnosticKind.VerboseContext, diagnostic.Kind);
        Assert.Equal("visible context", diagnostic.Content);
    }

    [Fact]
    public void Loop_turn_result_reports_outcome_transcript_and_optional_run_identity()
    {
        var run = new LoopRunIdentity("default-conversation", "run-1");
        var transcript = new[] { new RuntimeTranscriptMessage(LlmMessageRole.Assistant, "done") };

        var completed = DefaultConversationLoopTurnResult.Completed("done", transcript, run);
        var failed = DefaultConversationLoopTurnResult.Failed("provider failed", runIdentity: run);
        var cancelled = DefaultConversationLoopTurnResult.Cancelled("caller cancelled", runIdentity: run);

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, completed.Status);
        Assert.Equal("done", completed.AssistantOutput);
        Assert.Equal(run, completed.RunIdentity);
        Assert.True(completed.UserMessageAccepted);
        Assert.Equal(transcript, completed.TranscriptMessages);
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, failed.Status);
        Assert.Equal("provider failed", failed.FailureDetail);
        Assert.Equal(run, failed.RunIdentity);
        Assert.False(failed.UserMessageAccepted);
        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, cancelled.Status);
        Assert.Equal("caller cancelled", cancelled.FailureDetail);
        Assert.Equal(run, cancelled.RunIdentity);
        Assert.False(cancelled.UserMessageAccepted);
    }

    [Fact]
    public void Runtime_diagnostics_and_results_reject_ambiguous_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.Unknown, "context"));
        Assert.Throws<ArgumentException>(() => new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.Status, " "));
        Assert.Throws<ArgumentException>(() => DefaultConversationLoopTurnResult.Completed(" "));
        Assert.Throws<ArgumentException>(() => DefaultConversationLoopTurnResult.Failed(" "));
    }
}
