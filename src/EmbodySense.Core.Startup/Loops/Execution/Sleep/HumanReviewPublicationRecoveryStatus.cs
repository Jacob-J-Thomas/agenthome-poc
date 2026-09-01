namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Describes the closed result of one bounded wake-less approval publication scan.</summary>
public enum HumanReviewPublicationRecoveryStatus
{
    /// <summary>No supported scan outcome was produced.</summary>
    Unknown = 0,

    /// <summary>The canonical run page was scanned successfully.</summary>
    Current = 1,

    /// <summary>The run page or a reread contained malformed canonical evidence.</summary>
    Invalid = 2,

    /// <summary>The canonical run store or publication boundary was unavailable.</summary>
    Unavailable = 3,
}
