namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines closed worker-selection and lease bounds.</summary>
public static class TriggerWorkerLimits
{
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

    /// <summary>Gets the maximum bounded lease renewal count.</summary>
    public const int MaxLeaseRenewals = 16;

    /// <summary>Gets the minimum lease duration.</summary>
    public static TimeSpan MinLeaseDuration { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum lease duration.</summary>
    public static TimeSpan MaxLeaseDuration { get; } = TimeSpan.FromMinutes(5);
}
