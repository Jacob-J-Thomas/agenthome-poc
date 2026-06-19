namespace EmbodySense.Core.Application.Governance.Tools;

public interface ILocalWorkspaceClient
{
    Task<LocalWorkspaceResult> ListAsync(string resolvedPath, CancellationToken cancellationToken = default);

    Task<LocalWorkspaceResult> ReadAsync(string resolvedPath, CancellationToken cancellationToken = default);

    Task<LocalWorkspaceResult> SearchAsync(string resolvedPath, string? pattern, CancellationToken cancellationToken = default);

    Task<LocalWorkspaceResult> AppendAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default);

    Task<LocalWorkspaceResult> WriteAsync(string resolvedPath, string? content, CancellationToken cancellationToken = default);

    Task<LocalWorkspaceResult> DeleteAsync(string resolvedPath, CancellationToken cancellationToken = default);
}
