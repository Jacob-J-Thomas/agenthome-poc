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
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Workspace;
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
    public async Task InitializeAsync_rejects_overlapping_file_trust_roots_before_creating_workspace_or_trust_state()
    {
        using var root = new TestWorkspace();
        var workspaceRoot = root.File("absent-workspace");
        var trustRoot = Path.Combine(workspaceRoot, "server-trust");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot).InitializeAsync(workspaceRoot));

        Assert.False(Directory.Exists(workspaceRoot));
        Assert.False(File.Exists(Path.Combine(trustRoot, "capability-catalog-root.key")));
    }

    [Fact]
    public async Task InitializeAsync_rejects_a_workspace_inside_the_file_trust_root_before_mutation()
    {
        using var trustRoot = new TestWorkspace();
        var workspaceRoot = trustRoot.File("absent-workspace");
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot.RootPath).InitializeAsync(workspaceRoot));

        Assert.False(Directory.Exists(workspaceRoot));
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
        Assert.False(Directory.Exists(provider.AnchorsPath));
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
    public async Task InitializeAsync_seeds_exact_built_ins_as_available_but_not_assigned_or_authorized()
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
            Assert.Equal(CapabilityEnablementState.Enabled, entry.Lifecycle.Enablement);
            Assert.Equal(CapabilityHealthState.Healthy, entry.Lifecycle.Health);
            Assert.Equal(CapabilityTrustState.Verified, entry.Lifecycle.Trust);
            Assert.Equal(CapabilityRetirementState.Active, entry.Lifecycle.Retirement);
        }

        var json = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        Assert.DoesNotContain("assignment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authority", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.AuthorityProfilesDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task InitializeAsync_reuses_one_populated_server_trust_root_across_distinct_workspaces()
    {
        using var trustRoot = new TestWorkspace();
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot.RootPath).InitializeAsync(firstWorkspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot.RootPath).InitializeAsync(secondWorkspace.RootPath);

        var first = await new CapabilityCatalogStore(new WorkspacePaths(firstWorkspace.RootPath), provider).ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        var second = await new CapabilityCatalogStore(new WorkspacePaths(secondWorkspace.RootPath), provider).ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        Assert.Equal(CapabilityCatalogReadStatus.Available, first.Status);
        Assert.Equal(CapabilityCatalogReadStatus.Available, second.Status);
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, first.Page!.Entries.Count);
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, second.Page!.Entries.Count);
        Assert.Equal(2, Directory.EnumerateFiles(provider.AnchorsPath, "*.json", SearchOption.TopDirectoryOnly).Count());
    }

    [Fact]
    public async Task InitializeAsync_concurrently_seeds_distinct_workspaces_while_the_first_anchor_commit_holds_the_proved_server_trust_root()
    {
        using var trustRoot = new TestWorkspace();
        using var firstWorkspace = new TestWorkspace();
        using var secondWorkspace = new TestWorkspace();
        var barrier = new BlockingCapabilityCatalogAnchorDurabilityBarrier();
        var firstProvider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath, barrier);
        var secondProvider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var signalingProvider = new SignalingCapabilityCatalogTrustProvider(secondProvider);
        var firstInitializer = new WorkspaceInitializer(new WorkspaceScaffolder(), new BuiltInCapabilityCatalogSeeder(firstProvider));
        var secondInitializer = new WorkspaceInitializer(new WorkspaceScaffolder(), new BuiltInCapabilityCatalogSeeder(signalingProvider));
        var timeout = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource();
        Task? firstInitialization = null;
        Task? secondInitialization = null;
        var initializationsConverged = false;

        try
        {
            firstInitialization = firstInitializer.InitializeAsync(firstWorkspace.RootPath, cancellation.Token);
            await barrier.AnchorWriteEntered.WaitAsync(timeout);
            secondInitialization = secondInitializer.InitializeAsync(secondWorkspace.RootPath, cancellation.Token);
            await signalingProvider.ReadEntered.WaitAsync(timeout);
            Assert.False(signalingProvider.ReadCompleted.IsCompleted);
            Assert.False(secondInitialization.IsCompleted);
            barrier.Release();
            await signalingProvider.ReadCompleted.WaitAsync(timeout);
            await Task.WhenAll(firstInitialization, secondInitialization).WaitAsync(timeout);
            initializationsConverged = true;
        }
        finally
        {
            barrier.Release();
            if (!initializationsConverged)
            {
                cancellation.Cancel();
            }

            await AwaitInitializationCleanupAsync(firstInitialization, secondInitialization, initializationsConverged);
        }

        var first = await new CapabilityCatalogStore(new WorkspacePaths(firstWorkspace.RootPath), firstProvider).ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        var second = await new CapabilityCatalogStore(new WorkspacePaths(secondWorkspace.RootPath), secondProvider).ReadAsync(null, CapabilityCatalogLimits.MaximumPageSize);
        Assert.Equal(CapabilityCatalogReadStatus.Available, first.Status);
        Assert.Equal(CapabilityCatalogReadStatus.Available, second.Status);
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, first.Page!.Entries.Count);
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, second.Page!.Entries.Count);
        Assert.Equal(2, Directory.EnumerateFiles(firstProvider.AnchorsPath, "*.json", SearchOption.TopDirectoryOnly).Count());
    }

    private static async Task AwaitInitializationCleanupAsync(Task? firstInitialization, Task? secondInitialization, bool initializationsConverged)
    {
        var initializations = new[] { firstInitialization, secondInitialization }.OfType<Task>().ToArray();
        if (initializations.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(initializations);
        }
        catch when (!initializationsConverged)
        {
        }
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
    public async Task InitializeAsync_rejects_a_partial_existing_agent_home_without_loading_or_backfilling_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.PermissionsPath, "{}");
        await File.WriteAllTextAsync(paths.AgentFile("AGENT.md"), "legacy role");
        Assert.False(paths.IsInitialized);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath));

        Assert.Contains("Remove .agent explicitly before reinitializing", exception.Message, StringComparison.Ordinal);
        Assert.False(paths.IsInitialized);
        Assert.False(File.Exists(paths.RolePath));
        Assert.False(File.Exists(paths.WorkspaceInitializationMarkerPath));
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
        Assert.Equal("legacy role", await File.ReadAllTextAsync(paths.AgentFile("AGENT.md")));
    }

    [Fact]
    public async Task InitializeAsync_rejects_an_invalid_existing_completion_marker_before_any_workspace_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.WorkspaceInitializationMarkerPath, "{\"schemaVersion\":2,\"status\":\"completed\"}\n");
        var before = await File.ReadAllTextAsync(paths.WorkspaceInitializationMarkerPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath));

        Assert.Equal(before, await File.ReadAllTextAsync(paths.WorkspaceInitializationMarkerPath));
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
        Assert.False(File.Exists(paths.RolePath));
    }

    [Fact]
    public async Task InitializeAsync_seeds_the_exact_default_contextual_role_only_for_a_fresh_agent_home()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        using var store = new ContextualRoleRevisionStore(paths, workspaceId);
        var identity = new ContextualRoleRevisionIdentity(DefaultContextualRoleSeeder.RoleId, DefaultContextualRoleSeeder.Revision);
        var revisionRead = await store.ReadAsync(new ContextualRoleRevisionReadRequest(identity));
        var lifecycleRead = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(DefaultContextualRoleSeeder.RoleId));
        Assert.Equal(ContextualRoleRevisionReadStatus.Found, revisionRead.Status);
        Assert.Equal(ContextualRoleRevisionDisposition.Active, revisionRead.Disposition);
        Assert.Equal(ContextualRoleStatus.Published, revisionRead.Revision!.Status);
        Assert.Equal(workspaceId, Assert.Single(revisionRead.Revision.WorkspaceApplicability.WorkspaceIds));
        Assert.Equal(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, revisionRead.Revision.InstructionSource.Kind);
        Assert.Equal("role", revisionRead.Revision.InstructionSource.ReferenceId);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Found, lifecycleRead.Status);
        Assert.Equal(ContextualRoleLifecycleState.Active, lifecycleRead.Snapshot!.State);
        Assert.True(paths.IsInitialized);
    }

    [Fact]
    public async Task InitializeAsync_revalidates_a_valid_existing_workspace_without_reseeding_its_role()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var roleStorePath = Path.Combine(paths.AgentPath, "contextual-roles");
        var roleStoreBefore = DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(roleStorePath);
        var roleSeeder = new RecordingRoleSeeder();
        var initializer = new WorkspaceInitializer(
            new WorkspaceScaffolder(),
            new BuiltInCapabilityCatalogSeeder(new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)),
            roleSeeder);

        await initializer.InitializeAsync(workspace.RootPath);

        Assert.Equal(0, roleSeeder.CallCount);
        AssertSnapshotsEqual(roleStoreBefore, DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(roleStorePath));
        var status = new WorkspaceStatusReader().Read(workspace.RootPath);
        Assert.True(status.IsInitialized);
        Assert.False(status.RequiresExplicitCleanup);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("substituted")]
    [InlineData("inactive")]
    [InlineData("wrong-workspace")]
    [InlineData("source-ineligible")]
    public async Task InitializeAsync_refuses_damaged_role_evidence_without_backfill_resurrection_or_mutation(string damage)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await DefaultContextualRoleEvidenceTestSupport.DamageAsync(workspace, damage);
        var workspaceBefore = DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.RootPath);
        var serverStateBefore = DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.ServerStatePath);
        var roleSeeder = new RecordingRoleSeeder();
        var initializer = new WorkspaceInitializer(
            new WorkspaceScaffolder(),
            new BuiltInCapabilityCatalogSeeder(new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)),
            roleSeeder);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync(workspace.RootPath));

        Assert.Contains("Remove .agent explicitly before reinitializing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, roleSeeder.CallCount);
        AssertSnapshotsEqual(workspaceBefore, DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.RootPath));
        AssertSnapshotsEqual(serverStateBefore, DefaultContextualRoleEvidenceTestSupport.SnapshotFiles(workspace.ServerStatePath));
        var status = new WorkspaceStatusReader().Read(workspace.RootPath);
        Assert.False(status.IsInitialized);
        Assert.True(status.RequiresExplicitCleanup);
        Assert.True(File.Exists(paths.WorkspaceInitializationMarkerPath));
    }

    [Fact]
    public async Task InitializeAsync_reproves_the_exact_role_after_existing_workspace_work_and_before_rewriting_completion()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var roleSeeder = new RecordingRoleSeeder();
        var initializer = new WorkspaceInitializer(
            new WorkspaceScaffolder(),
            new BuiltInCapabilityCatalogSeeder(new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)),
            roleSeeder,
            new ConcurrentRoleRemovalObserver());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync(workspace.RootPath));

        Assert.Contains("could not be re-read as the exact active revision", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, roleSeeder.CallCount);
        Assert.False(Directory.Exists(Path.Combine(paths.AgentPath, "contextual-roles")));
        Assert.False(File.Exists(paths.WorkspaceInitializationMarkerPath));
        var status = new WorkspaceStatusReader().Read(workspace.RootPath);
        Assert.False(status.IsInitialized);
        Assert.True(status.RequiresExplicitCleanup);
    }

    [Fact]
    public async Task InitializeAsync_leaves_no_completion_marker_after_role_seed_crash_and_retry_requires_cleanup()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var roleSeeder = new ThrowingRoleSeeder();
        var initializer = new WorkspaceInitializer(
            new WorkspaceScaffolder(),
            new BuiltInCapabilityCatalogSeeder(new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)),
            roleSeeder);

        await Assert.ThrowsAsync<IOException>(() => initializer.InitializeAsync(workspace.RootPath));

        Assert.True(Directory.Exists(paths.AgentPath));
        Assert.True(File.Exists(paths.RolePath));
        Assert.False(File.Exists(paths.WorkspaceInitializationMarkerPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath));
        Assert.Equal(1, roleSeeder.CallCount);
        Assert.False(File.Exists(paths.WorkspaceInitializationMarkerPath));
    }

    [Fact]
    public async Task InitializeAsync_fails_closed_when_agent_home_appears_after_the_freshness_snapshot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var initializer = new WorkspaceInitializer(
            new WorkspaceScaffolder(),
            new BuiltInCapabilityCatalogSeeder(new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)),
            new DefaultContextualRoleSeeder(),
            new ConcurrentAgentHomeObserver());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync(workspace.RootPath));

        Assert.Contains("appeared after the fresh-workspace decision", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(paths.AgentPath));
        Assert.False(File.Exists(paths.CapabilityCatalogDocumentPath));
        Assert.False(File.Exists(paths.WorkspaceInitializationMarkerPath));
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
        Assert.True(Directory.Exists(workspace.File(".agent", "loops", "revisions")));
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
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
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

    private sealed class RecordingRoleSeeder : IDefaultContextualRoleSeeder
    {
        public int CallCount { get; private set; }

        public Task<ContextualRoleRevisionPin> SeedAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ContextualRoleRevisionPin(
                new ContextualRoleRevisionIdentity(DefaultContextualRoleSeeder.RoleId, DefaultContextualRoleSeeder.Revision),
                new string('a', 64)));
        }
    }

    private sealed class ThrowingRoleSeeder : IDefaultContextualRoleSeeder
    {
        public int CallCount { get; private set; }

        public Task<ContextualRoleRevisionPin> SeedAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new IOException("Simulated crash after scaffolding.");
        }
    }

    private sealed class ConcurrentAgentHomeObserver : IWorkspaceInitializationBoundaryObserver
    {
        public ValueTask OnFreshnessCapturedAsync(WorkspacePaths paths, bool wasFreshAgentHome, bool hadValidCompletionMarker, CancellationToken cancellationToken = default)
        {
            Assert.True(wasFreshAgentHome);
            Assert.False(hadValidCompletionMarker);
            Directory.CreateDirectory(paths.AgentPath);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrentRoleRemovalObserver : IWorkspaceInitializationBoundaryObserver
    {
        public ValueTask OnFreshnessCapturedAsync(WorkspacePaths paths, bool wasFreshAgentHome, bool hadValidCompletionMarker, CancellationToken cancellationToken = default)
        {
            Assert.False(wasFreshAgentHome);
            Assert.True(hadValidCompletionMarker);
            Directory.Delete(Path.Combine(paths.AgentPath, "contextual-roles"), recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private static void AssertSnapshotsEqual(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var path in expected.Keys)
        {
            Assert.Equal(expected[path], actual[path]);
        }
    }
}
