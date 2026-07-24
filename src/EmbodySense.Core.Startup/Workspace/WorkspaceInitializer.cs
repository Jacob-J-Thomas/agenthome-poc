using System.Text.Json;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    private const long MaxMigratedPermissionsUtf8Bytes = 128 * 1024;
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
        await MigratePermissionsAsync(paths, cancellationToken);
        await _scaffolder.ApplyAsync(paths, WorkspaceDefaults.GetDirectories(paths), WorkspaceDefaults.GetSeedFiles(paths), _actor, cancellationToken);
    }

    private static async Task MigratePermissionsAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        var file = new FileInfo(paths.PermissionsPath);
        if (!file.Exists || file.Length < 1 || file.Length > MaxMigratedPermissionsUtf8Bytes)
        {
            return;
        }

        PermissionsDocument? permissions;
        try
        {
            permissions = PermissionsDocument.FromJson(await File.ReadAllTextAsync(paths.PermissionsPath, cancellationToken));
        }
        catch (JsonException)
        {
            return;
        }

        if (permissions?.EnsureToolResponseInspectionApproval() == true)
        {
            await File.WriteAllTextAsync(paths.PermissionsPath, permissions.ToJson() + Environment.NewLine, cancellationToken);
        }
    }
}
