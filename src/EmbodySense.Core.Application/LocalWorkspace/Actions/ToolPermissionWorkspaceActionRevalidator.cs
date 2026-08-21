using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Projects exact native workspace state into the current shared directory-permission policy.</summary>
public sealed class ToolPermissionWorkspaceActionRevalidator : IWorkspaceActionPermissionRevalidator
{
    private readonly IToolPermissionService _permissionService;

    /// <summary>Creates a current-policy revalidator over the same permission source used by ToolBroker.</summary>
    public ToolPermissionWorkspaceActionRevalidator(IToolPermissionService permissionService)
    {
        _permissionService = permissionService ?? throw new ArgumentNullException(nameof(permissionService));
    }

    /// <inheritdoc />
    public Task<WorkspaceActionPermissionRevalidation> RevalidateAsync(
        WorkspaceActionPermissionRevalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Operation != WorkspaceActionPermissionOperation.For(request.Input.Kind, request.EntryKind)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(request.TargetFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(request.RootIdentityFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(request.ParentIdentityFingerprint)
            || request.NativeIdentityFingerprint is not null && !WorkspaceActionFingerprint.IsCanonicalSha256(request.NativeIdentityFingerprint))
        {
            return Task.FromResult(new WorkspaceActionPermissionRevalidation(false, request.Operation, null));
        }

        var command = request.Input.Kind switch
        {
            WorkspaceActionKind.Append => ToolCommand.Append,
            WorkspaceActionKind.Write => ToolCommand.Write,
            WorkspaceActionKind.Delete => ToolCommand.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(request), "The workspace action kind is unsupported."),
        };
        var current = _permissionService.EvaluateExactFileMutation(new ToolRequest(command, request.Input.Target.Value), request.Operation);
        var allowed = current.Operation == request.Operation
            && current.Evaluation.Decision == PermissionDecision.Allow
            && WorkspaceActionFingerprint.IsCanonicalSha256(current.PolicyHash);
        return Task.FromResult(new WorkspaceActionPermissionRevalidation(allowed, current.Operation, current.PolicyHash));
    }
}
