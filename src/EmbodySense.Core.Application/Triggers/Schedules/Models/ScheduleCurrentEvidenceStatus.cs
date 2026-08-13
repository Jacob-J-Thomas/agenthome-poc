namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Defines closed current-evidence resolution outcomes.</summary>
public enum ScheduleCurrentEvidenceStatus
{
    /// <summary>No recognized resolution was returned.</summary>
    Unknown = 0,
    /// <summary>Every required current evidence item was resolved.</summary>
    Available = 1,
    /// <summary>The exact combined permission was denied.</summary>
    PermissionDenied = 2,
    /// <summary>The governed target could not be resolved.</summary>
    TargetUnavailable = 3,
    /// <summary>The exact adapter could not be resolved.</summary>
    AdapterUnavailable = 4,
    /// <summary>The exact actor or scope could not be resolved.</summary>
    ActorUnavailable = 5,
    /// <summary>Fresh authority-profile evidence could not be resolved.</summary>
    AuthorityUnavailable = 6,
    /// <summary>Recurrence invocation is not currently permitted.</summary>
    RecurrenceDenied = 7,
    /// <summary>The governed payload bytes could not be resolved.</summary>
    PayloadUnavailable = 8,
    /// <summary>The evidence source was unavailable.</summary>
    Unavailable = 9,
    /// <summary>Returned evidence failed integrity validation.</summary>
    Corrupt = 10,
    /// <summary>The evidence source refused the read under bounded load.</summary>
    Backpressured = 11,
}
