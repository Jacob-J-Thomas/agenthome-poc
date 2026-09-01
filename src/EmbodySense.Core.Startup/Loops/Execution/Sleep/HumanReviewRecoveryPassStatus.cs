namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Describes the closed result of one bounded Startup Human Review recovery pass.</summary>
public enum HumanReviewRecoveryPassStatus
{
    /// <summary>No supported pass outcome was produced.</summary>
    Unknown = 0,

    /// <summary>Every requested lane completed a bounded canonical pass.</summary>
    Current = 1,

    /// <summary>A request or canonical evidence page was malformed or corrupt.</summary>
    Invalid = 2,

    /// <summary>A required canonical dependency could not complete its bounded operation.</summary>
    Unavailable = 3,
}
