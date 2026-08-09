using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Calculates finite worker-lease renewal budgets from the governed ownership horizon.</summary>
public static class TriggerWorkerLeaseRenewalPolicy
{
    /// <summary>Gets the persisted safety cap for renewals at the worker's half-lease cadence.</summary>
    /// <param name="leaseDuration">The validated lease duration used for every renewal by the worker.</param>
    /// <returns>A finite count derived by ceiling-dividing the maximum ownership horizon by the half-lease interval.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leaseDuration"/> is outside the supported lease range.</exception>
    public static int GetMaxLeaseRenewals(TimeSpan leaseDuration)
    {
        if (leaseDuration < TriggerWorkerLimits.MinLeaseDuration || leaseDuration > TriggerWorkerLimits.MaxLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var renewalIntervalTicks = Math.Max(1, leaseDuration.Ticks / 2);
        var ownershipTicks = TriggerWorkerLimits.MaxLeaseOwnershipDuration.Ticks;
        return checked((int)((ownershipTicks + renewalIntervalTicks - 1) / renewalIntervalTicks));
    }
}
