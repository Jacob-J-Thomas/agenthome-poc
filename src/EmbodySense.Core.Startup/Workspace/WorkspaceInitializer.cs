using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Persistence.Workspace;
namespace EmbodySense.Core.Startup.Workspace;

public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    private readonly WorkspaceScaffolder _scaffolder;
    private readonly string _actor;

    public WorkspaceInitializer() : this(new WorkspaceScaffolder(), AuditSchema.Actors.Web)
    {
    }

    public WorkspaceInitializer(string actor) : this(new WorkspaceScaffolder(), actor)
    {
    }

    public WorkspaceInitializer(WorkspaceScaffolder scaffolder, string actor = AuditSchema.Actors.Web)
    {
        ArgumentNullException.ThrowIfNull(scaffolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        _scaffolder = scaffolder;
        _actor = actor;
    }

    public Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);
        return _scaffolder.ApplyAsync(paths, WorkspaceDefaults.GetDirectories(paths), WorkspaceDefaults.GetSeedFiles(paths), _actor, cancellationToken);
    }
}
