using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Workspace;
namespace EmbodySense.Core.Startup.Workspace;

public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    private readonly WorkspaceScaffolder _scaffolder;

    public WorkspaceInitializer()
        : this(new WorkspaceScaffolder())
    {
    }

    public WorkspaceInitializer(WorkspaceScaffolder scaffolder)
    {
        ArgumentNullException.ThrowIfNull(scaffolder);

        _scaffolder = scaffolder;
    }

    public Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);
        return _scaffolder.ApplyAsync(paths, WorkspaceDefaults.GetDirectories(paths), WorkspaceDefaults.GetSeedFiles(paths), cancellationToken);
    }
}
