using EmbodySense.Core.Application.Loops.TraceRetention;
namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Represents a custom loop trace deletion reservation result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Operation">The operation.</param>
public sealed record CustomLoopTraceDeletionReservationResult(
    CustomLoopTraceDeletionReservationStatus Status,
    CustomLoopTraceDeletionOperation? Operation);
