namespace EmbodySense.Core.Startup.Workspace;

public interface IWorkspaceInitializer
{
    Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default);
}
