using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Audit.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;

namespace EmbodySense.Core.Persistence.Workspace;

public sealed class WorkspaceScaffolder
{
    public async Task ApplyAsync(
        WorkspacePaths paths,
        IReadOnlyList<string> directories,
        IReadOnlyList<WorkspaceSeedFile> seedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(seedFiles);

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var file in seedFiles)
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
