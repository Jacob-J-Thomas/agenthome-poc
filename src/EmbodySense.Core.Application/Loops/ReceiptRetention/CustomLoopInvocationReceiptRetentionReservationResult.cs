namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public sealed record CustomLoopInvocationReceiptRetentionReservationResult(
    CustomLoopInvocationReceiptRetentionReservationStatus Status,
    CustomLoopInvocationReceiptRetentionOperation? Operation);
