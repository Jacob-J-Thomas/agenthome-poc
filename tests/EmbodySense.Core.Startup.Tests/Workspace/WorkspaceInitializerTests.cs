using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Workspace;

public sealed class WorkspaceInitializerTests
{
    [Fact]
    public void Default_composition_factories_do_not_mutate_server_state_during_construction()
    {
        Assert.NotNull(new WorkspaceInitializer());
        Assert.NotNull(WorkspaceInitializer.ForCli());
        Assert.NotNull(WorkspaceInitializer.ForWeb());
    }

    [Fact]
    public async Task InitializeAsync_creates_a_genuinely_absent_workspace_before_seeding_and_audits_only_after_success()
    {
        using var workspace = new TestWorkspace();
        Directory.Delete(workspace.RootPath, recursive: true);
        var paths = new WorkspacePaths(workspace.RootPath);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        Assert.True(paths.IsInitialized);
        Assert.True(File.Exists(paths.CapabilityCatalogDocumentPath));
        var auditLines = await File.ReadAllLinesAsync(paths.EventsLogPath);
        Assert.Single(auditLines, line => line.Contains(AuditSchema.Actions.WorkspaceInit, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_seeds_exact_built_ins_as_installed_but_not_enabled_trusted_assigned_or_authorized()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var firstRead = await new CapabilityCatalogStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)).ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        var firstArtifact = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var read = await new CapabilityCatalogStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)).ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
        Assert.Equal(firstRead.Page!.CatalogRevision, read.Page!.CatalogRevision);
        Assert.Equal(firstArtifact, await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath));
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, read.Page!.Entries.Count);
        foreach (var entry in read.Page.Entries)
        {
            Assert.Equal(CapabilityDeclarationState.Declared, entry.Lifecycle.Declaration);
            Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
            Assert.Equal(CapabilityEnablementState.Disabled, entry.Lifecycle.Enablement);
            Assert.Equal(CapabilityTrustState.Unverified, entry.Lifecycle.Trust);
            Assert.Equal(CapabilityRetirementState.Active, entry.Lifecycle.Retirement);
        }

        var json = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        Assert.DoesNotContain("assignment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authority", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_fails_closed_when_built_in_catalog_primary_and_proof_are_corrupt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(paths.CapabilityCatalogDocumentPath, "{broken");
        await File.WriteAllTextAsync(paths.CapabilityCatalogProofPath, "{also-broken");
        var auditBeforeFailure = await File.ReadAllTextAsync(paths.EventsLogPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath));
        Assert.Equal(auditBeforeFailure, await File.ReadAllTextAsync(paths.EventsLogPath));
    }

    [Fact]
    public async Task InitializeAsync_upgrades_a_pre_role_workspace_without_loading_or_deleting_legacy_agent_text()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.PermissionsPath, "{}");
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "legacy role");
        Assert.False(paths.IsInitialized);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        Assert.True(paths.IsInitialized);
        Assert.True(File.Exists(paths.RolePath));
        Assert.Equal("{\"schemaVersion\":1,\"status\":\"completed\"}\n", await File.ReadAllTextAsync(paths.WorkspaceInitializationMarkerPath));
        Assert.Equal("legacy role", await File.ReadAllTextAsync(paths.AgentFile("AGENT.md")));
    }

    [Fact]
    public async Task InitializeAsync_seeds_memory_priority_guidance()
    {
        using var workspace = new TestWorkspace();

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var roleGuide = await File.ReadAllTextAsync(workspace.File(".agent", "ROLE.md"));
        Assert.Contains("contextual role the durable agent occupies in this workspace", roleGuide);
        Assert.Contains("does not create a separate identity", roleGuide);
        Assert.Contains("Use `.agent/ROLE.md` for this workspace's contextual role", roleGuide);
        Assert.Contains("Use `.agent/SOUL.md` for stable purpose and values.", roleGuide);
        Assert.Contains("Use `.agent/PERSONALITY.md` for durable interaction style and behavioral defaults.", roleGuide);
        Assert.Contains("Treat `.agent/MEMORY.md` as the primary durable memory registry.", roleGuide);
        Assert.Contains("Store, update, create, and retrieve most long-lived memories in `.agent/MEMORY.md`.", roleGuide);
        Assert.Contains("Query conversation history only for transcript-specific evidence", roleGuide);
        Assert.Contains("## Emergent capability growth", roleGuide);
        Assert.Contains("Do not claim hooks, cron jobs, subagents, planners, MCP integrations, model routing, or other advanced capabilities are live", roleGuide);
        Assert.False(File.Exists(workspace.File(".agent", "AGENT.md")));

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
        Assert.Contains("tool-response manifests and chunks", permissionsReadme);

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
        Assert.True(Directory.Exists(workspace.File(".agent", "logs", "tool-responses")));
    }

    [Fact]
    public async Task InitializeAsync_defaults_workspace_init_audit_to_web_actor()
    {
        using var workspace = new TestWorkspace();

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var auditText = await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson"));
        Assert.Contains(AuditSchema.Actors.Web, auditText);
        Assert.DoesNotContain(AuditSchema.Actors.Cli, auditText);
    }

    [Fact]
    public async Task InitializeAsync_leaves_unsupported_permissions_document_for_explicit_reinitialization()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        const string Unsupported = "{\"version\": 2}";
        await File.WriteAllTextAsync(paths.PermissionsPath, Unsupported);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        Assert.Equal(Unsupported, await File.ReadAllTextAsync(paths.PermissionsPath));
        var evaluation = new PermissionPolicyStore().Load(paths).EvaluateDirectory(paths.ToolResponsesPath, FileSystemOperation.Read);
        Assert.Equal(PermissionDecision.RequiresApproval, evaluation.Decision);
    }

    [Fact]
    public async Task InitializeAsync_preserves_a_current_policy_that_intentionally_removes_tool_response_inspection()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var permissions = Assert.IsType<PermissionsDocument>(PermissionsDocument.FromJson(await File.ReadAllTextAsync(paths.PermissionsPath)));
        permissions.Approved.RemoveAll(entry => string.Equals(entry.Path, PermissionsDocument.ToolResponseInspectionPath, StringComparison.Ordinal));
        await File.WriteAllTextAsync(paths.PermissionsPath, permissions.ToJson());

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var preserved = Assert.IsType<PermissionsDocument>(PermissionsDocument.FromJson(await File.ReadAllTextAsync(paths.PermissionsPath)));
        Assert.Equal(PermissionsDocument.CurrentVersion, preserved.Version);
        Assert.DoesNotContain(preserved.Approved, entry => string.Equals(entry.Path, PermissionsDocument.ToolResponseInspectionPath, StringComparison.Ordinal));
        var evaluation = new PermissionPolicyStore().Load(paths).EvaluateDirectory(paths.ToolResponsesPath, FileSystemOperation.Read);
        Assert.Equal(PermissionDecision.Deny, evaluation.Decision);
    }

    [Fact]
    public async Task InitializeAsync_inspects_a_current_read_only_policy_without_requiring_write_access()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var original = await File.ReadAllTextAsync(paths.PermissionsPath);
        var originalAttributes = File.GetAttributes(paths.PermissionsPath);
        UnixFileMode? originalMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(paths.PermissionsPath);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(paths.PermissionsPath, originalAttributes | FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(paths.PermissionsPath, UnixFileMode.UserRead);
            }

            await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

            Assert.Equal(original, await File.ReadAllTextAsync(paths.PermissionsPath));
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(paths.PermissionsPath, originalAttributes);
            }
            else
            {
                File.SetUnixFileMode(paths.PermissionsPath, originalMode!.Value);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_seeds_default_conversation_loop_definition()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var definition = await new LoopDefinitionStore(paths).LoadAsync("default-conversation");
        Assert.NotNull(definition);
        Assert.Equal(LoopDefinition.CurrentSchemaVersion, definition.SchemaVersion);
        Assert.Equal("Default conversation loop", definition.DisplayName);
        Assert.Equal("default-assistant", definition.RoleId);
        Assert.Equal(LoopTrigger.HumanMessage, definition.Trigger);
        Assert.Equal(LoopMemoryScope.WorkspaceStartupContext, definition.MemoryScope);
        Assert.Contains("workspace.command", definition.CapabilityIds);
        Assert.Contains("approval.request", definition.CapabilityIds);
        Assert.Equal(LoopState.Enabled, definition.State);
        Assert.Equal(LoopEditMode.SystemLocked, definition.EditMode);
        Assert.Equal(DefaultConversationLoopGraphIds.AcceptUserMessage, definition.Graph.EntryNodeId);
        Assert.Contains(definition.Graph.Nodes, node => node.Id == DefaultConversationLoopGraphIds.AssembleContext);
        var json = await File.ReadAllTextAsync(paths.DefaultConversationLoopDefinitionPath);
        Assert.Contains("\"trigger\": \"human-message\"", json);
        Assert.Contains("\"state\": \"enabled\"", json);
        Assert.Contains("\"graph\"", json);
        Assert.Contains("\"entryNodeId\": \"accept-user-message\"", json);
    }
}
