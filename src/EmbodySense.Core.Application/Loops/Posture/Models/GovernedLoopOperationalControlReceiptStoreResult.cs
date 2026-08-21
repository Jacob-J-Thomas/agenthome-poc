using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Returns one closed receipt-store outcome and current durable receipt.</summary>
public sealed record GovernedLoopOperationalControlReceiptStoreResult(
    GovernedLoopOperationalControlReceiptStoreStatus Status,
    GovernedLoopOperationalControlReceipt? Receipt,
    IGovernedLoopOperationalControlLease? Lease = null);
