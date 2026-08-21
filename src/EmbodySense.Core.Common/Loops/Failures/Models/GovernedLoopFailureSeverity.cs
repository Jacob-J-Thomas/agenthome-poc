namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Identifies the bounded severity of one canonical failure.</summary>
public enum GovernedLoopFailureSeverity
{
    /// <summary>An undefined severity.</summary>
    Unknown = 0,
    /// <summary>A normal failed activation.</summary>
    Error,
    /// <summary>A terminal or authority-owned failure.</summary>
    Critical,
    /// <summary>An ambiguity or integrity failure that requires review.</summary>
    ReviewBlocked,
}
