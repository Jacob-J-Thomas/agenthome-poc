namespace EmbodySense.Core.Workspace;

public interface IWorkspaceInitializer
{
    Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default);
}
