using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Runtime.Diagnostics;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class RuntimeDiagnosticFormatterTests
{
    [Fact]
    public void Verbose_context_projects_every_public_message_role_and_source_without_hidden_state()
    {
        var context = new RuntimeVerboseContext(
            LoopDefinition.CreateDefaultConversation(),
            new LoopRunIdentity(BuiltInLoopIds.DefaultConversation, "run-diagnostic", "diagnostic-role"),
            RuntimeSurfaceId.Web,
            [
                new RuntimeContextMessage(LlmMessage.System("system-content"), RuntimeContextSource.StartupContext, "startup"),
                new RuntimeContextMessage(LlmMessage.User("user-content"), RuntimeContextSource.CurrentTurnInput, "current"),
                new RuntimeContextMessage(LlmMessage.Assistant("assistant-content"), RuntimeContextSource.RestoredConversationHistory, "restored"),
                new RuntimeContextMessage(LlmMessage.Tool("tool-content"), RuntimeContextSource.SessionTranscript, "session"),
            ],
            [new RuntimeContextOmission("memory", "admission", "bounded")],
            "not-required");

        var formatted = RuntimeDiagnosticFormatter.FormatVerboseContext(context);

        Assert.Contains("message 3: role=assistant source=restored-conversation-history", formatted, StringComparison.Ordinal);
        Assert.Contains("Assistant:\nassistant-content", formatted, StringComparison.Ordinal);
        Assert.Contains("message 4: role=tool source=session-transcript", formatted, StringComparison.Ordinal);
        Assert.Contains("Tool:\ntool-content", formatted, StringComparison.Ordinal);
        Assert.Contains("memory (admission): bounded", formatted, StringComparison.Ordinal);
        Assert.Contains("not private model reasoning, hidden chain-of-thought", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Verbose_context_explicitly_projects_an_absent_graph()
    {
        var definition = LoopDefinition.CreateDefaultConversation() with { Graph = null! };
        var context = new RuntimeVerboseContext(
            definition,
            new LoopRunIdentity(definition.Id, "run-no-graph"),
            RuntimeSurfaceId.Runtime,
            [],
            [],
            "not-required");

        var formatted = RuntimeDiagnosticFormatter.FormatVerboseContext(context);

        Assert.Contains("graph_entry_node: (none)", formatted, StringComparison.Ordinal);
        Assert.Contains("graph_terminal_nodes: (none)", formatted, StringComparison.Ordinal);
        Assert.Contains("graph_nodes: (none)", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Verbose_context_requires_a_public_context()
    {
        Assert.Throws<ArgumentNullException>(() => RuntimeDiagnosticFormatter.FormatVerboseContext(null!));
    }
}
