using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Workspace;

public sealed class WorkspaceInitializer : IWorkspaceInitializer
{
    public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);

        foreach (var directory in WorkspaceDefaults.GetDirectories(paths))
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var file in WorkspaceDefaults.GetSeedFiles(paths))
        {
            await WriteSeedFileAsync(file, cancellationToken);
        }

        var audit = new AuditLog(paths);
        await audit.AppendAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Cli,
            action: AuditSchema.Actions.WorkspaceInit,
            target: paths.RootPath,
            outcome: AuditSchema.Outcomes.Succeeded,
            detail: "Initialized or refreshed EmbodySense workspace scaffolding.",
            metadata: new Dictionary<string, object?>
            {
                ["agent_path"] = paths.AgentPath,
                ["audit_path"] = paths.AuditPath,
                ["permissions_path"] = paths.PermissionsPath,
                ["workspace_path"] = paths.WorkspacePath
            }), cancellationToken);
    }

    private static async Task WriteSeedFileAsync(WorkspaceSeedFile file, CancellationToken cancellationToken)
    {
        if (!file.Overwrite && File.Exists(file.Path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(file.Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(file.Path, file.Content, cancellationToken);
    }
}
