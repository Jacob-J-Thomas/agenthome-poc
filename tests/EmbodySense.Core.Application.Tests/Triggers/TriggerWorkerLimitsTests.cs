using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Tests.Triggers;

public sealed class TriggerWorkerLimitsTests
{
    [Fact]
    public void Renewal_budgets_cover_the_same_bounded_ownership_horizon_at_half_lease_cadence()
    {
        Assert.Equal(TimeSpan.FromMinutes(40), TriggerWorkerLimits.MaxLeaseOwnershipDuration);
        Assert.Equal(4_800, TriggerWorkerLimits.MaxLeaseRenewals);
        Assert.Equal(4_800, TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(TriggerWorkerLimits.MinLeaseDuration));
        Assert.Equal(80, TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(TimeSpan.FromMinutes(1)));
        Assert.Equal(16, TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(TriggerWorkerLimits.MaxLeaseDuration));

        var sevenSecondLease = TimeSpan.FromSeconds(7);
        var halfLeaseTicks = sevenSecondLease.Ticks / 2;
        var expectedCeiling = (TriggerWorkerLimits.MaxLeaseOwnershipDuration.Ticks + halfLeaseTicks - 1) / halfLeaseTicks;
        Assert.Equal(expectedCeiling, TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(sevenSecondLease));
    }

    [Fact]
    public void Minimum_lease_half_cadence_covers_a_latest_start_maximum_governed_run_before_count_loss()
    {
        var halfLeaseTicks = TriggerWorkerLimits.MinLeaseDuration.Ticks / 2;
        var latestGovernedCompletionTicks = TriggerWorkerLimits.MaxLeaseDuration.Ticks + (CustomLoopLimits.MaxRunExecutionMilliseconds * TimeSpan.TicksPerMillisecond);
        var renewalsThroughLatestCompletion = (latestGovernedCompletionTicks + halfLeaseTicks - 1) / halfLeaseTicks;

        Assert.Equal(4_200, renewalsThroughLatestCompletion);
        Assert.True(renewalsThroughLatestCompletion < TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(TriggerWorkerLimits.MinLeaseDuration));
        Assert.Equal(TriggerWorkerLimits.MaxLeaseDuration, TriggerWorkerLimits.MaxLeaseOwnershipDuration - TimeSpan.FromTicks(latestGovernedCompletionTicks));
    }

    [Fact]
    public void Renewal_budget_rejects_durations_outside_the_closed_supported_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(TriggerWorkerLimits.MinLeaseDuration - TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(TriggerWorkerLimits.MaxLeaseDuration + TimeSpan.FromTicks(1)));
    }
}
