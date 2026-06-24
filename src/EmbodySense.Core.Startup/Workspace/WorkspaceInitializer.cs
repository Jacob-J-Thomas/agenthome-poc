using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    private readonly WorkspaceScaffolder _scaffolder;
    private readonly string _actor;

    public WorkspaceInitializer() : this(new WorkspaceScaffolder(), WorkspaceActors.Web)
    {
    }

    public WorkspaceInitializer(string actor) : this(new WorkspaceScaffolder(), actor)
    {
    }

    public WorkspaceInitializer(WorkspaceScaffolder scaffolder, string actor = WorkspaceActors.Web)
    {
        ArgumentNullException.ThrowIfNull(scaffolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        _scaffolder = scaffolder;
        _actor = actor;
    }

    public static WorkspaceInitializer ForCli()
    {
        return new WorkspaceInitializer(WorkspaceActors.Cli);
    }

    public static WorkspaceInitializer ForWeb()
    {
        return new WorkspaceInitializer(WorkspaceActors.Web);
    }

    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);
        await _scaffolder.ApplyAsync(paths, WorkspaceDefaults.GetDirectories(paths), WorkspaceDefaults.GetSeedFiles(paths), _actor, cancellationToken);
    }
}
