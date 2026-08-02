namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies an isolated executable invocation outcome.</summary>
public enum CapabilityExecutableInvocationStatus
{
    /// <summary>The process exited successfully with one bounded JSON result.</summary>
    Succeeded = 1,
    /// <summary>The configured isolated host cannot enforce the declared boundary.</summary>
    Unavailable = 2,
    /// <summary>The process exceeded its declared execution duration.</summary>
    TimedOut = 3,
    /// <summary>The caller cancelled the invocation and the process tree was terminated.</summary>
    Cancelled = 4,
    /// <summary>The process crashed or returned a nonzero exit code.</summary>
    Crashed = 5,
    /// <summary>Standard output or standard error exceeded the declared bound.</summary>
    OutputLimitExceeded = 6,
    /// <summary>The process result was not one complete JSON value.</summary>
    MalformedResult = 7,
    /// <summary>The invocation request was invalid or escaped its artifact root.</summary>
    Invalid = 8
}
