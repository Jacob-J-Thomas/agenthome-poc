using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Authoring.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Authoring;

/// <summary>
/// Represents a custom loop authoring result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Definition">The definition.</param>
/// <param name="ValidationErrors">The validation errors.</param>
/// <param name="Conflict">The conflict.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopAuthoringResult(
    CustomLoopAuthoringStatus Status,
    CustomLoopDefinition? Definition,
    IReadOnlyList<CustomLoopValidationError> ValidationErrors,
    CustomLoopDefinitionConflict? Conflict,
    string? Detail)
{
    /// <summary>
    /// Gets a value indicating whether the value is committed.
    /// </summary>
    /// <value><see langword="true"/> when the value is committed; otherwise, <see langword="false"/>.</value>
    public bool IsCommitted => Status is CustomLoopAuthoringStatus.Created or CustomLoopAuthoringStatus.Updated or CustomLoopAuthoringStatus.Deleted or CustomLoopAuthoringStatus.Replayed or CustomLoopAuthoringStatus.CommittedWithAuditWarning;

    /// <summary>
    /// Creates a custom loop authoring result representing invalid.
    /// </summary>
    /// <param name="errors">The errors.</param>
    /// <returns>The custom loop authoring result.</returns>
    public static CustomLoopAuthoringResult Invalid(IReadOnlyList<CustomLoopValidationError> errors) => new(CustomLoopAuthoringStatus.Invalid, null, errors, null, "The loop definition is invalid.");

    /// <summary>
    /// Creates a custom loop authoring result representing audit unavailable.
    /// </summary>
    /// <returns>The custom loop authoring result.</returns>
    public static CustomLoopAuthoringResult AuditUnavailable() => new(CustomLoopAuthoringStatus.AuditUnavailable, null, [], null, "The mutation was not attempted because its audit intent could not be recorded.");

    /// <summary>
    /// Creates a custom loop authoring result representing active run.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The custom loop authoring result.</returns>
    public static CustomLoopAuthoringResult ActiveRun(CustomLoopDefinition? definition) => new(CustomLoopAuthoringStatus.ActiveRunExists, definition, [], null, "Finish or cancel the loop's nonterminal run before editing or deleting its definition.");
}
