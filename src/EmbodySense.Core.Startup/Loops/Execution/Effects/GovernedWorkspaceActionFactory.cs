using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.LocalWorkspace.Actions;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.WorkspaceActions;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Composes the three workspace operations over one native host and the shared capability mutation boundary.</summary>
public static class GovernedWorkspaceActionFactory
{
    /// <summary>Creates the finite exact workspace operation registry for one statically admitted workspace scope.</summary>
    public static GovernedActuatorOperationRegistry CreateRegistry(
        WorkspacePaths paths,
        ICapabilityAuthorityTransaction capabilityAuthorityTransaction,
        IToolPermissionService permissionService,
        TimeProvider? timeProvider = null,
        IWorkspaceActionCommittedAfterEvidenceResolver? committedAfterEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(capabilityAuthorityTransaction);
        ArgumentNullException.ThrowIfNull(permissionService);
        var descriptor = BuiltInCapabilityCatalog.Descriptors.Single(candidate =>
            string.Equals(candidate.Id.Value, "org.embodysense/workspace-command", StringComparison.Ordinal));
        if (!CapabilityDescriptorIdentity.TryCreate(descriptor, out var capability, out _))
        {
            throw new InvalidOperationException("The built-in workspace capability identity is unavailable.");
        }
        if (!WorkspaceActionScopeId.TryParse("workspace", out var scope))
        {
            throw new InvalidOperationException("The built-in workspace scope is invalid.");
        }
        IWorkspaceMutationCommitBoundary nestedBoundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, capabilityAuthorityTransaction);
        var evidence = new WorkspaceActionEvidenceStore(paths);
        var host = new WorkspaceActionNativeHost(
            paths,
            scope!,
            nestedBoundary,
            new ToolPermissionWorkspaceActionRevalidator(permissionService),
            evidenceStore: evidence,
            timeProvider: timeProvider,
            committedAfterEvidence: committedAfterEvidence ?? new WorkspaceActionCommittedAfterEvidenceStoreResolver(paths, evidence),
            attemptPresence: new WorkspaceActionAttemptStorePresenceResolver(paths, evidence));
        var operations = new[]
        {
            WorkspaceActionKind.Append,
            WorkspaceActionKind.Write,
            WorkspaceActionKind.Delete,
        }.Select(kind => new GovernedWorkspaceActionOperation(capability!, descriptor.Implementation, kind, host));
        return new GovernedActuatorOperationRegistry(operations);
    }
}
