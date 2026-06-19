namespace EmbodySense.Core.Application.Startup;

public interface IWorkspaceInitializer
{
    Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default);
}
