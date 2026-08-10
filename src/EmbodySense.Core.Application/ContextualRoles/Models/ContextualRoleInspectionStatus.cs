namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the closed result of exact contextual-role and source inspection.</summary>
public enum ContextualRoleInspectionStatus
{
    /// <summary>An undefined result that valid services never produce.</summary>
    Unknown = 0,
    /// <summary>The exact current role revision and its registered source are ready for later separate admission.</summary>
    Ready = 1,
    /// <summary>The exact role revision does not exist.</summary>
    NotFound = 2,
    /// <summary>The supplied revision identity or hash is no longer the exact current role revision.</summary>
    Stale = 3,
    /// <summary>The request is outside the bounded exact-revision contract.</summary>
    Invalid = 4,
    /// <summary>The role revision or lifecycle is not currently eligible.</summary>
    Ineligible = 5,
    /// <summary>The role does not apply to the bound workspace.</summary>
    WorkspaceMismatch = 6,
    /// <summary>The registered instruction source is absent.</summary>
    SourceMissing = 7,
    /// <summary>The instruction-source kind or opaque identity is not registered.</summary>
    SourceUnsupported = 8,
    /// <summary>The instruction source exceeds the server-owned bound.</summary>
    SourceOversized = 9,
    /// <summary>The instruction source or retained path was substituted.</summary>
    SourceSubstituted = 10,
    /// <summary>A required persistence or source boundary was unavailable.</summary>
    Unavailable = 11,
    /// <summary>Exact durable or physical evidence could not be proved consistently.</summary>
    Ambiguous = 12
}
