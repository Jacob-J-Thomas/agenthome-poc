using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Represents a custom loop create operation lookup result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Definition">The definition.</param>
/// <param name="OperationIntegrity">The operation integrity.</param>
public sealed record CustomLoopCreateOperationLookupResult(
    CustomLoopCreateOperationLookupStatus Status,
    CustomLoopDefinition? Definition,
    CustomLoopOperationIntegrity OperationIntegrity)
{
    /// <summary>
    /// Creates a custom loop create operation lookup result representing not found.
    /// </summary>
    /// <returns>The custom loop create operation lookup result.</returns>
    public static CustomLoopCreateOperationLookupResult NotFound() => new(CustomLoopCreateOperationLookupStatus.NotFound, null, CustomLoopOperationIntegrity.NotTracked);

    /// <summary>
    /// Creates a custom loop create operation lookup result representing pending definition commit.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The custom loop create operation lookup result.</returns>
    public static CustomLoopCreateOperationLookupResult PendingDefinitionCommit(CustomLoopDefinition definition) => new(CustomLoopCreateOperationLookupStatus.PendingDefinitionCommit, definition, CustomLoopOperationIntegrity.PendingOutcomeAudit);

    /// <summary>
    /// Creates a custom loop create operation lookup result representing committed.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="integrity">The integrity.</param>
    /// <returns>The custom loop create operation lookup result.</returns>
    public static CustomLoopCreateOperationLookupResult Committed(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity) => new(CustomLoopCreateOperationLookupStatus.Committed, definition, integrity);
}
