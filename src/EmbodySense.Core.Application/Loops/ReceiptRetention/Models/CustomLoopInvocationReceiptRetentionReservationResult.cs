using EmbodySense.Core.Application.Loops.ReceiptRetention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents a custom loop invocation receipt retention reservation result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Operation">The operation.</param>
public sealed record CustomLoopInvocationReceiptRetentionReservationResult(
    CustomLoopInvocationReceiptRetentionReservationStatus Status,
    CustomLoopInvocationReceiptRetentionOperation? Operation);
