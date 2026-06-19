using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Persistence.Workspace.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Tests.Core.Application.Runtime;

public sealed class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task CreateAsync_builds_session_with_startup_context_and_fresh_transcript()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "runtime guide");
        await File.WriteAllTextAsync(paths.CurrentConversationPath, "old transcript" + Environment.NewLine);

        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            WorkingDirectory = workspace.RootPath,
            CodexSandbox = "read-only"
        });

        Assert.Equal(paths.RootPath, runtime.Paths.RootPath);
        Assert.Contains(runtime.StartupContext, message => message.Content.Contains("runtime guide", StringComparison.Ordinal));
        Assert.NotEmpty(runtime.Session.Messages);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(paths.CurrentConversationPath));
        Assert.NotEmpty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
    }

    private sealed class RejectingApprovalPrompt : IToolApprovalPrompt
    {
        public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolApprovalResponse.Reject("test", "No approval needed during runtime construction."));
        }
    }
}
