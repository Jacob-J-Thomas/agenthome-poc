namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Defines explicit schema-1 bounds for schedule run-admission evidence.</summary>
public static class ScheduleRunAdmissionEvidenceLimits
{
    /// <summary>Gets the maximum retained admission observations for one exact occurrence.</summary>
    public const int MaxAttempts = 32;

    /// <summary>Gets the maximum canonical persisted evidence size.</summary>
    public const int MaxArtifactUtf8Bytes = 256 * 1024;
}
