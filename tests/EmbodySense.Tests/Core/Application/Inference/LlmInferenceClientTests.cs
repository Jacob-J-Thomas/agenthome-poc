using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Audit.Models;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Persistence.Workspace.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Tests.Core.Application.Inference;

public sealed class LlmInferenceClientTests
{
    [Fact]
    public async Task GenerateAsync_records_failed_audit_event_when_provider_fails()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var client = new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.AzureAiFoundry,
            WorkingDirectory = workspace.RootPath
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GenerateAsync(LlmInferenceRequest.FromUserText("hello")));

        var events = await new AuditLog(paths).ReadTailAsync(2);
        Assert.Collection(
            events,
            auditEvent =>
            {
                Assert.Equal("llm.inference.start", auditEvent.Action);
                Assert.Equal("started", auditEvent.Outcome);
            },
            auditEvent =>
            {
                Assert.Equal("llm.inference.complete", auditEvent.Action);
                Assert.Equal("failed", auditEvent.Outcome);
            });
    }
}
