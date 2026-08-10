namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies a value-free fail-closed contextual-role instruction-source posture.</summary>
public enum ContextualRoleInstructionSourceProbeStatus
{
    /// <summary>An undefined posture that valid probes never produce.</summary>
    Unknown = 0,
    /// <summary>The registered source is a stable bounded regular UTF-8 text file.</summary>
    Ready = 1,
    /// <summary>The registered source is absent.</summary>
    Missing = 2,
    /// <summary>The source kind or opaque identity is not server-registered.</summary>
    Unsupported = 3,
    /// <summary>The source exceeds the server-owned byte bound.</summary>
    Oversized = 4,
    /// <summary>The source or its retained path was a link, reparse point, directory, hard link, or replaced object.</summary>
    Substituted = 5,
    /// <summary>The source could not be interpreted as one stable non-empty UTF-8 instruction document.</summary>
    Ambiguous = 6,
    /// <summary>The source could not be inspected because its physical boundary was unavailable.</summary>
    Unavailable = 7,
    /// <summary>The exact revision is not currently eligible, so its source was not loaded.</summary>
    Ineligible = 8,
    /// <summary>The exact revision does not apply to the bound workspace, so its source was not loaded.</summary>
    WorkspaceMismatch = 9
}
