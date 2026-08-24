namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies one closed conclusive process outcome.</summary>
public enum CommandActionOutcomeKind
{
    /// <summary>No conclusive outcome was observed.</summary>
    Unknown = 0,
    /// <summary>The process exited zero with one valid structured result.</summary>
    Succeeded = 1,
    /// <summary>The process exited with a nonzero code.</summary>
    NonZeroExit = 2,
    /// <summary>The process exited zero but its result violated the declared shape.</summary>
    MalformedResult = 3,
    /// <summary>A standard stream contained invalid UTF-8.</summary>
    InvalidEncoding = 4,
    /// <summary>The combined standard-stream byte ceiling was exceeded.</summary>
    OutputLimitExceeded = 5,
    /// <summary>The execution timeout expired and the complete tree was proved terminal.</summary>
    TimedOut = 6,
    /// <summary>Cancellation was requested and the complete tree was proved terminal.</summary>
    Cancelled = 7,

    /// <summary>The registered isolation adapter affirmatively rejected launch before creating child code.</summary>
    IsolationRejected = 8,
}
