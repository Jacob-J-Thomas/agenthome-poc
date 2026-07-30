using EmbodySense.Core.Application.Loops.Models;
namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Represents a custom loop definition mutation lookup result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Operation">The operation.</param>
public sealed record CustomLoopDefinitionMutationLookupResult(CustomLoopDefinitionMutationLookupStatus Status, CustomLoopDefinitionMutationOperation? Operation)
{
    /// <summary>
    /// Creates a custom loop definition mutation lookup result representing not found.
    /// </summary>
    /// <returns>The custom loop definition mutation lookup result.</returns>
    public static CustomLoopDefinitionMutationLookupResult NotFound() => new(CustomLoopDefinitionMutationLookupStatus.NotFound, null);

    /// <summary>
    /// Creates a custom loop definition mutation lookup result representing found.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The custom loop definition mutation lookup result.</returns>
    public static CustomLoopDefinitionMutationLookupResult Found(CustomLoopDefinitionMutationOperation operation)
    {
        var status = operation.State == CustomLoopDefinitionMutationState.PendingMutation ? CustomLoopDefinitionMutationLookupStatus.PendingMutation : CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted;
        return new CustomLoopDefinitionMutationLookupResult(status, operation);
    }
}
