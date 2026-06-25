using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Workspace;

public sealed class WorkspaceInitializerTests
{
    [Fact]
    public async Task InitializeAsync_seeds_memory_priority_guidance()
    {
        using var workspace = new TestWorkspace();

        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);

        var agentGuide = await File.ReadAllTextAsync(workspace.File(".agent", "AGENT.md"));
        Assert.Contains("Keep agent documents current when durable identity, purpose, operating context, or user preferences change.", agentGuide);
        Assert.Contains("Use `.agent/SOUL.md` for stable purpose and values.", agentGuide);
        Assert.Contains("Use `.agent/PERSONALITY.md` for durable interaction style and behavioral defaults.", agentGuide);
        Assert.Contains("Treat `.agent/MEMORY.md` as the primary durable memory registry.", agentGuide);
        Assert.Contains("Store, update, create, and retrieve most long-lived memories in `.agent/MEMORY.md`.", agentGuide);
        Assert.Contains("Query conversation history only for transcript-specific evidence", agentGuide);

        var soulGuide = await File.ReadAllTextAsync(workspace.File(".agent", "SOUL.md"));
        Assert.Contains("stable purpose, values, and identity", soulGuide);

        var personalityGuide = await File.ReadAllTextAsync(workspace.File(".agent", "PERSONALITY.md"));
        Assert.Contains("durable interaction style", personalityGuide);

        var memoryGuide = await File.ReadAllTextAsync(workspace.File(".agent", "MEMORY.md"));
        Assert.Contains("Use this file as the primary durable memory registry.", memoryGuide);
        Assert.Contains("Store, update, create, and retrieve most memories here.", memoryGuide);
        Assert.Contains("Query conversation history only for specific transcript use cases", memoryGuide);

        var memoryReadme = await File.ReadAllTextAsync(workspace.File(".agent", "memory", "README.md"));
        Assert.Contains("The primary durable memory registry is `.agent/MEMORY.md`.", memoryReadme);
        Assert.Contains("Conversation history is supporting transcript evidence", memoryReadme);
    }

    [Fact]
    public async Task InitializeAsync_defaults_workspace_init_audit_to_web_actor()
    {
        using var workspace = new TestWorkspace();

        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);

        var auditText = await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson"));
        Assert.Contains(AuditSchema.Actors.Web, auditText);
        Assert.DoesNotContain(AuditSchema.Actors.Cli, auditText);
    }
}
