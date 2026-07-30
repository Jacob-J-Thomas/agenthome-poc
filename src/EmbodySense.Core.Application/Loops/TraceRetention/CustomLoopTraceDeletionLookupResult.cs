using EmbodySense.Core.Application.Loops.TraceRetention.Models;
namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Represents a custom loop trace deletion lookup result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Operation">The operation.</param>
public sealed record CustomLoopTraceDeletionLookupResult(CustomLoopTraceDeletionLookupStatus Status, CustomLoopTraceDeletionOperation? Operation)
{
    /// <summary>
    /// Creates a custom loop trace deletion lookup result representing not found.
    /// </summary>
    /// <returns>The custom loop trace deletion lookup result.</returns>
    public static CustomLoopTraceDeletionLookupResult NotFound() => new(CustomLoopTraceDeletionLookupStatus.NotFound, null);

    /// <summary>
    /// Creates a custom loop trace deletion lookup result representing found.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The custom loop trace deletion lookup result.</returns>
    public static CustomLoopTraceDeletionLookupResult Found(CustomLoopTraceDeletionOperation operation)
    {
        var status = operation.State == CustomLoopTraceDeletionOperationState.PendingMutation
            ? CustomLoopTraceDeletionLookupStatus.PendingMutation
            : CustomLoopTraceDeletionLookupStatus.OutcomeCommitted;
        return new CustomLoopTraceDeletionLookupResult(status, operation);
    }
}
