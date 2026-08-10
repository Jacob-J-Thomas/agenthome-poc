using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.ContextualRoles;

public sealed class ContextualRoleCatalogReaderTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Empty_and_invalid_catalog_reads_do_not_initialize_persistence()
    {
        using var workspace = new TestWorkspace();
        using var store = new ContextualRoleRevisionStore(new WorkspacePaths(workspace.RootPath), "workspace-one");

        var empty = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 10));
        var invalidCursor = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest("../unsafe", 10));
        var invalidMinimum = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 0));
        var invalidMaximum = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, ContextualRoleCatalogLimits.MaximumPageSize + 1));
        var invalidNull = await store.ReadCatalogAsync(null!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(ContextualRoleCatalogReadStatus.Available, empty.Status);
        Assert.Empty(empty.Entries);
        Assert.Null(empty.NextCursor);
        Assert.All([invalidCursor, invalidMinimum, invalidMaximum, invalidNull], result => Assert.Equal(ContextualRoleCatalogReadStatus.Invalid, result.Status));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 10), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, ".agent", "contextual-roles")));
    }

    [Fact]
    public async Task Partial_store_reads_fail_ambiguous_without_creating_layout_or_lock_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var partialRoot = Path.Combine(paths.AgentPath, "contextual-roles");
        Directory.CreateDirectory(partialRoot);
        await File.WriteAllTextAsync(Path.Combine(partialRoot, "interrupted.marker"), "partial");
        var before = Directory.EnumerateFileSystemEntries(partialRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(partialRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");

        var catalog = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 10));
        var revision = await store.ReadAsync(new ContextualRoleRevisionReadRequest(new ContextualRoleRevisionIdentity("reviewer", 1)));
        var lifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest("reviewer"));
        var after = Directory.EnumerateFileSystemEntries(partialRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(partialRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ContextualRoleCatalogReadStatus.Ambiguous, catalog.Status);
        Assert.Equal(ContextualRoleRevisionReadStatus.Ambiguous, revision.Status);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Ambiguous, lifecycle.Status);
        Assert.Equal(before, after);
        Assert.False(File.Exists(Path.Combine(partialRoot, ".mutations.lock")));
        Assert.False(Directory.Exists(Path.Combine(partialRoot, "revisions")));
        Assert.False(Directory.Exists(Path.Combine(partialRoot, "states")));
        Assert.False(Directory.Exists(Path.Combine(partialRoot, "operations")));
        Assert.False(Directory.Exists(Path.Combine(partialRoot, "proofs")));
    }

    [Fact]
    public async Task Existing_directories_without_a_lock_fail_ambiguous_without_creating_the_lock()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var root = Path.Combine(paths.AgentPath, "contextual-roles");
        Directory.CreateDirectory(Path.Combine(root, "revisions"));
        Directory.CreateDirectory(Path.Combine(root, "states"));
        Directory.CreateDirectory(Path.Combine(root, "operations"));
        Directory.CreateDirectory(Path.Combine(root, "proofs"));
        var before = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");

        var result = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 10));
        var after = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ContextualRoleCatalogReadStatus.Ambiguous, result.Status);
        Assert.Equal(before, after);
        Assert.False(File.Exists(Path.Combine(root, ".mutations.lock")));
    }

    [Fact]
    public async Task Catalog_pages_current_roles_in_ordinal_order_with_exact_lifecycle_posture()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await CreateAsync(paths, "writer");
        await CreateAsync(paths, "analyst");
        await CreateAsync(paths, "reviewer");
        await DisableAsync(paths, "reviewer");

        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var first = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 2));
        var second = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(first.NextCursor, 2));

        Assert.Equal(ContextualRoleCatalogReadStatus.Available, first.Status);
        Assert.Equal(["analyst", "reviewer"], first.Entries.Select(entry => entry.Revision.Identity.RoleId));
        Assert.Equal("reviewer", first.NextCursor);
        Assert.Equal(ContextualRoleLifecycleState.Disabled, first.Entries[1].Lifecycle.State);
        Assert.Equal(first.Entries[1].Revision.Identity, first.Entries[1].Lifecycle.CurrentIdentity);
        Assert.Equal(["writer"], second.Entries.Select(entry => entry.Revision.Identity.RoleId));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Catalog_returns_no_partial_entries_when_durable_state_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await CreateAsync(paths, "reviewer");
        await File.WriteAllTextAsync(Path.Combine(paths.AgentPath, "contextual-roles", "states", "reviewer.json"), "{not-json");

        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        var result = await store.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(null, 10));

        Assert.Equal(ContextualRoleCatalogReadStatus.Ambiguous, result.Status);
        Assert.Empty(result.Entries);
        Assert.Null(result.NextCursor);
    }

    private static async Task CreateAsync(WorkspacePaths paths, string roleId)
    {
        var revision = Revision(roleId);
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest($"create-{roleId}", string.Empty, ContextualRoleRevisionMutationKind.Create, roleId, "user-jake", revision, null, _now));
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
    }

    private static async Task DisableAsync(WorkspacePaths paths, string roleId)
    {
        var identity = new ContextualRoleRevisionIdentity(roleId, 1);
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest($"disable-{roleId}", string.Empty, ContextualRoleRevisionMutationKind.Disable, roleId, "user-jake", null, identity, _now.AddMinutes(1)));
        using var store = new ContextualRoleRevisionStore(paths, "workspace-one");
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await store.MutateAsync(request)).Status);
    }

    private static ContextualRoleRevision Revision(string roleId)
    {
        var value = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(roleId, 1),
            string.Empty,
            roleId,
            $"Purpose for {roleId}.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("user-jake", _now, _now),
            new ContextualRoleWorkspaceApplicability(["workspace-one"]),
            new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role", ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima([]));
        return ContextualRoleRevisionContentHash.Apply(value);
    }
}
