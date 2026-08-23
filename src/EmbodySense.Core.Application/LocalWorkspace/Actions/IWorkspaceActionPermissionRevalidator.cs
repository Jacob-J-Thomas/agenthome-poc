using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Revalidates the exact current directory-policy class immediately before a workspace namespace change.</summary>
public interface IWorkspaceActionPermissionRevalidator
{
    /// <summary>Evaluates one server-derived regular-file operation against the current permission policy.</summary>
    Task<WorkspaceActionPermissionRevalidation> RevalidateAsync(
        WorkspaceActionPermissionRevalidationRequest request,
        CancellationToken cancellationToken = default);
}
