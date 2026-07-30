using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Workspace.Models;
using EmbodySense.Core.Persistence.Audit;

namespace EmbodySense.Core.Persistence.Workspace;

/// <summary>
/// Creates the requested workspace directories and seed files, then records the initialization audit event.
/// </summary>
/// <remarks>
/// Seed files honor their individual overwrite policy. The sequence is not a multi-file transaction: an I/O or audit failure
/// may leave already-created scaffolding in place, and the caller receives the original exception.
/// </remarks>
public sealed class WorkspaceScaffolder
{
    /// <summary>
    /// Applies the supplied scaffold in input order and audits successful completion.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="directories">The directories.</param>
    /// <param name="seedFiles">The seed files.</param>
    /// <param name="actor">The actor.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ApplyAsync(
        WorkspacePaths paths,
        IReadOnlyList<string> directories,
        IReadOnlyList<WorkspaceSeedFile> seedFiles,
        string actor = AuditSchema.Actors.Web,
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
            actor: actor,
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
