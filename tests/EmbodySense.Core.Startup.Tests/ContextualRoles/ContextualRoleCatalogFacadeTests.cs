using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Startup.ContextualRoles;
using EmbodySense.Core.Startup.ContextualRoles.Models;
using EmbodySense.Tests.Support;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Startup.Tests.ContextualRoles;

public sealed class ContextualRoleCatalogFacadeTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 9, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Construction_is_read_only_and_validates_server_owned_workspace_configuration()
    {
        using var workspace = new TestWorkspace();
        var before = Snapshot(workspace.RootPath);

        _ = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");

        Assert.Equal(before.ToArray(), Snapshot(workspace.RootPath).ToArray());
        Assert.Throws<ArgumentException>(() => new ContextualRoleCatalogFacade(" ", "workspace-one"));
        Assert.Throws<ArgumentException>(() => new ContextualRoleCatalogFacade(workspace.RootPath, "../unsafe"));
    }

    [Fact]
    public async Task Cancellation_propagates_before_catalog_or_exact_inspection()
    {
        using var workspace = new TestWorkspace();
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => facade.ReadCatalogAsync(null, 10, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => facade.InspectAsync(new ContextualRoleInspectionInput("reviewer", 1, new string('a', 64)), cancellation.Token));
    }

    [Fact]
    public async Task Catalog_is_bounded_ordered_redacted_and_read_only_with_exact_provenance()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        const string AgentsSecret = "agents-secret-canary-cc09be";
        const string RoleSecret = "role-secret-canary-271be1";
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "AGENTS.md"), AgentsSecret);
        await File.WriteAllTextAsync(paths.RolePath, RoleSecret);
        var reviewer = Revision("reviewer", ContextualRoleInstructionSourceKind.AgentsMarkdown, "nearest-agents", capabilityIds: ["workspace-write", "workspace-read"]);
        var writer = Revision("writer", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        await CreateAsync(paths, reviewer);
        await CreateAsync(paths, writer);
        var before = Snapshot(workspace.RootPath);
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");

        var first = await facade.ReadCatalogAsync(null, 1);
        var second = await facade.ReadCatalogAsync(first.NextCursor, 1);
        var after = Snapshot(workspace.RootPath);

        Assert.Equal("available", first.Status);
        Assert.Null(first.Error);
        var firstRole = Assert.Single(first.Roles);
        Assert.Equal("reviewer", firstRole.RoleId);
        Assert.Equal(reviewer.Identity.Revision, firstRole.Revision);
        Assert.Equal(reviewer.ContentHash, firstRole.ContentHash);
        Assert.Equal("user-jake", firstRole.AuthorId);
        Assert.Equal(_now, firstRole.CreatedAtUtc);
        Assert.Equal("agents-markdown", firstRole.InstructionSourceKind);
        Assert.Equal("nearest-agents", firstRole.InstructionSourceId);
        Assert.Equal("ready", firstRole.SourceStatus);
        Assert.True(firstRole.IsAdmissionReady);
        Assert.Equal(["workspace-read", "workspace-write"], firstRole.CapabilityMaximumIds);
        Assert.Empty(firstRole.Dependents);
        Assert.False(firstRole.AreDependentsComplete);
        Assert.False(firstRole.DependentsTruncated);
        Assert.Equal("reviewer", first.NextCursor);
        Assert.Equal("writer", Assert.Single(second.Roles).RoleId);
        Assert.Null(second.NextCursor);
        Assert.Equal(before.ToArray(), after.ToArray());

        var serialized = JsonSerializer.Serialize(new[] { first, second });
        Assert.DoesNotContain(AgentsSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(RoleSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.RootPath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENTS.md", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("ROLE.md", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_inspection_requires_the_current_identity_hash_and_registered_source()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.RolePath, "review instructions");
        var revision = Revision("reviewer", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        await CreateAsync(paths, revision);
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");

        var ready = await facade.InspectAsync(new ContextualRoleInspectionInput("reviewer", 1, revision.ContentHash));
        var staleHash = await facade.InspectAsync(new ContextualRoleInspectionInput("reviewer", 1, new string('0', 64)));
        var missingRevision = await facade.InspectAsync(new ContextualRoleInspectionInput("reviewer", 2, revision.ContentHash));
        var malformed = await facade.InspectAsync(new ContextualRoleInspectionInput("../unsafe", 0, "bad"));
        var nullInput = await facade.InspectAsync(null!);

        Assert.Equal("ready", ready.Status);
        Assert.True(ready.Role!.IsAdmissionReady);
        Assert.Empty(ready.Role.Dependents);
        Assert.False(ready.Role.AreDependentsComplete);
        Assert.False(ready.Role.DependentsTruncated);
        Assert.Null(ready.Error);
        Assert.Equal("stale", staleHash.Status);
        Assert.Equal("contextual_role_stale", staleHash.Error!.Code);
        Assert.Null(staleHash.Role);
        Assert.Equal("not-found", missingRevision.Status);
        Assert.Equal("invalid", malformed.Status);
        Assert.Equal("invalid", nullInput.Status);
    }

    [Fact]
    public async Task Missing_unknown_oversized_ambiguous_and_cross_workspace_sources_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        var missing = Revision("missing", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        var unknown = Revision("unknown", ContextualRoleInstructionSourceKind.RoleArtifact, "role-artifact");
        var crossWorkspace = Revision("cross-workspace", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role", workspaceId: "workspace-other");
        await CreateAsync(paths, missing);
        await CreateAsync(paths, unknown);
        await CreateAsync(paths, crossWorkspace);
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");

        var missingResult = await facade.InspectAsync(Input(missing));
        var unknownResult = await facade.InspectAsync(Input(unknown));
        var crossWorkspaceResult = await facade.InspectAsync(Input(crossWorkspace));
        await File.WriteAllTextAsync(paths.RolePath, new string('x', WorkspaceContextualRoleInstructionSourceProbe.MaximumInstructionSourceBytes + 1));
        var oversized = await facade.InspectAsync(Input(missing));
        await File.WriteAllBytesAsync(paths.RolePath, [0xff, 0xfe]);
        var ambiguous = await facade.InspectAsync(Input(missing));

        Assert.Equal("source-missing", missingResult.Status);
        Assert.Equal("contextual_role_source_missing", missingResult.Error!.Code);
        Assert.False(missingResult.Role!.IsAdmissionReady);
        Assert.Equal("source-unsupported", unknownResult.Status);
        Assert.Equal("workspace-mismatch", crossWorkspaceResult.Status);
        Assert.Equal("source-oversized", oversized.Status);
        Assert.Equal("ambiguous", ambiguous.Status);
        Assert.All([missingResult, unknownResult, crossWorkspaceResult, oversized, ambiguous], result => Assert.False(result.Role!.IsAdmissionReady));
    }

    [Fact]
    public async Task Disabled_tombstoned_and_replaced_revisions_fail_closed_without_loading_sources()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.RolePath, "role instructions");
        var disabledRevision = Revision("disabled", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        var tombstonedRevision = Revision("tombstoned", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        var replacedRevision = Revision("replaced", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        await CreateAsync(paths, disabledRevision);
        await CreateAsync(paths, tombstonedRevision);
        await CreateAsync(paths, replacedRevision);
        await LifecycleAsync(paths, disabledRevision.Identity, ContextualRoleRevisionMutationKind.Disable, "disable-disabled", _now.AddMinutes(1));
        await LifecycleAsync(paths, tombstonedRevision.Identity, ContextualRoleRevisionMutationKind.Tombstone, "tombstone-tombstoned", _now.AddMinutes(1));
        var replacement = ContextualRoleRevisionContentHash.Apply(replacedRevision with { Identity = new ContextualRoleRevisionIdentity("replaced", 2), Purpose = "Replacement purpose." });
        await ReplaceAsync(paths, replacedRevision.Identity, replacement);
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");

        var disabled = await facade.InspectAsync(Input(disabledRevision));
        var tombstoned = await facade.InspectAsync(Input(tombstonedRevision));
        var stale = await facade.InspectAsync(Input(replacedRevision));

        Assert.Equal("ineligible", disabled.Status);
        Assert.Equal("ineligible", disabled.Role!.SourceStatus);
        Assert.Equal("ineligible", tombstoned.Status);
        Assert.Equal("tombstoned", tombstoned.Role!.LifecycleState);
        Assert.Equal("stale", stale.Status);
        Assert.Null(stale.Role);
    }

    [Fact]
    public async Task Symbolic_source_substitution_is_reported_without_target_content_or_path()
    {
        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        const string OutsideSecret = "outside-secret-canary-a93f30";
        var outsideRole = Path.Combine(outside.RootPath, "ROLE.md");
        await File.WriteAllTextAsync(outsideRole, OutsideSecret);
        File.CreateSymbolicLink(paths.RolePath, outsideRole);
        var revision = Revision("reviewer", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        await CreateAsync(paths, revision);
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");

        var result = await facade.InspectAsync(Input(revision));
        var serialized = JsonSerializer.Serialize(result);

        Assert.Equal("source-substituted", result.Status);
        Assert.Equal("contextual_role_source_substituted", result.Error!.Code);
        Assert.False(result.Role!.IsAdmissionReady);
        Assert.DoesNotContain(OutsideSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(outside.RootPath, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_catalog_failures_are_value_free_and_return_no_partial_entries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = Revision("reviewer", ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role");
        await CreateAsync(paths, revision);
        await File.WriteAllTextAsync(Path.Combine(paths.AgentPath, "contextual-roles", "states", "reviewer.json"), "{broken");
        var facade = new ContextualRoleCatalogFacade(workspace.RootPath, "workspace-one");
        var invalid = await facade.ReadCatalogAsync(null, ContextualRoleCatalogLimits.MaximumPageSize + 1);
        var ambiguous = await facade.ReadCatalogAsync(null, 10);
        var unavailable = await new ContextualRoleCatalogFacade(Path.Combine(workspace.RootPath, "missing"), "workspace-one").ReadCatalogAsync(null, 10);

        Assert.Equal("invalid", invalid.Status);
        Assert.Equal("invalid_contextual_role_catalog_request", invalid.Error!.Code);
        Assert.Empty(invalid.Roles);
        Assert.Equal("ambiguous", ambiguous.Status);
        Assert.Equal("contextual_role_catalog_ambiguous", ambiguous.Error!.Code);
        Assert.Empty(ambiguous.Roles);
        Assert.Equal("unavailable", unavailable.Status);
        Assert.Empty(unavailable.Roles);
        Assert.DoesNotContain(workspace.RootPath, JsonSerializer.Serialize(new[] { invalid, ambiguous, unavailable }), StringComparison.Ordinal);
    }

    [Fact]
    public void Public_snapshots_defensively_capture_capabilities_dependents_and_null_collections()
    {
        var dependent = new ContextualRoleDependentSnapshot("loop", "loop-one", 3);
        var capabilities = new List<string> { "workspace-read" };
        var dependents = new List<ContextualRoleDependentSnapshot> { dependent };
        var snapshot = new ContextualRoleSnapshot(
            "reviewer", 1, new string('a', 64), "Reviewer", "Review work.", "published", "active", "user-jake",
            _now, _now, _now, "workspace-role-markdown", "role", "ready", true, true, capabilities, dependents, true, false);
        var nullSnapshot = new ContextualRoleSnapshot(
            "reviewer", 1, new string('a', 64), "Reviewer", "Review work.", "published", "active", "user-jake",
            _now, _now, _now, "workspace-role-markdown", "role", "ready", true, true, null!, null!, true, false);
        var roles = new List<ContextualRoleSnapshot> { snapshot };
        var catalog = new ContextualRoleCatalogResponse("available", roles, null, null);
        var nullCatalog = new ContextualRoleCatalogResponse("available", null!, null, null);

        capabilities.Clear();
        dependents.Clear();
        roles.Clear();

        Assert.Equal("loop", dependent.Kind);
        Assert.Equal("loop-one", dependent.Identity);
        Assert.Equal(3, dependent.Revision);
        Assert.Equal("workspace-read", Assert.Single(snapshot.CapabilityMaximumIds));
        Assert.Same(dependent, Assert.Single(snapshot.Dependents));
        Assert.Empty(nullSnapshot.CapabilityMaximumIds);
        Assert.Empty(nullSnapshot.Dependents);
        Assert.Same(snapshot, Assert.Single(catalog.Roles));
        Assert.Empty(nullCatalog.Roles);
    }

    private static ContextualRoleInspectionInput Input(ContextualRoleRevision revision)
        => new(revision.Identity.RoleId, revision.Identity.Revision, revision.ContentHash);

    private static async Task CreateAsync(WorkspacePaths paths, ContextualRoleRevision revision)
    {
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest($"create-{revision.Identity.RoleId}", string.Empty, ContextualRoleRevisionMutationKind.Create, revision.Identity.RoleId, "user-jake", revision, null, _now));
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
    }

    private static async Task LifecycleAsync(WorkspacePaths paths, ContextualRoleRevisionIdentity identity, ContextualRoleRevisionMutationKind kind, string operationId, DateTimeOffset requestedAt)
    {
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(operationId, string.Empty, kind, identity.RoleId, "user-jake", null, identity, requestedAt));
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
    }

    private static async Task ReplaceAsync(WorkspacePaths paths, ContextualRoleRevisionIdentity previous, ContextualRoleRevision replacement)
    {
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest($"replace-{previous.RoleId}", string.Empty, ContextualRoleRevisionMutationKind.Replace, previous.RoleId, "user-jake", replacement, previous, _now.AddMinutes(1)));
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
    }

    private static ContextualRoleRevision Revision(
        string roleId,
        ContextualRoleInstructionSourceKind sourceKind,
        string sourceId,
        string workspaceId = "workspace-one",
        IReadOnlyList<string>? capabilityIds = null)
    {
        var value = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(roleId, 1),
            string.Empty,
            $"{roleId} display",
            $"Purpose for {roleId}.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("user-jake", _now, _now),
            new ContextualRoleWorkspaceApplicability([workspaceId]),
            new ContextualRoleInstructionSourceReference(sourceKind, sourceId, ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima((capabilityIds ?? []).ToImmutableArray()));
        return ContextualRoleRevisionContentHash.Apply(value);
    }

    private static IReadOnlyDictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);
}
