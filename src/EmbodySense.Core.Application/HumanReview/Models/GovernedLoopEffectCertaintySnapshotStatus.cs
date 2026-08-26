namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies one closed read-only current-effect-certainty source outcome.</summary>
public enum GovernedLoopEffectCertaintySnapshotStatus
{
    /// <summary>No supported source outcome was supplied.</summary>
    Unknown = 0,

    /// <summary>A detached exact current snapshot was returned.</summary>
    Current = 1,

    /// <summary>No durable effect attempt exists for the exact identity.</summary>
    Missing = 2,

    /// <summary>Canonical retained effect evidence was malformed, forward-versioned, or otherwise unsafe.</summary>
    Corrupt = 3,

    /// <summary>The canonical source could not complete the read.</summary>
    Unavailable = 4,

    /// <summary>The source found an attempt but its identity, preparation, authority, phase, or freshness no longer matches the query.</summary>
    Stale = 5,
}
