namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionReservationResult(
    CustomLoopTraceDeletionReservationStatus Status,
    CustomLoopTraceDeletionOperation? Operation);
