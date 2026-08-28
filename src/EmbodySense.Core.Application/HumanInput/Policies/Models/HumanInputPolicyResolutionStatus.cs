namespace EmbodySense.Core.Application.HumanInput.Policies.Models;

/// <summary>Identifies the closed fail-closed outcome of Human Input timeout and failure policy resolution.</summary>
public enum HumanInputPolicyResolutionStatus
{
    /// <summary>No supported resolution outcome was supplied.</summary>
    Unknown = 0,

    /// <summary>Both exact policy revisions resolved under one trusted UTC instant.</summary>
    Resolved = 1,

    /// <summary>The request or policy artifacts were malformed or unsupported.</summary>
    Invalid = 2,

    /// <summary>One exact policy revision is absent.</summary>
    NotFound = 3,

    /// <summary>A returned policy artifact does not exactly match its requested identity.</summary>
    Divergent = 4,

    /// <summary>The returned artifacts have a wrong timeout or failure policy kind.</summary>
    WrongKind = 5,

    /// <summary>The returned policy scope or actor attribution does not match server-derived resolution coordinates.</summary>
    ScopeMismatch = 6,

    /// <summary>The trusted time source or policy source could not safely prove resolution.</summary>
    Unavailable = 7,
}
