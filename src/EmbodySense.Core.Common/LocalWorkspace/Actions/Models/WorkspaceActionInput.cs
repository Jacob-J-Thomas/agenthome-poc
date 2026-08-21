namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains one bounded semantic workspace action input with no trusted host-derived evidence.</summary>
/// <param name="SchemaVersion">The only supported schema version.</param>
/// <param name="Kind">The server-selected action kind.</param>
/// <param name="ScopeId">The statically admitted scope identifier.</param>
/// <param name="Target">The exact normalized relative file target.</param>
/// <param name="Precondition">The exact optimistic precondition.</param>
/// <param name="Segments">The ordered semantic content segments captured by contract parsing.</param>
public sealed record WorkspaceActionInput(
    int SchemaVersion,
    WorkspaceActionKind Kind,
    WorkspaceActionScopeId ScopeId,
    WorkspaceRelativeFileTarget Target,
    WorkspaceActionPrecondition Precondition,
    IReadOnlyList<WorkspaceActionContentSegment> Segments);
