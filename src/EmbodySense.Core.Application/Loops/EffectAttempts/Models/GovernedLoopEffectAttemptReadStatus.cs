namespace EmbodySense.Core.Application.Loops.EffectAttempts.Models;

/// <summary>Identifies a closed, non-mutating effect-attempt head observation.</summary>
public enum GovernedLoopEffectAttemptReadStatus
{
    /// <summary>No supported read posture was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact detached current canonical head was returned.</summary>
    Current = 1,

    /// <summary>No durable effect attempt exists for the exact operation generation.</summary>
    Missing = 2,

    /// <summary>Retained effect evidence was malformed, incomplete, disconnected, or otherwise unsafe.</summary>
    Corrupt = 3,

    /// <summary>The canonical effect source could not complete the bounded read.</summary>
    Unavailable = 4,
}
