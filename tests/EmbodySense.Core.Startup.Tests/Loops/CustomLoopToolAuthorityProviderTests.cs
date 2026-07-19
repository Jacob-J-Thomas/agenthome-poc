using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class CustomLoopToolAuthorityProviderTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-19T12:00:00+00:00");

    [Fact]
    public async Task Resolve_projects_default_role_ceiling_and_admitted_intersection()
    {
        using var workspace = new TestWorkspace();
        var provider = await ProviderWithDefaultAsync(workspace, new FixedTimeProvider(Timestamp));

        var authority = await provider.ResolveAsync("default-assistant", [CustomLoopToolAssignment.Search, CustomLoopToolAssignment.List]);

        Assert.True(authority.IsValid);
        Assert.Equal("default-assistant", authority.RoleId);
        Assert.Equal([CustomLoopToolAssignment.Search, CustomLoopToolAssignment.List], authority.AdmittedMaximum);
        Assert.Equal([CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search], authority.CurrentRoleCeiling);
        Assert.Equal([CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search], authority.ImplementedCatalog);
        Assert.Equal([CustomLoopToolAssignment.List, CustomLoopToolAssignment.Search], authority.EffectiveAssignments);
        Assert.Equal(CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash(authority.RoleId, authority.CurrentRoleCeiling), authority.RoleCeilingHash);
        Assert.Equal(CustomLoopToolAuthorityProvider.ComputeCatalogHash(), authority.CatalogHash);
        Assert.Equal(Timestamp, authority.EvaluatedAtUtc);
        Assert.Contains("immutable admitted maximum", authority.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_rejects_role_drift_duplicate_and_unsupported_admissions()
    {
        using var workspace = new TestWorkspace();
        var provider = await ProviderWithDefaultAsync(workspace);

        var roleDrift = await provider.ResolveAsync("other-role", [CustomLoopToolAssignment.Read]);
        var duplicate = await provider.ResolveAsync("default-assistant", [CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Read]);
        var unsupported = await provider.ResolveAsync("default-assistant", [(CustomLoopToolAssignment)999]);

        Assert.False(roleDrift.IsValid);
        Assert.Empty(roleDrift.EffectiveAssignments);
        Assert.Contains("no longer matches", roleDrift.Detail, StringComparison.Ordinal);
        Assert.False(duplicate.IsValid);
        Assert.Empty(duplicate.EffectiveAssignments);
        Assert.Contains("unsupported or duplicate", duplicate.Detail, StringComparison.Ordinal);
        Assert.False(unsupported.IsValid);
        Assert.Empty(unsupported.EffectiveAssignments);
        Assert.Contains("unsupported or duplicate", unsupported.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_fails_closed_when_the_directory_role_definition_is_missing()
    {
        using var workspace = new TestWorkspace();
        var provider = Provider(workspace, new FixedTimeProvider(Timestamp));

        var authority = await provider.ResolveAsync("default-assistant", [CustomLoopToolAssignment.Read]);

        Assert.False(authority.IsValid);
        Assert.Empty(authority.CurrentRoleCeiling);
        Assert.Empty(authority.EffectiveAssignments);
        Assert.Contains("missing", authority.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_intersects_with_the_persisted_directory_role_ceiling()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new LoopDefinitionStore(paths);
        var definition = LoopDefinition.CreateDefaultConversation() with
        {
            CapabilityIds = [LoopCapabilityIds.WorkspaceCommandFor(ToolCommand.Read)]
        };
        await store.SaveAsync(definition);
        var provider = new CustomLoopToolAuthorityProvider(store, new FixedTimeProvider(Timestamp));

        var authority = await provider.ResolveAsync(definition.RoleId, [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search]);

        Assert.True(authority.IsValid);
        Assert.Equal([CustomLoopToolAssignment.Read], authority.CurrentRoleCeiling);
        Assert.Equal([CustomLoopToolAssignment.Read], authority.EffectiveAssignments);
        Assert.Equal([CustomLoopToolAssignment.Read], CustomLoopToolAuthorityProvider.ResolveCurrentRoleCeiling(definition));
    }

    [Fact]
    public async Task Resolve_fails_closed_when_the_directory_role_definition_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopDefinitionsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.LoopDefinitionsPath, "default-conversation.json"), "{invalid");
        var provider = new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(paths), new FixedTimeProvider(Timestamp));

        var authority = await provider.ResolveAsync("default-assistant", [CustomLoopToolAssignment.Read]);

        Assert.False(authority.IsValid);
        Assert.Equal("default-assistant", authority.RoleId);
        Assert.Equal([CustomLoopToolAssignment.Read], authority.AdmittedMaximum);
        Assert.Empty(authority.CurrentRoleCeiling);
        Assert.Empty(authority.EffectiveAssignments);
        Assert.Equal(CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash("default-assistant", []), authority.RoleCeilingHash);
        Assert.Equal(Timestamp, authority.EvaluatedAtUtc);
        Assert.Contains("FormatException", authority.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_boundaries_reject_missing_inputs()
    {
        using var workspace = new TestWorkspace();
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        var provider = new CustomLoopToolAuthorityProvider(store);

        Assert.Throws<ArgumentNullException>(() => new CustomLoopToolAuthorityProvider(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(" ", []));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.ResolveAsync("default-assistant", null!));
        Assert.Throws<ArgumentNullException>(() => CustomLoopToolAuthorityProvider.ResolveCurrentRoleCeiling(null!));
        Assert.Throws<ArgumentException>(() => CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash(" ", []));
        Assert.Throws<ArgumentNullException>(() => CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash("default-assistant", null!));
    }

    private static CustomLoopToolAuthorityProvider Provider(TestWorkspace workspace, TimeProvider? timeProvider = null)
    {
        return new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath)), timeProvider);
    }

    private static async Task<CustomLoopToolAuthorityProvider> ProviderWithDefaultAsync(TestWorkspace workspace, TimeProvider? timeProvider = null)
    {
        var store = new LoopDefinitionStore(new WorkspacePaths(workspace.RootPath));
        await store.SaveAsync(LoopDefinition.CreateDefaultConversation());
        return new CustomLoopToolAuthorityProvider(store, timeProvider);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
