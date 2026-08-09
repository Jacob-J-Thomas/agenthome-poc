using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>
/// Applies the canonical version-one workspace scaffold through a Core.Startup facade.
/// </summary>
/// <remarks>
/// The initializer attributes its completion audit event to the configured interface actor.
/// Root preparation, built-in catalog seeding, and scaffolding are ordered but non-transactional: existing protected state
/// is preserved, while cancellation, catalog, I/O, and audit failures propagate after any earlier changes remain in place.
/// Successful initialization is audited only after catalog seeding and the remaining scaffold both complete.
/// </remarks>
public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    private readonly WorkspaceScaffolder _scaffolder;
    private readonly BuiltInCapabilityCatalogSeeder _capabilitySeeder;
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
    public WorkspaceInitializer(WorkspaceScaffolder scaffolder, string actor = WorkspaceActors.Web) : this(scaffolder, new BuiltInCapabilityCatalogSeeder(), actor)
    {
    }

    /// <summary>Creates an initializer over explicit persistence and capability-seeding components.</summary>
    /// <param name="scaffolder">The persistence component that performs ordered scaffold writes.</param>
    /// <param name="capabilitySeeder">The Startup-owned built-in capability seeder.</param>
    /// <param name="actor">The nonblank actor recorded for successful initialization.</param>
    public WorkspaceInitializer(WorkspaceScaffolder scaffolder, BuiltInCapabilityCatalogSeeder capabilitySeeder, string actor = WorkspaceActors.Web)
    {
        ArgumentNullException.ThrowIfNull(scaffolder);
        ArgumentNullException.ThrowIfNull(capabilitySeeder);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        _scaffolder = scaffolder;
        _capabilitySeeder = capabilitySeeder;
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

    /// <summary>Creates an initializer over one explicit server-owned file trust root.</summary>
    /// <param name="trustRootPath">The server-owned root kept outside mutable workspace storage.</param>
    /// <param name="actor">The nonblank actor recorded for successful initialization.</param>
    /// <returns>An initializer whose capability seeder uses only the supplied trust root.</returns>
    public static WorkspaceInitializer ForFileCapabilityTrustRoot(string trustRootPath, string actor = WorkspaceActors.Web)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRootPath);
        var seeder = new BuiltInCapabilityCatalogSeeder(new FileCapabilityCatalogTrustProvider(trustRootPath));
        return new WorkspaceInitializer(new WorkspaceScaffolder(), seeder, actor);
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
        cancellationToken.ThrowIfCancellationRequested();
        _capabilitySeeder.RequireDisjointTrustRoot(paths.RootPath);
        Directory.CreateDirectory(paths.RootPath);
        if (Directory.Exists(paths.AgentPath))
        {
            File.Delete(paths.WorkspaceInitializationMarkerPath);
        }
        await _capabilitySeeder.SeedAsync(paths, cancellationToken);
        await _scaffolder.ApplyAsync(paths, WorkspaceDefaults.GetDirectories(paths), WorkspaceDefaults.GetSeedFiles(paths), _actor, cancellationToken);
        await WorkspaceInitializationCompletion.WriteAsync(paths.WorkspaceInitializationMarkerPath, cancellationToken);
    }
}
