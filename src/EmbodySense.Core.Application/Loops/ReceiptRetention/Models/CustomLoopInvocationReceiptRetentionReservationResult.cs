using EmbodySense.Core.Application.Loops.ReceiptRetention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

public sealed record CustomLoopInvocationReceiptRetentionReservationResult(
    CustomLoopInvocationReceiptRetentionReservationStatus Status,
    CustomLoopInvocationReceiptRetentionOperation? Operation);
