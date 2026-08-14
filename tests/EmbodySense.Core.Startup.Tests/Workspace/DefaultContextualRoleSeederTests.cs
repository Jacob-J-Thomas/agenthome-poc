using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Workspace;

public sealed class DefaultContextualRoleSeederTests
{
    [Fact]
    public async Task SeedAsync_persists_and_proves_the_exact_active_non_granting_default_revision()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        await File.WriteAllTextAsync(paths.RolePath, "# Workspace role\n\nAssist within explicit authority.\n");

        var pin = await new DefaultContextualRoleSeeder().SeedAsync(paths);

        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        using var store = new ContextualRoleRevisionStore(paths, workspaceId);
        var read = await store.ReadAsync(new ContextualRoleRevisionReadRequest(pin.Identity));
        var lifecycle = await store.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(DefaultContextualRoleSeeder.RoleId));
        var revision = Assert.IsType<ContextualRoleRevision>(read.Revision);
        Assert.Equal(ContextualRoleRevisionReadStatus.Found, read.Status);
        Assert.Equal(ContextualRoleRevisionDisposition.Active, read.Disposition);
        Assert.Equal(new ContextualRoleRevisionIdentity("default-assistant", 1), pin.Identity);
        Assert.Equal(pin.ContentHash, revision.ContentHash);
        Assert.True(ContextualRoleRevisionContentHash.Matches(revision));
        Assert.Equal(ContextualRoleStatus.Published, revision.Status);
        Assert.Equal("embodysense-initializer", revision.Provenance.AuthorId);
        Assert.Equal(workspaceId, Assert.Single(revision.WorkspaceApplicability.WorkspaceIds));
        Assert.Equal(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, revision.InstructionSource.Kind);
        Assert.Equal("role", revision.InstructionSource.ReferenceId);
        Assert.Equal(ContextualRoleInstructionClassification.RoleInstruction, revision.InstructionSource.Classification);
        Assert.Equal(
            LoopCapabilityRequirements.GetAssignedCapabilityIds(LoopCapabilityRequirements.CreateDefaultConversationManifest()).Select(id => id.Value).Order(StringComparer.Ordinal),
            revision.PolicyMaxima.CapabilityIds);
        Assert.True(revision.PolicyMaxima.IsNonGranting);
        Assert.Equal(ContextualRoleLifecycleReadStatus.Found, lifecycle.Status);
        Assert.Equal(ContextualRoleLifecycleState.Active, lifecycle.Snapshot!.State);
        Assert.Equal(pin.Identity, lifecycle.Snapshot.CurrentIdentity);
    }

    [Fact]
    public async Task SeedAsync_fails_before_role_persistence_when_the_registered_source_is_not_ready()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new DefaultContextualRoleSeeder().SeedAsync(paths));

        Assert.Contains("instruction source is not ready", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(paths.AgentPath, "contextual-roles")));
    }
}
