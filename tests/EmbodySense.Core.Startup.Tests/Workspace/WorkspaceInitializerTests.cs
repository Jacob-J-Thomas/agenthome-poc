using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
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
        Assert.Contains("## Emergent capability growth", agentGuide);
        Assert.Contains("Do not claim hooks, cron jobs, subagents, planners, MCP integrations, model routing, or other advanced capabilities are live", agentGuide);

        var soulGuide = await File.ReadAllTextAsync(workspace.File(".agent", "SOUL.md"));
        Assert.Contains("stable purpose and values", soulGuide);
        Assert.Contains("The agent exists to become a useful local assistant with a real workspace body", soulGuide);
        Assert.Contains("Be generative. Convert useful discoveries into durable capability", soulGuide);
        Assert.Contains("Use `PERSONALITY.md` for interaction style and behavioral defaults.", soulGuide);

        var personalityGuide = await File.ReadAllTextAsync(workspace.File(".agent", "PERSONALITY.md"));
        Assert.Contains("durable interaction style", personalityGuide);
        Assert.Contains("Be practical, direct, and context-aware.", personalityGuide);
        Assert.Contains("## Emergent behavior", personalityGuide);
        Assert.Contains("Do not expose or claim access to private model reasoning.", personalityGuide);

        var contextGuide = await File.ReadAllTextAsync(workspace.File(".agent", "CONTEXT.md"));
        Assert.Contains("This file holds concrete operating context for this workspace.", contextGuide);
        Assert.Contains("AI-only areas", contextGuide);
        Assert.Contains("Primary test or verification commands", contextGuide);

        var memoryGuide = await File.ReadAllTextAsync(workspace.File(".agent", "MEMORY.md"));
        Assert.Contains("Use this file as the primary durable memory registry.", memoryGuide);
        Assert.Contains("Store, update, create, and retrieve most memories here.", memoryGuide);
        Assert.Contains("Query conversation history only for specific transcript use cases", memoryGuide);
        Assert.Contains("## Retrieval protocol", memoryGuide);
        Assert.Contains("Mark old memories as superseded", memoryGuide);

        var memoryReadme = await File.ReadAllTextAsync(workspace.File(".agent", "memory", "README.md"));
        Assert.Contains("The primary durable memory registry is `.agent/MEMORY.md`.", memoryReadme);
        Assert.Contains("Conversation history is supporting transcript evidence", memoryReadme);
        Assert.Contains("Search `.agent/MEMORY.md` first.", memoryReadme);

        var permissionsReadme = await File.ReadAllTextAsync(workspace.File(".agent", "PERMISSIONS.md"));
        Assert.Contains("Agent document writes such as `.agent/MEMORY.md`", permissionsReadme);

        var auditReadme = await File.ReadAllTextAsync(workspace.File(".agent", "audit", "README.md"));
        Assert.Contains("## How agents should reason about audit", auditReadme);

        var modelsJson = await File.ReadAllTextAsync(workspace.File(".agent", "models.json"));
        Assert.Contains("placeholder-not-runtime-binding", modelsJson);
        Assert.Contains("configuration_agent", modelsJson);
        Assert.False(Directory.Exists(workspace.File("workspace")));
        Assert.True(Directory.Exists(workspace.File("shared")));
        Assert.True(Directory.Exists(workspace.File("generated")));
        Assert.True(Directory.Exists(workspace.File("system")));
        Assert.True(Directory.Exists(workspace.File("private")));
        Assert.True(Directory.Exists(workspace.File(".agent", "loops")));
        Assert.True(Directory.Exists(workspace.File(".agent", "loops", "definitions")));
        Assert.True(Directory.Exists(workspace.File(".agent", "loops", "runs")));
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

    [Fact]
    public async Task InitializeAsync_seeds_default_conversation_loop_definition()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);

        var definition = await new LoopDefinitionStore(paths).LoadAsync("default-conversation");
        Assert.NotNull(definition);
        Assert.Equal(LoopDefinition.CurrentSchemaVersion, definition.SchemaVersion);
        Assert.Equal("Default conversation loop", definition.DisplayName);
        Assert.Equal("default-assistant", definition.RoleId);
        Assert.Equal("human-message", definition.Trigger);
        Assert.Equal("workspace-startup-context", definition.MemoryScope);
        Assert.Contains("workspace.command", definition.CapabilityIds);
        Assert.Contains("approval.request", definition.CapabilityIds);
        Assert.Equal("enabled", definition.State);
    }
}
