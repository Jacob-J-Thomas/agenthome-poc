using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines closed worker-selection and lease bounds.</summary>
public static class TriggerWorkerLimits
{
    private const long MinLeaseDurationTicks = TimeSpan.TicksPerSecond;
    private const long MaxLeaseDurationTicks = 5 * TimeSpan.TicksPerMinute;
    private const long MaxRunExecutionTicks = CustomLoopLimits.MaxRunExecutionMilliseconds * TimeSpan.TicksPerMillisecond;
    private const long MaxLeaseOwnershipTicks = MaxRunExecutionTicks + (2 * MaxLeaseDurationTicks);

    /// <summary>Gets the maximum worker identity length.</summary>
    public const int MaxWorkerIdCharacters = 96;

    /// <summary>Gets the maximum operation identity length.</summary>
    public const int MaxOperationIdCharacters = 160;

    /// <summary>Gets the maximum governed run identity length.</summary>
    public const int MaxGovernedRunIdCharacters = 120;

    /// <summary>Gets the maximum persisted outcome detail length.</summary>
    public const int MaxOutcomeDetailCharacters = 512;

    /// <summary>Gets the maximum fairness-history length accepted per selection.</summary>
    public const int MaxRecentLoopIds = 32;

    /// <summary>Gets the maximum persisted lease renewal count across every supported lease duration.</summary>
    public const int MaxLeaseRenewals = (int)((MaxLeaseOwnershipTicks + (MinLeaseDurationTicks / 2) - 1) / (MinLeaseDurationTicks / 2));

    /// <summary>Gets the minimum lease duration.</summary>
    public static TimeSpan MinLeaseDuration { get; } = TimeSpan.FromTicks(MinLeaseDurationTicks);

    /// <summary>Gets the maximum lease duration.</summary>
    public static TimeSpan MaxLeaseDuration { get; } = TimeSpan.FromTicks(MaxLeaseDurationTicks);

    /// <summary>
    /// Gets the maximum ownership horizon: one maximum lease to record dispatch intent, the maximum governed run, and one final maximum lease tail.
    /// </summary>
    public static TimeSpan MaxLeaseOwnershipDuration { get; } = TimeSpan.FromTicks(MaxLeaseOwnershipTicks);
}
