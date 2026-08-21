using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Maps exact native before state to the only permission class that may authorize its workspace mutation.</summary>
public static class WorkspaceActionPermissionOperation
{
    /// <summary>Returns the exact operation class for one validated workspace action and before-entry kind.</summary>
    public static FileSystemOperation For(WorkspaceActionKind kind, WorkspaceActionEntryKind entryKind)
        => (kind, entryKind) switch
        {
            (WorkspaceActionKind.Append, WorkspaceActionEntryKind.Absent) => FileSystemOperation.Create,
            (WorkspaceActionKind.Append, WorkspaceActionEntryKind.RegularFile) => FileSystemOperation.Append,
            (WorkspaceActionKind.Write, WorkspaceActionEntryKind.Absent) => FileSystemOperation.Create,
            (WorkspaceActionKind.Write, WorkspaceActionEntryKind.RegularFile) => FileSystemOperation.Modify,
            (WorkspaceActionKind.Delete, WorkspaceActionEntryKind.RegularFile) => FileSystemOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(entryKind), "The workspace action and retained entry kind do not form one supported permission class."),
        };
}
