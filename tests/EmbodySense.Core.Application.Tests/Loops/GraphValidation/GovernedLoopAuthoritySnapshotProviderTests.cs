using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopAuthoritySnapshotProviderTests
{
    [Fact]
    public async Task Exact_active_resolution_projects_source_complete_authority_without_widening()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var source = new RoleSource { Resolution = Active(role, pin) };

        var snapshot = await new GovernedLoopAuthoritySnapshotProvider(source).GetSnapshotAsync(pin);

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(pin, snapshot.OwningRole);
        Assert.Equal(role, snapshot.RoleRevision);
        Assert.Equal(AuthorityGrantApplicationTestFixture.WorkspaceId, snapshot.WorkspaceId);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ready, snapshot.SourceStatus);
        Assert.Equal(role.PolicyMaxima.CapabilityIds, snapshot.CapabilityIds);
        Assert.Matches("^[0-9a-f]{64}$", snapshot.SourceEvidenceId);
    }

    [Fact]
    public async Task Empty_role_ceiling_remains_available_and_non_granting()
    {
        var role = AuthorityGrantApplicationTestFixture.Role(capabilityIds: []);
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var source = new RoleSource { Resolution = Active(role, pin) };

        var snapshot = await new GovernedLoopAuthoritySnapshotProvider(source).GetSnapshotAsync(pin);

        Assert.True(snapshot.IsAvailable);
        Assert.Empty(snapshot.CapabilityIds);
    }

    [Fact]
    public async Task Pin_workspace_source_and_lifecycle_substitution_fail_closed()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var provider = new GovernedLoopAuthoritySnapshotProvider(new RoleSource
        {
            Resolution = Active(role, pin) with
            {
                RequestedPin = new ContextualRoleRevisionPin(role.Identity, AuthorityGrantApplicationTestFixture.Hash64('f')),
            },
        });
        var pinMismatch = await provider.GetSnapshotAsync(pin);

        provider = new(new RoleSource
        {
            Resolution = Active(role, pin) with { WorkspaceId = "workspace-sha256:" + new string('b', 64) },
        });
        var workspaceMismatch = await provider.GetSnapshotAsync(pin);

        provider = new(new RoleSource
        {
            Resolution = Active(role, pin) with { SourceStatus = ContextualRoleInstructionSourceProbeStatus.Substituted },
        });
        var sourceMismatch = await provider.GetSnapshotAsync(pin);

        provider = new(new RoleSource
        {
            Resolution = Active(role, pin) with
            {
                Lifecycle = AuthorityGrantApplicationTestFixture.RoleLifecycle(role, ContextualRoleLifecycleState.Disabled),
            },
        });
        var lifecycleMismatch = await provider.GetSnapshotAsync(pin);

        Assert.False(pinMismatch.IsAvailable);
        Assert.False(workspaceMismatch.IsAvailable);
        Assert.False(sourceMismatch.IsAvailable);
        Assert.False(lifecycleMismatch.IsAvailable);
        Assert.All(new[] { pinMismatch, workspaceMismatch, sourceMismatch, lifecycleMismatch }, snapshot => Assert.Empty(snapshot.CapabilityIds));
    }

    [Fact]
    public async Task Source_failure_is_unavailable_and_cancellation_propagates()
    {
        var role = AuthorityGrantApplicationTestFixture.Role();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var source = new RoleSource { Exception = new IOException("offline") };
        var provider = new GovernedLoopAuthoritySnapshotProvider(source);

        var unavailable = await provider.GetSnapshotAsync(pin);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        source.Exception = null;

        Assert.False(unavailable.IsAvailable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSnapshotAsync(pin, cancellation.Token));
    }

    private static AuthorityGrantRoleResolution Active(ContextualRoleRevision role, ContextualRoleRevisionPin pin)
        => new(
            AuthorityGrantDependencyStatus.Active,
            pin,
            role,
            AuthorityGrantApplicationTestFixture.RoleLifecycle(role),
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            ContextualRoleInstructionSourceProbeStatus.Ready,
            AuthorityGrantApplicationTestFixture.Hash64('d'));

    private sealed class RoleSource : IAuthorityGrantRoleSource
    {
        internal AuthorityGrantRoleResolution Resolution { get; set; } = null!;
        internal Exception? Exception { get; set; }

        public Task<AuthorityGrantRoleResolution> ResolveAsync(ContextualRoleRevisionPin? pin, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Exception is null
                ? Task.FromResult(Resolution)
                : Task.FromException<AuthorityGrantRoleResolution>(Exception);
        }
    }
}
