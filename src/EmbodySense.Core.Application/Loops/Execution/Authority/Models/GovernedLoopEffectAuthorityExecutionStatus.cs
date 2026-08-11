namespace EmbodySense.Core.Application.Loops.Execution.Authority.Models;

/// <summary>Identifies whether one boundary request produced and durably recorded a valid authority decision.</summary>
public enum GovernedLoopEffectAuthorityExecutionStatus
{
    /// <summary>No supported outcome was selected.</summary>
    Unknown = 0,
    /// <summary>A valid authority decision was durably recorded.</summary>
    Decided = 1,
    /// <summary>The request or retained admission evidence was malformed or inconsistent.</summary>
    InvalidRequest = 2,
    /// <summary>Trusted time or another prerequisite prevented construction of a durable decision.</summary>
    AuthorityUnavailable = 3,
    /// <summary>The authority decision could not be durably recorded.</summary>
    EvidenceRejected = 4,
}
