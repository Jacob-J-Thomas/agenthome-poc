using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Defines the three exact schema-1 graph Action descriptors backed by the workspace actuator catalog.</summary>
public static class WorkspaceActionNodeDescriptors
{
    /// <summary>Gets the exact workspace append Action descriptor.</summary>
    public static GovernedLoopNodeDescriptor Append { get; } = Create(WorkspaceActionKind.Append);

    /// <summary>Gets the exact workspace write Action descriptor.</summary>
    public static GovernedLoopNodeDescriptor Write { get; } = Create(WorkspaceActionKind.Write);

    /// <summary>Gets the exact recoverable workspace delete Action descriptor.</summary>
    public static GovernedLoopNodeDescriptor Delete { get; } = Create(WorkspaceActionKind.Delete);

    /// <summary>Resolves an exact descriptor to its closed workspace action kind.</summary>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out WorkspaceActionKind kind)
    {
        kind = descriptor switch
        {
            not null when Equals(descriptor, Append) => WorkspaceActionKind.Append,
            not null when Equals(descriptor, Write) => WorkspaceActionKind.Write,
            not null when Equals(descriptor, Delete) => WorkspaceActionKind.Delete,
            _ => WorkspaceActionKind.Unknown,
        };
        return kind != WorkspaceActionKind.Unknown;
    }

    private static GovernedLoopNodeDescriptor Create(WorkspaceActionKind kind)
        => new(GovernedLoopNodeKind.Action, WorkspaceActionOperationIds.For(kind), 1);
}
