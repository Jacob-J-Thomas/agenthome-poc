using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>
/// Applies the canonical version-one workspace scaffold through a Core.Startup facade.
/// </summary>
/// <remarks>
/// The initializer attributes its completion audit event to the configured interface actor.
/// Scaffolding is ordered but non-transactional: existing protected seeds are preserved, while
/// cancellation, I/O, and audit failures propagate after any earlier changes remain in place.
/// </remarks>
public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    private readonly WorkspaceScaffolder _scaffolder;
    private readonly string _actor;

    /// <summary>
    /// Creates an initializer attributed to the Web interface.
    /// </summary>
    public WorkspaceInitializer() : this(new WorkspaceScaffolder(), WorkspaceActors.Web)
    {
    }

    /// <summary>
    /// Creates an initializer attributed to the supplied audit actor.
    /// </summary>
    /// <param name="actor">The nonblank actor recorded for successful initialization.</param>
    public WorkspaceInitializer(string actor) : this(new WorkspaceScaffolder(), actor)
    {
    }

    /// <summary>
    /// Creates an initializer over an explicit persistence scaffolder.
    /// </summary>
    /// <param name="scaffolder">The persistence component that performs ordered scaffold writes.</param>
    /// <param name="actor">The nonblank actor recorded for successful initialization.</param>
    public WorkspaceInitializer(WorkspaceScaffolder scaffolder, string actor = WorkspaceActors.Web)
    {
        ArgumentNullException.ThrowIfNull(scaffolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        _scaffolder = scaffolder;
        _actor = actor;
    }

    /// <summary>
    /// Creates an initializer whose audit events are attributed to the CLI.
    /// </summary>
    /// <returns>A CLI-attributed workspace initializer.</returns>
    public static WorkspaceInitializer ForCli()
    {
        return new WorkspaceInitializer(WorkspaceActors.Cli);
    }

    /// <summary>
    /// Creates an initializer whose audit events are attributed to the Web interface.
    /// </summary>
    /// <returns>A Web-attributed workspace initializer.</returns>
    public static WorkspaceInitializer ForWeb()
    {
        return new WorkspaceInitializer(WorkspaceActors.Web);
    }

    /// <summary>
    /// Applies the canonical directories and seed documents, then records successful completion.
    /// </summary>
    /// <param name="rootPath">The workspace root, normalized to an absolute path.</param>
    /// <param name="cancellationToken">The token used to cancel seed writes and audit recording.</param>
    /// <returns>A task that completes after scaffolding and auditing finish.</returns>
    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);
        if (Directory.Exists(paths.AgentPath))
        {
            File.Delete(paths.WorkspaceInitializationMarkerPath);
        }
        await _scaffolder.ApplyAsync(paths, WorkspaceDefaults.GetDirectories(paths), WorkspaceDefaults.GetSeedFiles(paths), _actor, cancellationToken);
        await WorkspaceInitializationCompletion.WriteAsync(paths.WorkspaceInitializationMarkerPath, cancellationToken);
    }
}
