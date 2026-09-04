namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Defines finite bounds for short-lived Human Input lifecycle candidate preparation.</summary>
public static class HumanInputLifecycleCandidateLimits
{
    /// <summary>The maximum number of server-generated route alternatives returned by one preparation.</summary>
    public const int MaxRerouteOptions = 16;

    /// <summary>The minimum lifetime accepted for a caller-provided candidate registration.</summary>
    public static readonly TimeSpan MinCandidateLifetime = TimeSpan.FromMinutes(1);

    /// <summary>The maximum lifetime of a process-local candidate registration.</summary>
    public static readonly TimeSpan MaxCandidateLifetime = TimeSpan.FromMinutes(15);
}
