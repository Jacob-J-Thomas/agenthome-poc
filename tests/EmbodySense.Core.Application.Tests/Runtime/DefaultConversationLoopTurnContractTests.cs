using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Runtime;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class DefaultConversationLoopTurnContractTests
{
    [Fact]
    public void Runtime_surface_requires_explicit_safe_identifier()
    {
        var web = RuntimeSurface.Create(" web ");
        var custom = RuntimeSurface.Create("editor-panel");

        Assert.Equal("web", web.Id);
        Assert.Equal("editor-panel", custom.Id);
        Assert.Equal("cli", RuntimeSurface.Cli.Id);
        Assert.Throws<ArgumentException>(() => RuntimeSurface.Create(" "));
        Assert.Throws<ArgumentException>(() => RuntimeSurface.Create("web/ui"));
    }

    [Fact]
    public void Loop_turn_request_requires_input_and_surface_without_defaulting_to_cli()
    {
        var request = new DefaultConversationLoopTurnRequest("hello", RuntimeSurface.Web);

        Assert.Equal("hello", request.Input);
        Assert.Equal("web", request.Surface.Id);
        Assert.Equal(LlmMessageRole.User, request.ToUserMessage().Role);
        Assert.Throws<ArgumentException>(() => new DefaultConversationLoopTurnRequest(" ", RuntimeSurface.Web));
        Assert.Throws<ArgumentNullException>(() => new DefaultConversationLoopTurnRequest("hello", null!));
    }

    [Fact]
    public async Task Loop_turn_request_carries_streaming_diagnostics_and_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<string>();
        var diagnostics = new List<RuntimeDiagnosticMessage>();
        var request = new DefaultConversationLoopTurnRequest(
            "hello",
            RuntimeSurface.Web,
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
        Assert.Equal(transcript, completed.TranscriptMessages);
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, failed.Status);
        Assert.Equal("provider failed", failed.FailureDetail);
        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, cancelled.Status);
        Assert.Equal("caller cancelled", cancelled.FailureDetail);
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
