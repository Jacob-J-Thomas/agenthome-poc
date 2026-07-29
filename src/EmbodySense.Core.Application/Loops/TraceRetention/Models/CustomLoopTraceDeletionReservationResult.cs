namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

public sealed record CustomLoopTraceDeletionReservationResult(
    CustomLoopTraceDeletionReservationStatus Status,
    CustomLoopTraceDeletionOperation? Operation);
