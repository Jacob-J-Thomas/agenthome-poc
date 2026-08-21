using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Defines the complete schema-1 workspace actuator operation catalog.</summary>
public static class WorkspaceActionOperationIds
{
    /// <summary>Gets the exact append operation identifier.</summary>
    public const string Append = "workspace.append.v1";

    /// <summary>Gets the exact write operation identifier.</summary>
    public const string Write = "workspace.write.v1";

    /// <summary>Gets the exact recoverable-delete operation identifier.</summary>
    public const string Delete = "workspace.delete.v1";

    /// <summary>Maps one action to its exact operation identifier.</summary>
    public static string For(WorkspaceActionKind kind)
        => kind switch
        {
            WorkspaceActionKind.Append => Append,
            WorkspaceActionKind.Write => Write,
            WorkspaceActionKind.Delete => Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "The workspace action kind is unsupported."),
        };

    /// <summary>Parses one exact operation identifier.</summary>
    public static bool TryParse(string? value, out WorkspaceActionKind kind)
    {
        kind = value switch
        {
            Append => WorkspaceActionKind.Append,
            Write => WorkspaceActionKind.Write,
            Delete => WorkspaceActionKind.Delete,
            _ => WorkspaceActionKind.Unknown,
        };
        return kind != WorkspaceActionKind.Unknown;
    }
}
